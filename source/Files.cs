using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Flow {
    // Output names are built by Flow (not by the engine) so they can be previewed and checked for collisions.
    public static class Naming {
        public static readonly string[] Tokens = { "title", "id", "channel", "platform", "quality", "date", "index", "playlist" };
        static readonly Regex TokenPattern = new Regex(@"\{([a-z]+)\}");
        static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        public const int MaxNameLength = 150;

        public static bool IsValidTemplate(string template) {
            if (string.IsNullOrWhiteSpace(template) || template.Length > 120) return false;
            if (TokenPattern.Replace(template, "").IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            var used = TokenPattern.Matches(template).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            if (used.Any(t => !Tokens.Contains(t))) return false;
            var rest = TokenPattern.Replace(template, "");
            if (rest.Contains("{") || rest.Contains("}")) return false;
            return used.Contains("title") || used.Contains("id");
        }

        public static string Expand(string template, IDictionary<string, string> values) {
            var text = TokenPattern.Replace(template, m => { string v; return values.TryGetValue(m.Groups[1].Value, out v) && v != null ? v : ""; });
            text = Regex.Replace(text, @"\s*\[\s*\]", "");       // drop empty "[{id}]"
            text = Regex.Replace(text, @"\s*\(\s*\)", "");
            text = Regex.Replace(text, @"^[\s\-_·]+|[\s\-_·]+$", "");
            return Sanitize(text);
        }

        // Windows-safe file name: invalid characters become similar full-width forms, reserved names are prefixed.
        public static string Sanitize(string name) {
            if (name == null) name = "";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name) {
                if (char.IsControl(c)) continue;
                switch (c) {
                    case '<': sb.Append('＜'); break; case '>': sb.Append('＞'); break; case ':': sb.Append('：'); break;
                    case '"': sb.Append('＂'); break; case '/': sb.Append('／'); break; case '\\': sb.Append('＼'); break;
                    case '|': sb.Append('｜'); break; case '?': sb.Append('？'); break; case '*': sb.Append('＊'); break;
                    default: sb.Append(c); break;
                }
            }
            var text = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            text = Truncate(text, MaxNameLength).TrimEnd('.', ' ');
            if (text.Length == 0) text = "video";
            var stem = text.Split('.')[0].Trim().ToUpperInvariant();
            if (Reserved.Contains(stem)) text = "_" + text;
            return text;
        }

        public static string Truncate(string text, int max) {
            if (text.Length <= max) return text;
            int cut = max;
            if (char.IsLowSurrogate(text[cut])) cut--;
            var head = text.Substring(0, cut);
            int space = head.LastIndexOf(' ');
            if (space > max * 2 / 3) head = head.Substring(0, space);
            return head.TrimEnd() + "…";
        }

        // yt-dlp treats "%" as template syntax.
        public static string EngineTemplate(string fileName) { return fileName.Replace("%", "%%"); }

        public static string QualityTag(int shortSide, double fps) {
            if (shortSide <= 0) return "";
            return shortSide.ToString(CultureInfo.InvariantCulture) + "p" + (fps > 30.5 ? Math.Round(fps).ToString(CultureInfo.InvariantCulture) : "");
        }
        public static string ClipTag(double start, double end) { return Stamp(start) + "-" + Stamp(end); }
        static string Stamp(double seconds) {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? string.Format(CultureInfo.InvariantCulture, "{0}h{1:00}m{2:00}s", (int)t.TotalHours, t.Minutes, t.Seconds) : string.Format(CultureInfo.InvariantCulture, "{0:00}m{1:00}s", (int)t.TotalMinutes, t.Seconds);
        }

        // "Name (2).ext", "Name (3).ext", ... for the first free name.
        public static string Unique(string folder, string name, string ext, Func<string, bool> exists) {
            var candidate = Path.Combine(folder, name + "." + ext);
            for (int i = 2; exists(candidate) && i < 1000; i++) candidate = Path.Combine(folder, name + " (" + i.ToString(CultureInfo.InvariantCulture) + ")." + ext);
            return candidate;
        }
    }
}
