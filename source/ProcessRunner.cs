using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Flow {
    public sealed class ProcessResult { public int ExitCode; public string Output; public string Error; }

    public static class ProcessRunner {
        public static async Task<ProcessResult> Run(string exe, IEnumerable<string> args, Action<string> line, CancellationToken token, int timeoutSeconds) {
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(exe, string.Join(" ", args.Select(Engine.Quote))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(exe) };
            var output = new StringBuilder(); var error = new StringBuilder();
            using (var process = new Process { StartInfo = start })
            using (var job = new ChildJob()) {
                process.Start();
                try { job.Attach(process); } catch { try { process.Kill(); } catch {} throw; }
                var stdout = Read(process.StandardOutput, output, line);
                var stderr = Read(process.StandardError, error, line);
                var exited = Task.Run(() => process.WaitForExit());
                using (var timer = new CancellationTokenSource()) {
                    var canceled = Task.Delay(Timeout.Infinite, token);
                    var timeout = Task.Delay(timeoutSeconds * 1000, timer.Token);
                    var winner = await Task.WhenAny(exited, canceled, timeout).ConfigureAwait(false);
                    if (winner != exited) {
                        job.Kill();
                        await exited.ConfigureAwait(false);
                        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException("Process timed out.");
                    }
                    timer.Cancel();
                    await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return new ProcessResult { ExitCode = process.ExitCode, Output = output.ToString(), Error = error.ToString() };
                }
            }
        }
        static async Task Read(StreamReader reader, StringBuilder buffer, Action<string> callback) {
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) {
                // Keep metadata intact; cap diagnostic accumulation for long downloads.
                if (buffer.Length < 28 * 1024 * 1024) buffer.AppendLine(line);
                if (callback != null) callback(line);
            }
        }
    }

    // Closing the job also terminates ffmpeg / Deno subprocesses on cancel, pause or app exit.
    public sealed class ChildJob : IDisposable {
        IntPtr handle;
        [StructLayout(LayoutKind.Sequential)] struct Basic { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] struct Io { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] struct Extended { public Basic BasicLimitInformation; public Io IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job, int type, IntPtr info, uint size);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        public ChildJob() {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            var info = new Extended(); info.BasicLimitInformation.LimitFlags = 0x2000;
            int size = Marshal.SizeOf(info); var ptr = Marshal.AllocHGlobal(size);
            try { Marshal.StructureToPtr(info, ptr, false); if (!SetInformationJobObject(handle, 9, ptr, (uint)size)) { Dispose(); throw new System.ComponentModel.Win32Exception(); } }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        public void Attach(Process process) { if (!AssignProcessToJobObject(handle, process.Handle)) throw new System.ComponentModel.Win32Exception(); }
        public void Kill() { if (handle != IntPtr.Zero) TerminateJobObject(handle, 1); }
        public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    }
}
