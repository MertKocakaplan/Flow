using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Flow {
    public sealed class AppPaths {
        public string DataDir, LegacyDir, DefaultDownloads;
        public string SettingsFile { get { return Path.Combine(DataDir, "flow.settings.json"); } }
        public string HistoryFile { get { return Path.Combine(DataDir, "flow.history.json"); } }
        public string QueueFile { get { return Path.Combine(DataDir, "flow.queue.json"); } }
        public string LockFile { get { return Path.Combine(DataDir, "flow.lock"); } }
        public string CacheDir { get { return Path.Combine(DataDir, "cache"); } }
        public string LegacySettings { get { return Path.Combine(LegacyDir, "settings.json"); } }

        public static AppPaths Resolve() { return Resolve(Environment.GetEnvironmentVariable); }
        // FLOW_* variables isolate tests; the older AKIS_* names are still honored for existing harnesses.
        public static AppPaths Resolve(Func<string, string> env) {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var akisData = env("AKIS_DATA_DIR");
            return new AppPaths {
                DataDir = env("FLOW_DATA_DIR") ?? akisData ?? Path.Combine(local, "Flow"),
                LegacyDir = env("FLOW_LEGACY_DIR") ?? akisData ?? Path.Combine(local, "Akis"),
                DefaultDownloads = env("FLOW_DOWNLOAD_DIR") ?? env("AKIS_DOWNLOAD_DIR") ?? Path.Combine(KnownFolders.Downloads(), "Flow")
            };
        }
    }

    public static class KnownFolders {
        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
        public static string Downloads() {
            IntPtr ptr; var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out ptr) == 0) { try { return Marshal.PtrToStringUni(ptr); } finally { Marshal.FreeCoTaskMem(ptr); } }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    public sealed class LoadResult {
        public Dictionary<string, object> Data;   // null when nothing usable was found
        public bool Recovered;                     // main file was damaged; the last good backup was used
        public bool Reset;                         // main file was damaged and no backup was usable
        public string CorruptCopy;                 // where the damaged file was preserved
    }

    // Atomic JSON files: write to a temp file, flush, then replace. The previous file becomes ".bak"
    // (last good copy). A damaged file is kept aside, never silently overwritten.
    public static class JsonStore {
        public static LoadResult Load(string path) {
            var result = new LoadResult();
            var backup = path + ".bak";
            if (File.Exists(path)) {
                result.Data = TryRead(path);
                if (result.Data != null) return result;
                result.CorruptCopy = path + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                try { File.Move(path, result.CorruptCopy); } catch (IOException) { result.CorruptCopy = null; }
                result.Data = File.Exists(backup) ? TryRead(backup) : null;
                if (result.Data != null) { result.Recovered = true; TryRestore(backup, path); }
                else result.Reset = true;
                return result;
            }
            // Main file missing but a backup exists (interrupted replace): recover quietly.
            if (File.Exists(backup)) { result.Data = TryRead(backup); if (result.Data != null) TryRestore(backup, path); }
            return result;
        }
        static void TryRestore(string backup, string path) { try { File.Copy(backup, path, false); } catch (IOException) {} catch (UnauthorizedAccessException) {} }
        static Dictionary<string, object> TryRead(string path) {
            try {
                var text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text)) return null;
                return Engine.Json().DeserializeObject(text) as Dictionary<string, object>;
            } catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static void Save(string path, object value) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var json = Engine.Json().Serialize(value);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var bytes = new UTF8Encoding(false).GetBytes(json);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            try {
                if (File.Exists(path)) {
                    try { File.Replace(temp, path, path + ".bak", true); }
                    catch (IOException) { File.Copy(path, path + ".bak", true); File.Delete(path); File.Move(temp, path); }
                } else File.Move(temp, path);
            } finally { if (File.Exists(temp)) try { File.Delete(temp); } catch (IOException) {} }
        }

        public static T Convert<T>(Dictionary<string, object> data) where T : class {
            if (data == null) return null;
            try { return Engine.Json().ConvertToType<T>(data); } catch (InvalidOperationException) { return null; } catch (ArgumentException) { return null; }
        }
    }

    public static class SettingsRules {
        public static readonly string[] Languages = { "system", "tr", "en" };
        public static readonly string[] Themes = { "system", "light", "dark" };
        public static readonly string[] Profiles = { "compatible", "best", "small", "mp3", "m4a", "custom" };
        public static readonly string[] Formats = { "mp4", "mkv", "mp3", "m4a" };
        public static readonly int[] Heights = { 0, 2160, 1440, 1080, 720, 480, 360 };
        public static readonly string[] Codecs = { "auto", "h264" };
        public static readonly int[] FpsLimits = { 0, 30, 60 };
        public static readonly string[] Organizes = { "none", "platform", "platform-channel" };
        public static readonly string[] Duplicates = { "skip", "rename" };
        public static readonly string[] MissedSchedules = { "start", "hold" };
        public const string DefaultTemplate = "{title} [{id}]";

        public static FlowSettings Defaults(AppPaths paths) {
            return new FlowSettings {
                SchemaVersion = FlowSettings.CurrentSchema, Language = "system", Theme = "system", Folder = paths.DefaultDownloads,
                Profile = "compatible", Format = "mp4", Height = 1080, StrictQuality = false, Codec = "h264", FpsLimit = 0,
                Organize = "none", NameTemplate = DefaultTemplate, Duplicate = "rename", Parallel = 1, Retries = 2, SpeedLimit = 0,
                AutoPreview = true, ResumeInterrupted = true, MissedSchedule = "start", MinimizeToTray = false, CloseToTray = false, Notify = true,
                CollectionFolder = true, LastTab = "YouTube", TabProfiles = new Dictionary<string, string>()
            };
        }

        // Missing or invalid fields fall back to defaults field by field; valid values are kept.
        public static FlowSettings Normalize(Dictionary<string, object> raw, AppPaths paths) {
            var d = Defaults(paths);
            if (raw == null) return d;
            var s = JsonStore.Convert<FlowSettings>(raw) ?? new FlowSettings();
            Func<string, bool> has = key => raw.ContainsKey(key) && raw[key] != null;
            s.SchemaVersion = FlowSettings.CurrentSchema;
            if (!Languages.Contains(s.Language)) s.Language = d.Language;
            if (!Themes.Contains(s.Theme)) s.Theme = d.Theme;
            if (string.IsNullOrWhiteSpace(s.Folder) || !IsRooted(s.Folder)) s.Folder = d.Folder;
            if (!Profiles.Contains(s.Profile)) s.Profile = d.Profile;
            if (!Formats.Contains(s.Format)) s.Format = d.Format;
            if (!has("Height") || !Heights.Contains(s.Height)) s.Height = d.Height;
            if (!Codecs.Contains(s.Codec)) s.Codec = d.Codec;
            if (!FpsLimits.Contains(s.FpsLimit)) s.FpsLimit = d.FpsLimit;
            if (!Organizes.Contains(s.Organize)) s.Organize = d.Organize;
            if (!Naming.IsValidTemplate(s.NameTemplate)) s.NameTemplate = d.NameTemplate;
            if (!Duplicates.Contains(s.Duplicate)) s.Duplicate = d.Duplicate;
            if (s.Parallel < 1 || s.Parallel > 3) s.Parallel = d.Parallel;
            if (!has("Retries") || s.Retries < 0 || s.Retries > 5) s.Retries = d.Retries;
            if (s.SpeedLimit < 0) s.SpeedLimit = 0;
            if (!has("AutoPreview")) s.AutoPreview = d.AutoPreview;
            if (!has("ResumeInterrupted")) s.ResumeInterrupted = d.ResumeInterrupted;
            if (!has("Notify")) s.Notify = d.Notify;
            if (!has("CollectionFolder")) s.CollectionFolder = d.CollectionFolder;
            if (!new long[] { 0, 262144, 524288, 1048576, 2097152, 5242880, 10485760 }.Contains(s.SpeedLimit)) s.SpeedLimit = 0;
            if (!MissedSchedules.Contains(s.MissedSchedule)) s.MissedSchedule = d.MissedSchedule;
            if (s.TabProfiles == null) s.TabProfiles = new Dictionary<string, string>();
            foreach (var key in s.TabProfiles.Keys.ToList()) if (!Profiles.Contains(s.TabProfiles[key])) s.TabProfiles.Remove(key);
            if (!new[] { "YouTube", "Instagram", "TikTok", "Other" }.Contains(s.LastTab)) s.LastTab = d.LastTab;
            return s;
        }
        static bool IsRooted(string path) { try { return Path.IsPathRooted(path) && Path.GetFullPath(path).Length > 2; } catch (ArgumentException) { return false; } catch (NotSupportedException) { return false; } }
    }

    public static class HistoryRules {
        public const int Limit = 500;
        public static List<HistoryEntry> Normalize(Dictionary<string, object> raw) {
            var file = JsonStore.Convert<HistoryFile>(raw);
            if (file == null || file.Entries == null) return new List<HistoryEntry>();
            var list = file.Entries.Where(e => e != null && (!string.IsNullOrEmpty(e.FilePath) || !string.IsNullOrEmpty(e.SourceUrl))).ToList();
            foreach (var e in list) {
                if (string.IsNullOrEmpty(e.Id)) e.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrEmpty(e.Title)) e.Title = string.IsNullOrEmpty(e.FilePath) ? "" : Path.GetFileNameWithoutExtension(e.FilePath);
                if (string.IsNullOrEmpty(e.Status)) e.Status = "completed";
                // Only public http(s) links may be re-opened or re-queued.
                Uri uri;
                if (e.SourceUrl != null && !Platforms.TryPublicWebUrl(e.SourceUrl, out uri)) e.SourceUrl = null;
            }
            return list.Take(Limit).ToList();
        }
        public static HistoryFile Wrap(List<HistoryEntry> entries) { return new HistoryFile { SchemaVersion = HistoryFile.CurrentSchema, Entries = entries.Take(Limit).ToList() }; }
    }

    // One-time import of Akış 1.x settings and history. The old file and download folder are left untouched.
    public static class LegacyImport {
        public sealed class Result { public string Folder, Format; public int Height; public List<HistoryEntry> History = new List<HistoryEntry>(); public bool Failed; }
        static readonly Regex SummaryDate = new Regex(@"^\s*(\d{2}\.\d{2}\.\d{4})\s+(\d{2}:\d{2})");
        static readonly Regex SummaryFormat = new Regex(@"·\s*(MP4|MKV|MP3|M4A)\s*·", RegexOptions.IgnoreCase);
        static readonly Regex SummarySize = new Regex(@"([\d.,]+)\s*(KB|MB|GB)\s*$", RegexOptions.IgnoreCase);
        static readonly Regex FileId = new Regex(@"\[([A-Za-z0-9_-]{4,64})\]\.[A-Za-z0-9]{2,5}$");

        public static Result Read(string path) {
            if (!File.Exists(path)) return null;
            Dictionary<string, object> data;
            try { data = Engine.Json().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>; }
            catch (Exception) { return new Result { Failed = true }; }
            if (data == null) return new Result { Failed = true };
            var result = new Result { Folder = Engine.Text(data, "Folder", null), Format = Engine.Text(data, "Format", null), Height = (int)Engine.Number(data, "Height", 1080) };
            object items;
            if (!data.TryGetValue("History", out items) || !(items is System.Collections.IEnumerable)) return result;
            foreach (var raw in ((System.Collections.IEnumerable)items).Cast<object>()) {
                var item = raw as Dictionary<string, object>;
                if (item == null) continue;
                var filePath = Engine.Text(item, "FilePath", null);
                if (string.IsNullOrEmpty(filePath)) continue;
                var summary = Engine.Text(item, "Summary", "");
                var entry = new HistoryEntry { Id = Guid.NewGuid().ToString("N"), Title = Engine.Text(item, "Title", Path.GetFileNameWithoutExtension(filePath)), FilePath = filePath, Status = "completed", Legacy = true, SourceUrl = null, Platform = null };
                var date = SummaryDate.Match(summary);
                DateTime when;
                if (date.Success && DateTime.TryParseExact(date.Groups[1].Value + " " + date.Groups[2].Value, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out when)) entry.CompletedAt = Iso.From(when);
                var format = SummaryFormat.Match(summary);
                entry.Format = format.Success ? format.Groups[1].Value.ToLowerInvariant() : Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
                try { if (File.Exists(filePath)) entry.SizeBytes = new FileInfo(filePath).Length; } catch (Exception) {}
                if (entry.SizeBytes == 0) entry.SizeBytes = ParseSize(summary);
                // The bracketed id is kept for reference only; no source URL is invented from it.
                var id = FileId.Match(filePath);
                if (id.Success) entry.ContentId = id.Groups[1].Value;
                result.History.Add(entry);
            }
            return result;
        }
        static long ParseSize(string summary) {
            var m = SummarySize.Match(summary);
            double value;
            if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(".", "").Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return 0;
            var unit = m.Groups[2].Value.ToUpperInvariant();
            return (long)(value * (unit == "GB" ? 1073741824 : unit == "MB" ? 1048576 : 1024));
        }
    }

    public sealed class StoreState {
        public FlowSettings Settings;
        public List<HistoryEntry> History;
        public LoadResult SettingsLoad, HistoryLoad;
        public bool Imported, ImportFailed;
    }

    public static class Store {
        public static StoreState Load(AppPaths paths) {
            var state = new StoreState();
            state.SettingsLoad = JsonStore.Load(paths.SettingsFile);
            state.HistoryLoad = JsonStore.Load(paths.HistoryFile);
            bool fresh = state.SettingsLoad.Data == null && !state.SettingsLoad.Reset && !File.Exists(paths.SettingsFile + ".bak");
            state.Settings = SettingsRules.Normalize(state.SettingsLoad.Data, paths);
            state.History = HistoryRules.Normalize(state.HistoryLoad.Data);
            if (fresh) {
                var legacy = LegacyImport.Read(paths.LegacySettings);
                if (legacy != null && legacy.Failed) state.ImportFailed = true;
                else if (legacy != null) {
                    var s = state.Settings;
                    if (!string.IsNullOrEmpty(legacy.Folder) && Path.IsPathRooted(legacy.Folder)) s.Folder = legacy.Folder;
                    if (SettingsRules.Formats.Contains(legacy.Format)) { s.Format = legacy.Format; s.Profile = legacy.Format == "mp3" ? "mp3" : legacy.Format == "m4a" ? "m4a" : "custom"; s.Codec = "auto"; }
                    if (SettingsRules.Heights.Contains(legacy.Height)) s.Height = legacy.Height;
                    s.StrictQuality = true;   // Akış treated the resolution as a hard upper bound.
                    s.ImportedFrom = paths.LegacySettings; s.ImportedAt = Iso.Now(); s.ImportedCount = legacy.History.Count;
                    var known = new HashSet<string>(state.History.Select(h => h.FilePath ?? ""), StringComparer.OrdinalIgnoreCase);
                    state.History.AddRange(legacy.History.Where(h => !known.Contains(h.FilePath)));
                    state.Imported = true;
                }
            }
            bool folderUpdated = UpdateLegacyDownloadFolder(state.Settings, paths);
            if (state.Imported || folderUpdated) SaveSettings(paths, state.Settings);
            if (state.Imported) SaveHistory(paths, state.History);
            return state;
        }
        // Upgrade only the old default beside Downloads\Flow. Custom destinations,
        // existing history and queued jobs keep their paths; no media is moved.
        static bool UpdateLegacyDownloadFolder(FlowSettings settings, AppPaths paths) {
            try {
                var target = Path.GetFullPath(paths.DefaultDownloads).TrimEnd('\\', '/');
                if (!string.Equals(Path.GetFileName(target), "Flow", StringComparison.OrdinalIgnoreCase)) return false;
                var folder = Path.GetFullPath(settings.Folder).TrimEnd('\\', '/');
                var parent = Path.GetDirectoryName(target);
                if (!new[] { "Akis", "Akış" }.Any(name => string.Equals(folder, Path.Combine(parent, name), StringComparison.OrdinalIgnoreCase))) return false;
                settings.Folder = paths.DefaultDownloads;
                return true;
            } catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }
        public static void SaveSettings(AppPaths paths, FlowSettings settings) { settings.SchemaVersion = FlowSettings.CurrentSchema; JsonStore.Save(paths.SettingsFile, settings); }
        public static void SaveHistory(AppPaths paths, List<HistoryEntry> history) { JsonStore.Save(paths.HistoryFile, HistoryRules.Wrap(history)); }
    }

    // One Flow instance per data folder. A second start signals the first to come forward and exits.
    public sealed class InstanceLock : IDisposable {
        Mutex mutex; FileStream lockFile; EventWaitHandle activate; RegisteredWaitHandle registration;
        public static string Key(string dataDir) {
            using (var sha = SHA256.Create()) {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).TrimEnd('\\').ToLowerInvariant()));
                return BitConverter.ToString(bytes, 0, 10).Replace("-", "");
            }
        }
        public static InstanceLock TryAcquire(string dataDir) {
            var key = Key(dataDir);
            bool created;
            var mutex = new Mutex(true, @"Local\Flow.Instance." + key, out created);
            if (!created) { mutex.Dispose(); return null; }
            FileStream file = null;
            try {
                Directory.CreateDirectory(dataDir);
                file = new FileStream(Path.Combine(dataDir, "flow.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            } catch (IOException) { mutex.ReleaseMutex(); mutex.Dispose(); return null; }
            catch (UnauthorizedAccessException) { file = null; }
            var result = new InstanceLock { mutex = mutex, lockFile = file };
            result.activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Flow.Activate." + key);
            return result;
        }
        public void OnActivate(Action callback) {
            registration = ThreadPool.RegisterWaitForSingleObject(activate, (s, timedOut) => callback(), null, Timeout.Infinite, false);
        }
        public static bool SignalExisting(string dataDir) {
            try { using (var handle = EventWaitHandle.OpenExisting(@"Local\Flow.Activate." + Key(dataDir))) return handle.Set(); }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        public void Dispose() {
            if (registration != null) { registration.Unregister(null); registration = null; }
            if (activate != null) { activate.Dispose(); activate = null; }
            if (lockFile != null) { lockFile.Dispose(); lockFile = null; }
            if (mutex != null) { try { mutex.ReleaseMutex(); } catch (ApplicationException) {} mutex.Dispose(); mutex = null; }
        }
    }
}
