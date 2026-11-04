using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Flow.BrowserIntegration;

namespace Flow {
    public sealed class BrowserPipeServer : IDisposable {
        readonly Func<string, CancellationToken, Task<string>> handle;
        readonly string name;
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        readonly HashSet<NamedPipeServerStream> pipes = new HashSet<NamedPipeServerStream>();
        public BrowserPipeServer(string dataDir, Func<string, CancellationToken, Task<string>> handler) { name = BrowserProtocol.PipeName(dataDir); handle = handler; }
        public void Start() { Task.Run((Func<Task>)Listen); }
        async Task Listen() {
            while (!stop.IsCancellationRequested) {
                NamedPipeServerStream pipe = null;
                bool retry = false;
                try {
                    var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
                    acl.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                    // Explicitly deny network logons, even for the same account; this endpoint is local only.
                    acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
                    pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, acl);
                    lock (pipes) { if (stop.IsCancellationRequested) { pipe.Dispose(); return; } pipes.Add(pipe); }
                    await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                    var connected = pipe; pipe = null;
                    // A connection owns one bounded request. Slow inspection cannot block queue polling.
                    Task.Run(() => Serve(connected));
                } catch (OperationCanceledException) { break; }
                catch (Exception) { retry = !stop.IsCancellationRequested; }
                finally { if (pipe != null) { lock (pipes) pipes.Remove(pipe); pipe.Dispose(); } }
                if (retry) await Task.Delay(200).ConfigureAwait(false);
            }
        }
        async Task Serve(NamedPipeServerStream pipe) {
            using (pipe)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token)) {
                timeout.CancelAfter(200000);
                using (timeout.Token.Register(() => { try { pipe.Dispose(); } catch (Exception) {} })) {
                    try {
                        var request = await BrowserProtocol.ReadAsync(pipe, BrowserProtocol.MaxRequest, timeout.Token).ConfigureAwait(false);
                        var response = await handle(request, timeout.Token).ConfigureAwait(false);
                        BrowserProtocol.Write(pipe, response);
                    } catch (Exception) { /* A disconnected browser must not cancel a queued download. */ }
                    finally { lock (pipes) pipes.Remove(pipe); }
                }
            }
        }
        public void Dispose() {
            stop.Cancel(); lock (pipes) { foreach (var p in pipes) p.Dispose(); pipes.Clear(); }
        }
    }

    public partial class Controller {
        BrowserPipeServer browserServer;
        internal BrowserApi Browser;
        public void StartBrowserBridge() {
            Browser = new BrowserApi(queue, () => settings, (url, token) => (InfoSource ?? ((u, t) => EngineExecutor.Inspect(u.Url, u.Platform, 0, t,u.Referrer)))(url, token), () => { BringToFront(); ShowPage("queue"); });
            browserServer = new BrowserPipeServer(paths.DataDir, (json, token) => {
                var completion = new TaskCompletionSource<string>();
                if (Window.Dispatcher.HasShutdownStarted) { completion.SetResult(BrowserProtocol.Error("APP_UNAVAILABLE")); return completion.Task; }
                Window.Dispatcher.BeginInvoke(new Action(async () => {
                    try { completion.TrySetResult(await Browser.Handle(json, token)); }
                    catch (Exception) { completion.TrySetResult(BrowserProtocol.Error("APP_UNAVAILABLE")); }
                }));
                return completion.Task;
            });
            browserServer.Start();
            Window.Closed += (s, e) => browserServer.Dispose();
        }
    }
}
