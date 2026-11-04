using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Flow {
    // All Flow texts live in Strings.Table as { key, Turkish, English }.
    // XAML reads them as DynamicResource "s.<key>"; C# uses Loc.T / Loc.F.
    public static class Loc {
        static Dictionary<string, string[]> map;
        public static string Lang { get; private set; }
        public static CultureInfo Culture { get; private set; }
        public static event Action Changed;

        static Loc() { Set("system"); }

        public static string Resolve(string preference) {
            if (preference == "tr" || preference == "en") return preference;
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
        }

        public static void Set(string preference) {
            if (map == null) {
                map = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var row in Strings.Table) map[row[0]] = row;
            }
            Lang = Resolve(preference);
            Culture = CultureInfo.GetCultureInfo(Lang == "tr" ? "tr-TR" : "en-US");
            if (windows != null) Reapply();
            var handler = Changed; if (handler != null) handler();
        }

        public static IEnumerable<string> Keys { get { return map.Keys; } }
        public static bool Has(string key) { return map.ContainsKey(key); }

        public static string T(string key) {
            string[] row;
            if (!map.TryGetValue(key, out row)) return "[" + key + "]";
            return Lang == "tr" ? row[1] : row[2];
        }
        public static string In(string lang, string key) { string[] row; return map.TryGetValue(key, out row) ? (lang == "tr" ? row[1] : row[2]) : null; }
        public static string F(string key, params object[] args) { return string.Format(Culture, T(key), args); }

        public static string Error(ErrorCode code, params object[] args) {
            var key = "err." + code;
            if (!Has(key)) key = "err.Unknown";
            // An argument may be a text key, stored as such so it follows the language.
            var shown = (args ?? new object[0]).Select(a => { var s = a as string; return s != null && Has(s) ? T(s) : a; }).ToArray();
            // Records saved without their arguments lose the placeholder, never show it raw.
            try { return F(key, shown); } catch (FormatException) { return Regex.Replace(T(key), @"[\s:]*\{\d+\}", "").Trim(); }
        }
        public static string Error(Exception ex) {
            var flow = ex as FlowException;
            if (flow != null) return Error(flow.Code, flow.Args.Cast<object>().ToArray());
            var code = Errors.FromException(ex);
            return code == ErrorCode.Unknown ? Error(code) + " (" + ex.Message + ")" : Error(code);
        }

        public static string Size(double bytes) {
            if (bytes < 0 || double.IsNaN(bytes)) return T("common.unknown");
            if (bytes >= 1073741824) return (bytes / 1073741824).ToString("0.00", Culture) + " GB";
            if (bytes >= 1048576) return (bytes / 1048576).ToString("0.0", Culture) + " MB";
            return Math.Ceiling(bytes / 1024).ToString("0", Culture) + " KB";
        }
        public static string Speed(double bytesPerSecond) { return Size(bytesPerSecond) + T("unit.perSecond"); }
        public static string Duration(double seconds) {
            var t = TimeSpan.FromSeconds(Math.Max(0, Math.Min(seconds, 315360000)));
            return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") : ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");
        }
        public static string Date(DateTime utc) { return utc.ToLocalTime().ToString(Lang == "tr" ? "dd.MM.yyyy HH:mm" : "MMM d, yyyy h:mm tt", Culture); }
        public static string Count(long value) { return value.ToString("N0", Culture); }

        public static void Apply(System.Windows.ResourceDictionary resources) {
            foreach (var row in Strings.Table) resources["s." + row[0]] = Lang == "tr" ? row[1] : row[2];
        }

        // Windows whose "s.*" resources follow the current language (switching never reloads the UI).
        static readonly List<WeakReference> windows = new List<WeakReference>();
        public static void Attach(System.Windows.Window window) {
            windows.Add(new WeakReference(window));
            Apply(window.Resources);
            window.Language = System.Windows.Markup.XmlLanguage.GetLanguage(Culture.IetfLanguageTag);
        }
        static void Reapply() {
            foreach (var reference in windows.ToArray()) {
                var window = reference.Target as System.Windows.Window;
                if (window == null) { windows.Remove(reference); continue; }
                Apply(window.Resources);
                window.Language = System.Windows.Markup.XmlLanguage.GetLanguage(Culture.IetfLanguageTag);
            }
        }
    }
}
