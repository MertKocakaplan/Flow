using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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

        // Which tool a file is, also for kept copies such as "yt-dlp-2026.08.19.exe".
        public static string ToolKind(string exe) {
            var name = Path.GetFileName(exe ?? "").ToLowerInvariant();
            foreach (var tool in Engine.ToolFiles) if (name == tool || name.StartsWith(Path.GetFileNameWithoutExtension(tool) + "-", StringComparison.Ordinal)) return tool;
            return name;
        }
        public static async Task<string> RunVersion(string exe, CancellationToken token) {
            try {
                var kind = ToolKind(exe);
                var r = await ProcessRunner.Run(exe, VersionArgs(kind), null, token, 60);
                return r.ExitCode == 0 ? ParseVersion(kind, r.Output) : null;
            } catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; }
        }

        public static async Task<List<ComponentStatus>> Check(string toolsDir, CancellationToken token) {
            var manifest = Load(toolsDir);
            // An engine Flow installed itself is recorded with its checksum; any other difference is reported as changed.
            var installed = Installed(toolsDir);
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
                else if (entry.Updatable && installed != null && string.Equals(status.Hash, installed.Sha256, StringComparison.OrdinalIgnoreCase)) status.State = "updated";
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

        // The engine Flow installed itself (null when the packaged engine is in place). Kept outside components.json,
        // which describes the package as shipped.
        public static string InstalledFile(string toolsDir) { return Path.Combine(toolsDir, "engine.json"); }
        public static ComponentEntry Installed(string toolsDir) {
            try { var path = InstalledFile(toolsDir); return File.Exists(path) ? Engine.Json().Deserialize<ComponentEntry>(File.ReadAllText(path, Encoding.UTF8)) : null; }
            catch (Exception) { return null; }
        }
        static void Record(string toolsDir, string exe, string version, string source) {
            var manifest = Load(toolsDir).Components.FirstOrDefault(c => c.File == "yt-dlp.exe");
            var hash = Hash(exe);
            // Back on the packaged file: nothing to record.
            if (manifest != null && string.Equals(manifest.Sha256, hash, StringComparison.OrdinalIgnoreCase)) { try { File.Delete(InstalledFile(toolsDir)); } catch (IOException) {} return; }
            JsonStore.Save(InstalledFile(toolsDir), new ComponentEntry { Name = "yt-dlp", File = "yt-dlp.exe", Version = version, Sha256 = hash, Source = source, Updatable = true });
        }

        // Release numbers are dates ("2026.10.26", nightly "2026.10.26.123456"); compared part by part.
        public static bool IsNewer(string candidate, string current) {
            Version a, b;
            if (!Version.TryParse(candidate ?? "", out a)) return false;
            if (!Version.TryParse(current ?? "", out b)) return true;
            return a > b;
        }
        // "<sha256>  yt-dlp.exe" lines of the release's SHA2-256SUMS file.
        public static string ExpectedHash(string sums, string file) {
            foreach (var line in (sums ?? "").Split('\n')) {
                var m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(\S+)$");
                if (m.Success && m.Groups[2].Value == file) return m.Groups[1].Value.ToLowerInvariant();
            }
            return null;
        }
        public static IEngineSource Source = new GitHubEngineSource();

        // Site changes break an old engine first and show up as these errors. The engine's version is its release
        // date, so its age is known without asking the network.
        public const int StaleDays = 30;
        public static int EngineAgeDays(string version, DateTime todayUtc) {
            var m = Regex.Match(version ?? "", @"^(\d{4})\.(\d{2})\.(\d{2})");
            DateTime released;
            if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out released)) return -1;
            return Math.Max(0, (int)(todayUtc.Date - released.Date).TotalDays);
        }
        public static bool SuggestUpdate(ErrorCode code, string version, DateTime todayUtc) {
            bool engineShaped = code == ErrorCode.Unknown || code == ErrorCode.Forbidden || code == ErrorCode.NoVideo || code == ErrorCode.Unsupported || code == ErrorCode.FormatUnavailable;
            return engineShaped && EngineAgeDays(version, todayUtc) > StaleDays;
        }

        public static Task<UpdateResult> UpdateEngine(string toolsDir, Func<int> activeJobs, Func<string, CancellationToken, Task<bool>> referenceCheck, Action<string> log, CancellationToken token) {
            return UpdateEngine(toolsDir, activeJobs, referenceCheck, log, Source, token);
        }
        // Downloads the published engine for the latest release tag and checks it against that release's SHA-256 list
        // before it replaces anything. The engine's own self-updater is not used: an old executable's certificate store
        // may be out of date. The previous file is kept; if the new one does not run, fails its self-check, or breaks
        // the reference check that worked before, the previous file is restored.
        public static async Task<UpdateResult> UpdateEngine(string toolsDir, Func<int> activeJobs, Func<string, CancellationToken, Task<bool>> referenceCheck, Action<string> log, IEngineSource source, CancellationToken token) {
            if (activeJobs() > 0) throw new FlowException(ErrorCode.UpdateBusy);
            var exe = Path.Combine(toolsDir, "yt-dlp.exe");
            if (!File.Exists(exe)) throw new FlowException(ErrorCode.ToolsMissing, "yt-dlp.exe", new[] { "yt-dlp.exe" });
            var result = new UpdateResult { OldVersion = await RunVersion(exe, token) };
            EngineRelease release;
            try { release = await source.Latest(token); }
            catch (OperationCanceledException) { throw; }
            catch (FlowException) { throw; }
            catch (Exception ex) { throw new FlowException(ErrorCode.EngineUpdateFailed, ex.Message, new[] { "comp.fail.network" }); }
            if (!IsNewer(release.Tag, result.OldVersion)) { result.NewVersion = result.OldVersion; return result; }

            // Staged inside the tools folder so the final copy stays on the same disk.
            var stage = Path.Combine(toolsDir, "update");
            var staged = Path.Combine(stage, "yt-dlp.exe");
            try {
                Directory.CreateDirectory(stage);
                foreach (var f in Directory.GetFiles(stage)) File.Delete(f);
                var sumsFile = Path.Combine(stage, "SHA2-256SUMS");
                await Fetch(source, release.SumsUrl, sumsFile, 64 * 1024, token);
                var expected = ExpectedHash(File.ReadAllText(sumsFile, Encoding.UTF8), "yt-dlp.exe");
                if (expected == null) throw new FlowException(ErrorCode.EngineUpdateFailed, "no yt-dlp.exe line in SHA2-256SUMS", new[] { "comp.fail.checksum" });
                if (log != null) log("Flow: downloading yt-dlp " + release.Tag);
                await Fetch(source, release.ExeUrl, staged, 300L * 1024 * 1024, token);
                var actual = await Task.Run(() => Hash(staged), token);
                if (actual != expected) throw new FlowException(ErrorCode.EngineUpdateFailed, "checksum " + actual + " != " + expected, new[] { "comp.fail.checksum" });
            } catch (Exception) { TryClear(stage); throw; }

            var backupDir = BackupDir(toolsDir);
            Directory.CreateDirectory(backupDir);
            var backup = Path.Combine(backupDir, "yt-dlp-" + (result.OldVersion ?? "unknown") + ".exe");
            File.Copy(exe, backup, true);
            File.SetLastWriteTimeUtc(backup, DateTime.UtcNow);
            var before = Installed(toolsDir);
            bool workedBefore = referenceCheck == null || await referenceCheck(exe, token);
            Exception replaceError = null;
            try { await Restore(staged, exe); } catch (Exception ex) { replaceError = ex; }
            TryClear(stage);
            if (replaceError != null) { await Restore(backup, exe); throw new FlowException(ErrorCode.EngineUpdateFailed, replaceError.Message, new[] { "comp.fail.runs" }); }
            result.NewVersion = await RunVersion(exe, token);
            string failure = null;
            if (result.NewVersion == null) failure = Loc.T("comp.fail.runs");
            else if (result.NewVersion != release.Tag) failure = Loc.T("comp.fail.version");
            else {
                var list = await RunOrNull(exe, new[] { "--ignore-config", "--list-extractors" }, null, token, 120);
                if (list == null || list.ExitCode != 0 || list.Output.Split('\n').Length < 500) failure = Loc.T("comp.fail.selftest");
                else if (workedBefore && referenceCheck != null && !await referenceCheck(exe, token)) failure = Loc.T("comp.fail.reference");
            }
            if (failure != null) {
                await Restore(backup, exe);
                if (before == null) { try { File.Delete(InstalledFile(toolsDir)); } catch (IOException) {} }
                result.RolledBack = true; result.Reason = failure; result.NewVersion = await RunVersion(exe, token);
                return result;
            }
            result.Changed = true;
            Record(toolsDir, exe, result.NewVersion, "https://github.com/yt-dlp/yt-dlp/releases/tag/" + release.Tag);
            // Keep the two most recent backups.
            foreach (var old in Directory.GetFiles(backupDir, "yt-dlp-*.exe").OrderByDescending(f => File.GetLastWriteTimeUtc(f)).Skip(2)) try { File.Delete(old); } catch (IOException) {}
            return result;
        }
        static async Task Fetch(IEngineSource source, string url, string target, long maxBytes, CancellationToken token) {
            try { await source.Download(url, target, maxBytes, token); }
            catch (OperationCanceledException) { throw; }
            catch (FlowException) { throw; }
            catch (Exception ex) { throw new FlowException(ErrorCode.EngineUpdateFailed, ex.Message, new[] { "comp.fail.network" }); }
        }
        static void TryClear(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) {} }
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
            var exe = Path.Combine(toolsDir, "yt-dlp.exe");
            await Restore(backup, exe);
            File.Delete(backup);
            Record(toolsDir, exe, version, "backup");
            return version;
        }
    }

    public sealed class EngineRelease { public string Tag, ExeUrl, SumsUrl; }
    public interface IEngineSource {
        Task<EngineRelease> Latest(CancellationToken token);
        Task Download(string url, string target, long maxBytes, CancellationToken token);
    }

    // Official yt-dlp releases on GitHub. Both files come from the same release tag, so the checksum list and the
    // executable cannot belong to different releases.
    public sealed class GitHubEngineSource : IEngineSource {
        const string LatestApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
        const string DownloadBase = "https://github.com/yt-dlp/yt-dlp/releases/download/";
        static readonly Regex TagPattern = new Regex(@"^\d{4}\.\d{2}\.\d{2}(\.\d+)?$");

        public async Task<EngineRelease> Latest(CancellationToken token) {
            var file = Path.GetTempFileName();
            try {
                await Download(LatestApi, file, 2 * 1024 * 1024, token);
                var data = Engine.Json().DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                var tag = Engine.Text(data, "tag_name", null);
                if (tag == null || !TagPattern.IsMatch(tag) || Engine.Flag(data, "prerelease") || Engine.Flag(data, "draft")) throw new InvalidDataException("Unexpected release data");
                return new EngineRelease { Tag = tag, ExeUrl = DownloadBase + tag + "/yt-dlp.exe", SumsUrl = DownloadBase + tag + "/SHA2-256SUMS" };
            } finally { try { File.Delete(file); } catch (IOException) {} }
        }

        public async Task Download(string url, string target, long maxBytes, CancellationToken token) {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Flow/" + Program.Version;
            request.Accept = url.StartsWith(LatestApi, StringComparison.Ordinal) ? "application/vnd.github+json" : "*/*";
            request.AllowAutoRedirect = true; request.MaximumAutomaticRedirections = 5;
            request.Timeout = 30000; request.ReadWriteTimeout = 60000;
            request.Credentials = null; request.UseDefaultCredentials = false;
            using (token.Register(request.Abort))
            using (var response = (HttpWebResponse)await request.GetResponseAsync()) {
                token.ThrowIfCancellationRequested();
                // Release files redirect to GitHub's download host; every hop must stay on HTTPS.
                if (response.ResponseUri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Redirected away from HTTPS");
                if (response.ContentLength > maxBytes) throw new InvalidDataException("File too large");
                using (var input = response.GetResponseStream())
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true)) {
                    var buffer = new byte[81920]; long total = 0; int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token)) > 0) {
                        total += read;
                        if (total > maxBytes) throw new InvalidDataException("File too large");
                        await output.WriteAsync(buffer, 0, read, token);
                    }
                }
            }
        }
    }
}
