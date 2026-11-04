using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Flow {
    public class ComponentEntry {
        public string Name { get; set; }
        public string File { get; set; }
        public string Version { get; set; }
        public string Sha256 { get; set; }
        public string License { get; set; }
        public string Source { get; set; }
        public bool Updatable { get; set; }
    }
    public class ComponentManifest {
        public int SchemaVersion { get; set; }
        public string Package { get; set; }
        public List<ComponentEntry> Components { get; set; }
    }

    public class ComponentStatus {
        public ComponentEntry Entry;
        public bool Present, Runs;
        public string Version, Hash;
        // verified: matches the packaged file; updated: the engine was updated by Flow; changed: differs from the package; missing
        public string State;
        public string Name { get { return Entry.Name; } }
        public string VersionText { get { return Present ? (Version ?? Loc.T("common.unknown")) : "—"; } }
        public string StateText { get { return Loc.T("comp." + State); } }
        public string Kind { get { return State == "verified" ? "done" : State == "updated" ? "active" : "error"; } }
        public string Detail { get { return Entry.Updatable ? Loc.T("comp.updatable") : Loc.T("comp.packaged"); } }
    }

    // yt-dlp.exe cannot be replaced while something runs it. Short reads enter here; an update waits for them
    // to end, and reads that arrive during the update wait for it. Downloads are held by the queue instead.
    public static class EngineGate {
        static readonly object sync = new object();
        static int users;
        static TaskCompletionSource<bool> updating;
        sealed class Lease : IDisposable {
            int done;
            public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) lock (sync) users--; }
        }
        public static bool Updating { get { lock (sync) return updating != null; } }
        public static async Task<IDisposable> Enter(CancellationToken token) {
            while (true) {
                Task wait;
                lock (sync) {
                    if (updating == null) { users++; return new Lease(); }
                    wait = updating.Task;
                }
                await Task.WhenAny(wait, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
        }
        // Throws UpdateBusy (and stays open) when a read has not finished within the wait.
        public static async Task BeginUpdate(int waitMilliseconds) {
            lock (sync) { if (updating != null) throw new FlowException(ErrorCode.UpdateBusy); updating = new TaskCompletionSource<bool>(); }
            var until = DateTime.UtcNow.AddMilliseconds(waitMilliseconds);
            while (true) {
                lock (sync) if (users == 0) return;
                if (DateTime.UtcNow >= until) { EndUpdate(); throw new FlowException(ErrorCode.UpdateBusy); }
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        public static void EndUpdate() {
            TaskCompletionSource<bool> finished;
            lock (sync) { finished = updating; updating = null; }
            if (finished != null) finished.TrySetResult(true);
        }
    }

    public sealed class UpdateResult { public string OldVersion, NewVersion; public bool Changed, RolledBack; public string Reason; }

    // Component versions, integrity against the package manifest, and the engine update with rollback.
    // FFmpeg, ffprobe and Deno are not updated by the engine's own updater; they ship with Flow.
    public static class Components {
        public static ComponentManifest Load(string toolsDir) {
            try {
                var path = Path.Combine(toolsDir, "components.json");
                var m = File.Exists(path) ? Engine.Json().Deserialize<ComponentManifest>(File.ReadAllText(path, Encoding.UTF8)) : null;
                if (m != null && m.Components != null) return m;
            } catch (Exception) {}
            return new ComponentManifest { SchemaVersion = 1, Components = Engine.ToolFiles.Select(f => new ComponentEntry { Name = Path.GetFileNameWithoutExtension(f), File = f, Updatable = f == "yt-dlp.exe" }).ToList() };
        }

        public static string Hash(string path) {
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static string ParseVersion(string file, string output) {
            if (string.IsNullOrWhiteSpace(output)) return null;
            var first = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            Match m;
            if (file == "yt-dlp.exe") { m = Regex.Match(first, @"^\d{4}\.\d{2}\.\d{2}(\.\d+)?"); return m.Success ? m.Value : null; }
            if (file == "ffmpeg.exe" || file == "ffprobe.exe") { m = Regex.Match(first, @"version\s+(\S+)"); return m.Success ? m.Groups[1].Value : null; }
            if (file == "deno.exe") { m = Regex.Match(first, @"^deno\s+(\S+)"); return m.Success ? m.Groups[1].Value : null; }
            return first;
        }
        static string[] VersionArgs(string file) { return file == "yt-dlp.exe" ? new[] { "--version" } : file == "deno.exe" ? new[] { "--version" } : new[] { "-hide_banner", "-version" }; }

        public static async Task<string> RunVersion(string exe, CancellationToken token) {
            try {
                var r = await ProcessRunner.Run(exe, VersionArgs(Path.GetFileName(exe)), null, token, 60);
                return r.ExitCode == 0 ? ParseVersion(Path.GetFileName(exe), r.Output) : null;
            } catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; }
        }

        public static async Task<List<ComponentStatus>> Check(string toolsDir, CancellationToken token) {
            var manifest = Load(toolsDir);
            var list = new List<ComponentStatus>();
            foreach (var entry in manifest.Components) {
                var status = new ComponentStatus { Entry = entry };
                var path = Path.Combine(toolsDir, entry.File);
                status.Present = File.Exists(path);
                if (status.Present) {
                    status.Version = await RunVersion(path, token);
                    status.Runs = status.Version != null;
                    status.Hash = await Task.Run(() => Hash(path), token);
                }
                if (!status.Present || !status.Runs) status.State = "missing";
                else if (string.Equals(status.Hash, entry.Sha256, StringComparison.OrdinalIgnoreCase)) status.State = "verified";
                else if (entry.Updatable && HasBackup(toolsDir)) status.State = "updated";
                else status.State = "changed";
                list.Add(status);
            }
            return list;
        }

        public static string BackupDir(string toolsDir) { return Path.Combine(toolsDir, "backup"); }
        public static bool HasBackup(string toolsDir) { return LatestBackup(toolsDir) != null; }
        public static string LatestBackup(string toolsDir) {
            var dir = BackupDir(toolsDir);
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, "yt-dlp-*.exe").OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
        }

        // Updates yt-dlp with its own verified updater. The previous file is kept; if the new one does not run,
        // fails its self-check, or breaks the reference check that worked before, the previous file is restored.
        public static async Task<UpdateResult> UpdateEngine(string toolsDir, Func<int> activeJobs, Func<string, CancellationToken, Task<bool>> referenceCheck, Action<string> log, CancellationToken token) {
            if (activeJobs() > 0) throw new FlowException(ErrorCode.UpdateBusy);
            var exe = Path.Combine(toolsDir, "yt-dlp.exe");
            if (!File.Exists(exe)) throw new FlowException(ErrorCode.ToolsMissing, "yt-dlp.exe", new[] { "yt-dlp.exe" });
            var result = new UpdateResult { OldVersion = await RunVersion(exe, token) };
            var backupDir = BackupDir(toolsDir);
            Directory.CreateDirectory(backupDir);
            var backup = Path.Combine(backupDir, "yt-dlp-" + (result.OldVersion ?? "unknown") + ".exe");
            File.Copy(exe, backup, true);
            File.SetLastWriteTimeUtc(backup, DateTime.UtcNow);
            bool workedBefore = referenceCheck == null || await referenceCheck(exe, token);
            var run = await RunOrNull(exe, new[] { "--ignore-config", "--no-colors", "--encoding", "utf-8", "--socket-timeout", "25", "--update" }, log, token, 300);
            if (run == null || run.ExitCode != 0) { await Restore(backup, exe); throw new FlowException(ErrorCode.EngineUpdateFailed, run == null ? "timeout" : run.Error + run.Output, new[] { run == null ? "" : Last(run.Error) }); }
            result.NewVersion = await RunVersion(exe, token);
            string failure = null;
            if (result.NewVersion == null) failure = Loc.T("comp.fail.runs");
            else {
                var list = await RunOrNull(exe, new[] { "--ignore-config", "--list-extractors" }, null, token, 120);
                if (list == null || list.ExitCode != 0 || list.Output.Split('\n').Length < 500) failure = Loc.T("comp.fail.selftest");
                else if (workedBefore && referenceCheck != null && !await referenceCheck(exe, token)) failure = Loc.T("comp.fail.reference");
            }
            if (failure != null) {
                await Restore(backup, exe);
                result.RolledBack = true; result.Reason = failure; result.NewVersion = await RunVersion(exe, token);
                return result;
            }
            result.Changed = result.NewVersion != result.OldVersion;
            if (!result.Changed) { try { File.Delete(backup); } catch (IOException) {} }
            // Keep the two most recent backups.
            foreach (var old in Directory.GetFiles(backupDir, "yt-dlp-*.exe").OrderByDescending(f => File.GetLastWriteTimeUtc(f)).Skip(2)) try { File.Delete(old); } catch (IOException) {}
            return result;
        }
        static string Last(string text) { return (text ?? "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? ""; }
        static async Task<ProcessResult> RunOrNull(string exe, string[] args, Action<string> log, CancellationToken token, int seconds) {
            try { return await ProcessRunner.Run(exe, args, log, token, seconds); } catch (TimeoutException) { return null; }
        }
        // The engine or a scanner may hold the file for a moment after exiting, so a locked file is tried again.
        internal static async Task Restore(string backup, string exe) {
            for (int attempt = 0; ; attempt++) {
                try { File.Copy(backup, exe, true); return; }
                catch (IOException) { if (attempt >= 9) throw; }
                catch (UnauthorizedAccessException) { if (attempt >= 9) throw; }
                await Task.Delay(500);
            }
        }

        // Puts back the most recent previous engine, after checking that it runs.
        public static async Task<string> Rollback(string toolsDir, CancellationToken token) {
            var backup = LatestBackup(toolsDir);
            if (backup == null) return null;
            var version = await RunVersion(backup, token);
            if (version == null) throw new FlowException(ErrorCode.EngineUpdateFailed, "backup does not run", new[] { Loc.T("comp.fail.runs") });
            await Restore(backup, Path.Combine(toolsDir, "yt-dlp.exe"));
            File.Delete(backup);
            return version;
        }
    }
}
