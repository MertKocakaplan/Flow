using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Flow {
    // What was actually tried with this package's engine. "Listed by the engine" is not the same as
    // "works now for this video", so the two are shown separately.
    public class SiteInfo {
        public string Name { get; set; }
        public string Status { get; set; }
        public string NoteKey { get; set; }
        public string StatusText { get { return Loc.T("site." + Status); } }
        public string NoteText { get { return string.IsNullOrEmpty(NoteKey) ? "" : Loc.T(NoteKey); } }
        public string Kind { get { return Status == "verified" ? "done" : Status == "partial" || Status == "unverified" ? "warning" : Status == "untested" ? "muted" : "error"; } }
    }

    public class ExtractorRow {
        public string Name; public bool Broken;
        public override string ToString() { return Broken ? Name + "  —  " + Loc.T("site.broken") : Name; }
    }

    public static class Sites {
        public const string CheckedOn = "2026-09-28";
        // Results of the live checks for Flow 2.0 (see TEST-SONUCLARI.txt).
        public static readonly SiteInfo[] Known = {
            new SiteInfo { Name = "YouTube", Status = "verified", NoteKey = "site.note.youtube" },
            new SiteInfo { Name = "Instagram", Status = "verified", NoteKey = "site.note.instagram" },
            new SiteInfo { Name = "TikTok", Status = "partial", NoteKey = "site.note.tiktok" },
            new SiteInfo { Name = "Dailymotion", Status = "verified", NoteKey = null },
            new SiteInfo { Name = "Reddit", Status = "verified", NoteKey = null },
            new SiteInfo { Name = "Twitch", Status = "verified", NoteKey = "site.note.twitch" },
            new SiteInfo { Name = "SoundCloud", Status = "verified", NoteKey = "site.note.audio" },
            new SiteInfo { Name = "Bandcamp", Status = "verified", NoteKey = "site.note.audio" },
            new SiteInfo { Name = "Vimeo", Status = "login", NoteKey = "site.note.vimeo" },
            new SiteInfo { Name = "X / Twitter", Status = "unverified", NoteKey = "site.note.x" },
            new SiteInfo { Name = "Facebook", Status = "untested", NoteKey = null }
        };

        static List<ExtractorRow> cache;
        static string cacheVersion;

        public static List<ExtractorRow> ParseExtractors(string output) {
            return output.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0 && !l.StartsWith("ERROR", StringComparison.Ordinal))
                .Select(l => l.EndsWith("(CURRENTLY BROKEN)", StringComparison.Ordinal) ? new ExtractorRow { Name = l.Substring(0, l.Length - "(CURRENTLY BROKEN)".Length).Trim(), Broken = true } : new ExtractorRow { Name = l }).ToList();
        }

        // The engine's extractor list, cached per engine version.
        public static async Task<List<ExtractorRow>> Extractors(string cacheDir, string engineVersion, CancellationToken token) {
            var version = engineVersion ?? "unknown";
            if (cache != null && cacheVersion == version) return cache;
            var file = Path.Combine(cacheDir, "extractors-" + version + ".txt");
            string text = null;
            try { if (File.Exists(file)) text = File.ReadAllText(file, Encoding.UTF8); } catch (IOException) {}
            if (text == null) {
                var r = await ProcessRunner.Run(Engine.Exe, new[] { "--ignore-config", "--list-extractors" }, null, token, 120);
                if (r.ExitCode != 0) throw new FlowException(Engine.Classify(r.Error), r.Error);
                text = r.Output;
                try { Directory.CreateDirectory(cacheDir); File.WriteAllText(file, text, new UTF8Encoding(false)); } catch (IOException) {} catch (UnauthorizedAccessException) {}
            }
            cache = ParseExtractors(text); cacheVersion = version;
            return cache;
        }
        public static IEnumerable<ExtractorRow> Search(IEnumerable<ExtractorRow> rows, string query) {
            query = (query ?? "").Trim();
            if (query.Length == 0) return rows;
            return rows.Where(r => r.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
