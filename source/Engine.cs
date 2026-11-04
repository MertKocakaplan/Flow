using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Flow {
    public class DownloadOptions {
        public string Url;
        public string Folder;
        public string Format = "mp4";
        public int Height = 1080;
        public string Codec = "auto";
        public int FpsLimit;
    }

    public class ProgressEvent {
        public string FormatId;
        public string Status;
        public double Downloaded;
        public double Total;          // exact total, or -1
        public double TotalEstimate;  // estimate when exact is unknown, or -1
        public double Speed;          // bytes/s, or -1
        public double Eta;            // seconds, or -1
        public int FragmentIndex, FragmentCount;
        public double Percent { get { var t = Total > 0 ? Total : TotalEstimate; return t > 0 ? Math.Min(100, Downloaded * 100 / t) : -1; } }
    }

    public static partial class Engine {
        public static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string ToolsDir = Path.Combine(BaseDir, "tools");
        public static readonly string Exe = Path.Combine(ToolsDir, "yt-dlp.exe");
        public static readonly string FfmpegExe = Path.Combine(ToolsDir, "ffmpeg.exe");
        public static readonly string FfprobeExe = Path.Combine(ToolsDir, "ffprobe.exe");
        public static readonly string DenoExe = Path.Combine(ToolsDir, "deno.exe");
        public static readonly string[] ToolFiles = { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe", "deno.exe" };
        public const string ProgressPrefix = "FLOW_P:", PostPrefix = "FLOW_PP:", FilePrefix = "FLOW_FILE:", FormatPrefix = "FLOW_FMT:";

        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 256 }; }

        public static void EnsureTools() {
            foreach (var file in ToolFiles)
                if (!File.Exists(Path.Combine(ToolsDir, file))) throw new FlowException(ErrorCode.ToolsMissing, file, new[] { file });
        }

        // Windows CommandLineToArgvW-compatible quoting, including trailing backslashes.
        public static string Quote(string value) {
            if (value == null) value = "";
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value) {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append(c); }
                else { result.Append('\\', slashes); result.Append(c); }
                slashes = 0;
            }
            result.Append('\\', slashes * 2); result.Append('"'); return result.ToString();
        }

        // Shared engine arguments. The user's yt-dlp configuration, cookies and browser data are never used.
        public static List<string> BaseArgs() {
            return new List<string> { "--ignore-config", "--no-colors", "--encoding", "utf-8", "--no-warnings", "--socket-timeout", "25", "--retries", "3", "--fragment-retries", "5", "--ffmpeg-location", ToolsDir, "--js-runtimes", "deno:" + DenoExe };
        }
        // A single media item. itemIndex > 0 selects that entry of a multi-item post.
        public static List<string> SingleArgs(int itemIndex) {
            var args = BaseArgs();
            args.AddRange(new[] { "--no-playlist", "--playlist-items", (itemIndex > 0 ? itemIndex : 1).ToString(CultureInfo.InvariantCulture) });
            return args;
        }
        // Listing is separate from downloading: flat pages for long lists, full entries for short multi-item posts.
        public static List<string> ListArgs(string url, bool flat, int start, int end) {
            var args = BaseArgs();
            args.Add("--yes-playlist");
            if (flat) args.Add("--flat-playlist");
            if (end > 0) { args.Add("-I"); args.Add(start.ToString(CultureInfo.InvariantCulture) + ":" + end.ToString(CultureInfo.InvariantCulture)); }
            args.AddRange(new[] { "--dump-single-json", "--", url });
            return args;
        }
        public static List<string> InspectArgs(string url, int itemIndex) {
            return InspectArgs(url,itemIndex,null);
        }
        public static List<string> InspectArgs(string url, int itemIndex,string referrer) {
            var args = SingleArgs(itemIndex);
            AddReferrer(args,referrer);
            args.AddRange(new[] { "--skip-download", "--dump-single-json", "--", url });
            return args;
        }

        public static List<string> DownloadArgs(DownloadOptions opt) {
            var url = Platforms.Parse(opt.Url).Url;
            if (!new[] { "mp4", "mkv", "mp3", "m4a" }.Contains(opt.Format)) throw new FlowException(ErrorCode.FormatInvalid);
            if (!new[] { 0, 2160, 1440, 1080, 720, 480, 360 }.Contains(opt.Height)) throw new FlowException(ErrorCode.FormatInvalid);
            if (string.IsNullOrWhiteSpace(opt.Folder) || !Path.IsPathRooted(opt.Folder)) throw new FlowException(ErrorCode.FolderInvalid);
            var args = SingleArgs(1);
            args.AddRange(new[] { "--newline", "--progress", "--progress-delta", "0.5", "--progress-template", "download:" + ProgressPrefix + "[%(info.format_id)j,%(progress)j]", "--progress-template", "postprocess:" + PostPrefix + "[%(progress.status)j,%(progress.postprocessor)j]", "--print", "after_move:" + FilePrefix + "%(filepath)j", "--no-simulate", "--windows-filenames", "--trim-filenames", "160", "--no-overwrites", "--continue", "--no-mtime", "--abort-on-unavailable-fragments", "-P", Path.GetFullPath(opt.Folder), "-o", "%(title).140B [%(id)s].%(ext)s" });
            // The limit applies to the short side, so vertical videos are not mistaken for higher resolutions.
            var sortParts = new List<string>();
            if (opt.Codec == "h264") sortParts.Add("vcodec:h264");
            if (opt.Height > 0) sortParts.Add("res:" + opt.Height.ToString(CultureInfo.InvariantCulture));
            if (opt.FpsLimit > 0) sortParts.Add("fps:" + opt.FpsLimit.ToString(CultureInfo.InvariantCulture));
            var sort = sortParts.Count == 0 ? null : string.Join(",", sortParts);
            if (opt.Format == "mp3" || opt.Format == "m4a") {
                args.AddRange(new[] { "-f", "ba/b", "-x", "--audio-format", opt.Format, "--audio-quality", "0" });
            } else if (opt.Format == "mp4") {
                args.AddRange(new[] { "-f", "bv[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv[ext=mp4]", "--merge-output-format", "mp4", "--remux-video", "mp4" });
            } else {
                args.AddRange(new[] { "-f", "bv+ba/b/bv", "--merge-output-format", "mkv", "--remux-video", "mkv" });
            }
            if (sort != null && opt.Format != "mp3" && opt.Format != "m4a") args.AddRange(new[] { "-S", sort });
            args.Add("--"); args.Add(url); return args;
        }

        // Download of one job. The chosen format ids come first; the fallback only applies if they vanished.
        // homeDir is the output folder, or the job folder when Flow post-processes the file itself.
        public static List<string> JobArgs(DownloadJob job, Selection selection, string homeDir, string baseName, string tempDir) {
            var o = job.Options;
            if (!SettingsRules.Formats.Contains(o.Format)) throw new FlowException(ErrorCode.FormatInvalid);
            if (string.IsNullOrWhiteSpace(homeDir) || !Path.IsPathRooted(homeDir)) throw new FlowException(ErrorCode.FolderInvalid);
            var url = Platforms.Parse(job.Url).Url;
            var args = SingleArgs(job.ItemIndex);
            AddReferrer(args,job.Referrer);
            args.AddRange(new[] {
                "--newline", "--progress", "--progress-delta", "0.5",
                "--progress-template", "download:" + ProgressPrefix + "[%(info.format_id)j,%(progress)j]",
                "--progress-template", "postprocess:" + PostPrefix + "[%(progress.status)j,%(progress.postprocessor)j]",
                "--print", "after_move:" + FilePrefix + "%(filepath)j", "--print", "after_move:" + FormatPrefix + "%(format_id)j", "--no-quiet",
                "--no-simulate", "--no-overwrites", "--continue", "--no-mtime", "--abort-on-unavailable-fragments",
                "-P", "home:" + Path.GetFullPath(homeDir), "-P", "temp:" + Path.GetFullPath(tempDir), "-o", Naming.EngineTemplate(baseName) + ".%(ext)s",
                "-f", selection.Spec + "/" + FormatSelector.Fallback(o)
            });
            var sort = FormatSelector.SortFor(o);
            if (sort != null) args.AddRange(new[] { "-S", sort });
            if (o.IsAudio) args.AddRange(new[] { "-x", "--audio-format", o.Format, "--audio-quality", "0" });
            else if (selection.VideoConversion || selection.ConvertIfNeeded) args.AddRange(new[] { "--merge-output-format", "mkv", "--remux-video", "mkv" });
            else args.AddRange(new[] { "--merge-output-format", o.Format, "--remux-video", o.Format });
            if (o.SpeedLimit > 0) args.AddRange(new[] { "-r", o.SpeedLimit.ToString(CultureInfo.InvariantCulture) });
            AddExtras(args, job, selection);
            args.Add("--"); args.Add(url);
            return args;
        }
        static partial void AddExtras(List<string> args, DownloadJob job, Selection selection);
        static void AddReferrer(List<string> args,string value) { if(!string.IsNullOrEmpty(value))args.AddRange(new[]{"--referer",ImageTransfer.Referrer(value)}); }

        public static string ParseValue(string line, string prefix) {
            if (line == null || !line.StartsWith(prefix, StringComparison.Ordinal)) return null;
            try { return Json().Deserialize<string>(line.Substring(prefix.Length)); } catch (ArgumentException) { return null; } catch (InvalidOperationException) { return null; }
        }

        public static string Text(Dictionary<string, object> data, string key, string fallback) { object value; return data != null && data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : fallback; }
        public static double Number(Dictionary<string, object> data, string key) { return Number(data, key, 0); }
        public static double Number(Dictionary<string, object> data, string key, double fallback) {
            object raw; double value;
            if (data == null || !data.TryGetValue(key, out raw) || raw == null || raw is bool) return fallback;
            return double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value) ? value : fallback;
        }
        public static bool Flag(Dictionary<string, object> data, string key) { object raw; return data != null && data.TryGetValue(key, out raw) && raw is bool && (bool)raw; }

        public static ProgressEvent ParseProgress(string line) {
            if (line == null || !line.StartsWith(ProgressPrefix, StringComparison.Ordinal)) return null;
            var array = Json().DeserializeObject(line.Substring(ProgressPrefix.Length)) as object[];
            if (array == null || array.Length < 2) return null;
            var data = array[1] as Dictionary<string, object>;
            if (data == null) return null;
            return new ProgressEvent {
                FormatId = array[0] as string,
                Status = Text(data, "status", "downloading"),
                Downloaded = Number(data, "downloaded_bytes", 0),
                Total = Number(data, "total_bytes", -1),
                TotalEstimate = Number(data, "total_bytes_estimate", -1),
                Speed = Number(data, "speed", -1),
                Eta = Number(data, "eta", -1),
                FragmentIndex = (int)Number(data, "fragment_index", 0),
                FragmentCount = (int)Number(data, "fragment_count", 0)
            };
        }
        // Returns { status, postprocessor } or null.
        public static string[] ParsePost(string line) {
            if (line == null || !line.StartsWith(PostPrefix, StringComparison.Ordinal)) return null;
            try { var array = Json().DeserializeObject(line.Substring(PostPrefix.Length)) as object[]; return array == null || array.Length < 2 ? null : new[] { array[0] as string, array[1] as string }; }
            catch (ArgumentException) { return null; }
        }
        public static string ParseFile(string line) {
            if (line == null || !line.StartsWith(FilePrefix, StringComparison.Ordinal)) return null;
            try { return Json().Deserialize<string>(line.Substring(FilePrefix.Length)); } catch (ArgumentException) { return null; } catch (InvalidOperationException) { return null; }
        }

        // Maps engine / FFmpeg error output to a language-neutral code. The last ERROR line is most specific.
        public static ErrorCode Classify(string text) {
            if (string.IsNullOrEmpty(text)) return ErrorCode.Unknown;
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var errors = lines.Where(l => l.StartsWith("ERROR:", StringComparison.Ordinal)).ToList();
            var lower = (errors.Count > 0 ? string.Join("\n", errors) : text).ToLowerInvariant();
            if (lower.Contains("unsupported url") || lower.Contains("no suitable extractor")) return ErrorCode.Unsupported;
            if (lower.Contains("drm")) return ErrorCode.Drm;
            if (lower.Contains("ip address is blocked") || lower.Contains("ip has been blocked") || (lower.Contains("your ip") && lower.Contains("blocked"))) return ErrorCode.IpBlocked;
            if (lower.Contains("no space left") || lower.Contains("disk full") || lower.Contains("errno 28") || lower.Contains("not enough space")) return ErrorCode.DiskFull;
            if (lower.Contains("permission denied") || lower.Contains("access is denied") || lower.Contains("errno 13")) return ErrorCode.WriteDenied;
            if (lower.Contains("this live event will begin") || lower.Contains("premieres in") || lower.Contains("is live") && lower.Contains("not supported")) return ErrorCode.Live;
            if (lower.Contains("requested format") && (lower.Contains("not available") || lower.Contains("unavailable"))) return ErrorCode.FormatUnavailable;
            if (lower.Contains("sign in") || lower.Contains("log in") || lower.Contains("login") || lower.Contains("logged-in") || lower.Contains("logged in") || lower.Contains("--cookies") || lower.Contains("not a bot") || lower.Contains("age-restricted") || lower.Contains("age restricted") || lower.Contains("authentication") || lower.Contains("members-only") || lower.Contains("private account")) return ErrorCode.LoginRequired;
            if (lower.Contains("no video") || lower.Contains("no media") || lower.Contains("only images") || lower.Contains("there is no video")) return ErrorCode.NoVideo;
            if (lower.Contains("fragment") && (lower.Contains("not found") || lower.Contains("unable to continue") || lower.Contains("giving up"))) return ErrorCode.FragmentsMissing;
            if (lower.Contains("http error 429") || lower.Contains("too many requests")) return ErrorCode.RateLimited;
            if (lower.Contains("private video") || lower.Contains("video unavailable") || lower.Contains("not available") || lower.Contains("unavailable") || lower.Contains("has been removed") || lower.Contains("does not exist") || lower.Contains("http error 404") || lower.Contains("http error 410")) return ErrorCode.Unavailable;
            if (lower.Contains("http error 403")) return ErrorCode.Forbidden;
            if (lower.Contains("timed out") || lower.Contains("getaddrinfo") || lower.Contains("failed to resolve") || lower.Contains("unable to resolve") || lower.Contains("connection") || lower.Contains("network is unreachable") || lower.Contains("remote end closed") || lower.Contains("ssl") || lower.Contains("proxy") || Regex.IsMatch(lower, @"http error 5\d\d") || lower.Contains("incomplete read") || lower.Contains("content too short")) return ErrorCode.Network;
            return ErrorCode.Unknown;
        }

        // Diagnostics may be copied by the user. Query values (signatures, tokens, session ids) are masked,
        // except a few non-sensitive content identifiers.
        static readonly Regex UrlPattern = new Regex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase);
        static readonly string[] SafeParams = { "v", "list", "index", "t", "lang" };
        public static string Mask(string text) {
            if (string.IsNullOrEmpty(text)) return text;
            text = UrlPattern.Replace(text, m => MaskUrl(m.Value));
            text = Regex.Replace(text, @"(?i)\b(cookie|authorization|x-goog-[a-z-]+|set-cookie)\s*[:=]\s*[^\r\n]+", "$1: ***");
            return Regex.Replace(text, @"(?i)\b(Users\\)[^\\\r\n""]+", "$1***");
        }
        public static string MaskUrl(string url) {
            int q = url.IndexOf('?');
            if (q < 0) return url;
            var head = url.Substring(0, q);
            var pieces = url.Substring(q + 1).Split('&').Select(p => {
                int eq = p.IndexOf('=');
                if (eq < 0) return p;
                var name = p.Substring(0, eq);
                return SafeParams.Contains(name.ToLowerInvariant()) ? p : name + "=***";
            });
            return head + "?" + string.Join("&", pieces);
        }
    }
}
