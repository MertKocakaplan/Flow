using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Flow {
    // Start / end times typed by the user: "90", "1:30", "01:02:03", "1:30.5" or "1:30,5".
    public static class ClipTime {
        static readonly Regex Pattern = new Regex(@"^\s*(?:(\d{1,3}):)?(?:(\d{1,3}):)?(\d{1,6})(?:[.,](\d{1,3}))?\s*$");
        public static bool TryParse(string text, out double seconds) {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = Pattern.Match(text);
            if (!m.Success) return false;
            var parts = new List<int>();
            if (m.Groups[1].Success) parts.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            if (m.Groups[2].Success) parts.Add(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            parts.Add(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            if (parts.Count > 1 && parts.Skip(1).Any(p => p >= 60)) return false;
            double value = 0;
            foreach (var p in parts) value = value * 60 + p;
            if (m.Groups[4].Success) value += double.Parse("0." + m.Groups[4].Value, CultureInfo.InvariantCulture);
            seconds = value;
            return true;
        }
        public static string Format(double seconds) { return Loc.Duration(seconds) + (seconds % 1 > 0.001 ? (seconds % 1).ToString(".0", CultureInfo.InvariantCulture) : ""); }
        public static string Engine(double seconds) { return seconds.ToString("0.###", CultureInfo.InvariantCulture); }
        // Throws ClipInvalid; duration 0 means unknown.
        public static void Validate(double start, double end, double duration) {
            if (start < 0 || end <= start || end - start < 1 || (duration > 0 && (start >= duration || end > duration + 0.5))) throw new FlowException(ErrorCode.ClipInvalid);
        }
    }

    public static partial class Engine {
        // Language keys come from the site. A leading "-" or a comma would mean something to the engine.
        public static bool SafeSubLang(string key) { return key != null && System.Text.RegularExpressions.Regex.IsMatch(key, @"^[A-Za-z0-9][A-Za-z0-9_-]{0,31}\z"); }
        static partial void AddExtras(List<string> args, DownloadJob job, Selection selection) {
            var o = job.Options;
            // Subtitles are skipped for clips: they would not match the cut.
            var langs = (o.SubLangs ?? new List<string>()).Where(SafeSubLang).ToList();
            if (langs.Count > 0 && !o.HasClip) {
                bool embed = o.SubMode == "embed" && !o.IsAudio;
                if (embed) args.Add("--embed-subs");
                else args.AddRange(new[] { "--write-subs", "--sub-format", "srt/vtt/best", "--convert-subs", "srt" });
                if (o.SubAuto) args.Add("--write-auto-subs");
                args.AddRange(new[] { "--sub-langs", string.Join(",", langs) });
            }
            if (o.HasClip && job.ClipMethod != "local") {
                args.AddRange(new[] { "--download-sections", "*" + ClipTime.Engine(o.ClipStart) + "-" + ClipTime.Engine(o.ClipEnd) });
                if (o.ClipMode == "precise") args.Add("--force-keyframes-at-cuts");
            }
        }
    }

    public partial class EngineExecutor {
        // Local cut / conversion with FFmpeg: used for the compatible profile without H.264 and as the
        // fallback when a range cannot be downloaded directly.
        public static async Task Transcode(string input, string output, string mode, double start, double end, double duration, CancellationToken token, Action<double> progress, Action<string> log) {
            if (File.Exists(output)) File.Delete(output);
            var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
            bool cut = end > start;
            if (cut) args.AddRange(new[] { "-ss", ClipTime.Engine(start), "-to", ClipTime.Engine(end) });
            args.AddRange(new[] { "-i", input });
            if (mode == "copy") { args.AddRange(new[] { "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero" }); if (output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) args.AddRange(new[] { "-c:s", "mov_text" }); }
            else if (mode == "h264") args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?", "-map", "0:s?", "-c:v", "libx264", "-preset", "medium", "-crf", cut ? "18" : "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-c:s", output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "mov_text" : "copy", "-movflags", "+faststart" });
            else if (mode == "mp3") args.AddRange(new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "0" });
            else args.AddRange(new[] { "-vn", "-c:a", "aac", "-b:a", "192k" });
            args.AddRange(new[] { "-progress", "pipe:1", "-nostats", output });
            double length = cut ? end - start : duration;
            var result = await ProcessRunner.Run(Flow.Engine.FfmpegExe, args, line => {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) || line.StartsWith("out_time_ms=", StringComparison.Ordinal)) {
                    long us; if (length > 0 && long.TryParse(line.Substring(line.IndexOf('=') + 1), out us) && us >= 0) progress(Math.Min(100, us / 1e6 / length * 100));
                } else if (!line.Contains("=")) log(line);
            }, token, 48 * 60 * 60);
            if (result.ExitCode != 0) { var code = Flow.Engine.Classify(result.Error); throw new FlowException(code == ErrorCode.Unknown ? ErrorCode.VerifyFailed : code, result.Error, new[] { Loc.T("verify.convert") }); }
        }

        // Subtitle files written next to the engine output are renamed to follow the final file.
        public static List<string> MoveSubtitles(string fromDir, string fromBase, string toDir, string toBase) {
            var moved = new List<string>();
            if (!Directory.Exists(fromDir)) return moved;
            foreach (var file in Directory.GetFiles(fromDir, fromBase + ".*.srt")) {
                var lang = Path.GetFileNameWithoutExtension(file).Substring(fromBase.Length + 1);
                var target = Path.Combine(toDir, toBase + "." + lang + ".srt");
                if (File.Exists(target)) File.Delete(target);
                File.Move(file, target); moved.Add(target);
            }
            return moved;
        }
        public static List<string> SubtitleFiles(string dir, string baseName) {
            try { return Directory.Exists(dir) ? Directory.GetFiles(dir, baseName + ".*.srt").ToList() : new List<string>(); } catch (Exception) { return new List<string>(); }
        }
    }
}
