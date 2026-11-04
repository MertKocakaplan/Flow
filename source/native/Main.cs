using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Flow.BrowserIntegration;

namespace FlowNative {
    public static class Program {
        public static int Main(string[] args) {
            // The manifest and host both pin the extension identity. No arbitrary CLI forwarding.
            if (args.Length == 0 || !BrowserProtocol.AllowedOrigin(args[0])) return 2;
            using (var input = Console.OpenStandardInput())
            using (var output = Console.OpenStandardOutput()) {
                while (true) {
                    string request;
                    try { request = BrowserProtocol.Read(input, BrowserProtocol.MaxRequest); } catch (Exception) { return 3; }
                    if (request == null) return 0;
                    string response;
                    try { response = Forward(request).GetAwaiter().GetResult(); }
                    catch (Exception) { response = BrowserProtocol.Error("APP_UNAVAILABLE"); }
                    try { BrowserProtocol.Write(output, response); } catch (IOException) { return 0; }
                }
            }
        }
        static async Task<string> Forward(string request) {
            var pipeName = BrowserProtocol.PipeName(BrowserProtocol.DataDirectory());
            var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BrowserProtocol.ApplicationFile);
            if (!File.Exists(exe)) return BrowserProtocol.Error("APP_MISSING");
            NamedPipeClientStream pipe = null;
            // Do not automatically restart Flow on routine polling after the user has closed it.
            var data = BrowserProtocol.Json().DeserializeObject(request) as System.Collections.Generic.Dictionary<string, object>;
            object launch;
            bool canLaunch = data != null && data.TryGetValue("launch", out launch) && launch is bool && (bool)launch;
            try {
                for (int attempt = 0; attempt < (canLaunch ? 40 : 1); attempt++) {
                    pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try { pipe.Connect(200); break; }
                    catch (TimeoutException) {
                        pipe.Dispose(); pipe = null;
                        if (attempt == 0 && canLaunch) Process.Start(new ProcessStartInfo(exe, "--browser") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
                    }
                    if (canLaunch) await Task.Delay(100).ConfigureAwait(false);
                }
                if (pipe == null || !BrowserProtocol.OwnedByCurrentUser(pipe)) return BrowserProtocol.Error("APP_UNAVAILABLE");
                using (var timeout = new CancellationTokenSource(195000))
                using (timeout.Token.Register(() => { try { pipe.Dispose(); } catch (Exception) {} })) {
                    BrowserProtocol.Write(pipe, request);
                    return await BrowserProtocol.ReadAsync(pipe, BrowserProtocol.MaxResponse, timeout.Token).ConfigureAwait(false);
                }
            } finally { if (pipe != null) pipe.Dispose(); }
        }
    }
}
