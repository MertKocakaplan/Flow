using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Flow;

namespace FlowTests {
    public static class T {
        public static int Count, Failed;
        public static readonly List<string> Failures = new List<string>();
        public static void Check(bool value, string label) {
            if (value) { Count++; Console.WriteLine("PASS " + label); }
            else { Failed++; Failures.Add(label); Console.WriteLine("FAIL " + label); }
        }
        public static void Rejects(Action action, ErrorCode code, string label) {
            try { action(); Check(false, label + " (no error)"); }
            catch (FlowException ex) { Check(code == ErrorCode.None || ex.Code == code, label + (ex.Code == code || code == ErrorCode.None ? "" : " (got " + ex.Code + ")")); }
        }
        public static string Fixture(string name) { return File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("FLOW_TEST_FIXTURES") ?? "fixtures", name), Encoding.UTF8); }
        public static string Scratch(string name) {
            var dir = Path.Combine(Environment.GetEnvironmentVariable("FLOW_TEST_OUT") ?? Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir); return dir;
        }
        public static string Hash(string path) { using (var sha = SHA256.Create()) using (var s = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(s)); }
    }

    public static partial class Program {
        [STAThread]
        public static int Main(string[] args) {
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Length > 0 && args[0] == "echo") { Console.Write(Engine.Json().Serialize(args.Skip(1).ToArray())); return 0; }
            if (args.Length > 0 && args[0] == "wait-child") { Thread.Sleep(60000); return 0; }
            if (args.Length > 0 && args[0] == "wait-parent") {
                var child = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "wait-child") { UseShellExecute = false, CreateNoWindow = true });
                Console.WriteLine(child.Id); Console.Out.Flush(); Thread.Sleep(60000); return 0;
            }
            if (args.Length > 1 && args[0] == "hold-lock") {
                using (var held = InstanceLock.TryAcquire(args[1])) { Console.WriteLine(held == null ? "BUSY" : "LOCKED"); Console.Out.Flush(); Console.In.ReadLine(); }
                return 0;
            }
            Loc.Set("tr");
            Controller.ShellIntegration = false;   // never touch the user's tray or Task Scheduler
            try {
                Unit.Run();
                BrowserTests.Wire();
                if (args.Contains("ui")) UiTests.Run();
                if (args.Contains("live")) LiveTests.Run();
            } catch (Exception ex) { Console.Error.WriteLine(ex); T.Failed++; T.Failures.Add("crash: " + ex.Message); }
            if (T.Failed > 0) { Console.WriteLine("FAILED " + T.Failed + " / " + (T.Count + T.Failed)); foreach (var f in T.Failures) Console.WriteLine("  - " + f); return 1; }
            Console.WriteLine("ALL " + T.Count + " CHECKS PASSED");
            return 0;
        }
    }

    public static partial class Unit {
        public static void Run() {
            Urls(); Arguments(); Parsing(); ErrorsAndText(); Processes(); StorageTests(); InstanceTests(); NamingTests();
            RunLater();
        }
        static partial void RunLater();

        static void Urls() {
            const string canonical = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
            foreach (string url in new[] { canonical, "youtu.be/jNQXAC9IVRw?si=abc", "https://m.youtube.com/watch?v=jNQXAC9IVRw&list=ignore", "https://www.youtube.com/shorts/jNQXAC9IVRw", "https://youtube.com/live/jNQXAC9IVRw", "http://music.youtube.com/watch?v=jNQXAC9IVRw" })
                T.Check(Platforms.Parse(url).Url == canonical && Platforms.Parse(url).Platform == Platform.YouTube, "canonical video URL " + url);
            foreach (string url in new[] { "", "--exec calc", "https://youtube.com/playlist?list=x", "https://user:pass@youtube.com/watch?v=jNQXAC9IVRw", "https://youtube.com:123/watch?v=jNQXAC9IVRw", "https://youtu.be/id", "file:///C:/test", "https://youtu.be/jNQXAC9IVRw/extra", "https://localhost/video", "https://127.0.0.1/video", "https://192.168.1.1/video", "https://[::1]/video", "https://2130706433/video", "https://server.local/video", "https://media.internal/video", "https://intranet/video", "ftp://example.com/video", "https://user:pass@instagram.com/p/test/", "https://tiktok.com:8443/video", "https://example.com/a\n--exec=calc", "https://example.com/a b", "https://example.com\\video" })
                T.Rejects(() => Platforms.Parse(url), ErrorCode.None, "invalid URL " + url.Replace("\n", "<newline>"));
            foreach (string url in new[] { "https://www.instagram.com/reel/ABC123/?igsh=abc", "https://www.instagram.com/p/ABC123/", "https://www.tiktok.com/@example/video/123456789", "https://vm.tiktok.com/ABC123/", "https://vt.tiktok.com/ABC123/", "https://vimeo.com/12345", "https://www.dailymotion.com/video/x12345", "https://example.com/media/video.mp4?token=a%2Bb&expires=123" })
                T.Check(Platforms.Parse(url).Url == url, "third-party path and query preserved " + url);
            T.Check(Platforms.Parse("instagram.com/reel/ABC123/").Url == "https://instagram.com/reel/ABC123/", "missing scheme defaults to HTTPS");
            T.Check(new Uri(Platforms.Parse("https://youtube.com.evil.test/watch?v=jNQXAC9IVRw").Url).Host == "youtube.com.evil.test", "lookalike domain never canonicalized as YouTube");
            T.Check(Platforms.Parse("https://youtube.com.evil.test/watch?v=jNQXAC9IVRw").Platform == Platform.Other, "lookalike domain is not detected as YouTube");
            // Platform detection and scope rules
            T.Check(Platforms.Parse("https://www.instagram.com/reel/ABC123/").Platform == Platform.Instagram && Platforms.Parse("https://vm.tiktok.com/ABC/").Platform == Platform.TikTok && Platforms.Parse("https://vimeo.com/1").Platform == Platform.Other, "platform detection by host");
            T.Check(Platforms.Detect("youtu.be/abc") == Platform.YouTube && Platforms.Detect("instagram.com/p/x") == Platform.Instagram && Platforms.Detect("notaurl") == null && Platforms.Detect("") == null, "lenient detection while typing");
            var list = Platforms.Parse("https://www.youtube.com/playlist?list=PLtest0123456789");
            T.Check(list.Kind == UrlKind.Collection && list.ListId == "PLtest0123456789" && list.Url == "https://www.youtube.com/playlist?list=PLtest0123456789", "YouTube playlist recognised as collection");
            var watchInList = Platforms.Parse("https://www.youtube.com/watch?v=jNQXAC9IVRw&list=PLtest0123456789&index=3");
            T.Check(watchInList.Kind == UrlKind.Single && watchInList.ListId == "PLtest0123456789" && watchInList.Url == canonical, "watch URL inside a list stays a single video, list offered");
            T.Rejects(() => Platforms.Parse("https://www.youtube.com/playlist?list=WL"), ErrorCode.PlaylistInvalid, "watch-later list needs sign-in");
            T.Rejects(() => Platforms.Parse("https://www.youtube.com/@SomeChannel/videos"), ErrorCode.ChannelUnsupported, "channel pages rejected with explanation");
            T.Rejects(() => Platforms.Parse("https://www.instagram.com/someuser/"), ErrorCode.ProfileUnsupported, "Instagram profile rejected");
            T.Rejects(() => Platforms.Parse("https://www.instagram.com/stories/someuser/123/"), ErrorCode.ProfileUnsupported, "Instagram stories rejected");
            T.Check(Platforms.Parse("https://www.instagram.com/someuser/reel/ABC123/").Platform == Platform.Instagram, "Instagram reel under a user path accepted");
            T.Rejects(() => Platforms.Parse("https://www.tiktok.com/@user/photo/123"), ErrorCode.PhotoUnsupported, "TikTok photo post rejected");
            T.Rejects(() => Platforms.Parse("https://www.tiktok.com/tag/funny"), ErrorCode.TikTokPageUnsupported, "TikTok tag page rejected");
            T.Rejects(() => Platforms.Parse("https://www.tiktok.com/@user"), ErrorCode.ProfileUnsupported, "TikTok profile rejected");
            T.Check(Platforms.FromExtractor("TikTok", Platform.Other) == Platform.TikTok && Platforms.FromExtractor("Generic", Platform.Instagram) == Platform.Instagram && Platforms.FromExtractor("Vimeo", Platform.Other) == Platform.Other, "platform from extractor key");
        }

        static void Arguments() {
            const string canonical = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
            var folder = Path.GetFullPath("test outputs");
            foreach (var format in new[] { "mp4", "mkv", "mp3", "m4a" }) {
                var command = Engine.DownloadArgs(new DownloadOptions { Url = canonical, Folder = folder, Format = format, Height = 1080 });
                T.Check(command[command.Count - 2] == "--" && command.Last() == canonical, format + " positional URL protected");
                T.Check(command.Contains("--no-overwrites") && command.Contains("--continue") && command.Contains("--ignore-config"), format + " preserve files / resume / ignore user config");
                if (format == "mp3" || format == "m4a") T.Check(command.Contains("-x") && !command.Contains("-S"), format + " best audio extraction");
                else T.Check(command[command.IndexOf("-S") + 1] == "res:1080", format + " resolution bound on the short side");
            }
            T.Rejects(() => Engine.DownloadArgs(new DownloadOptions { Url = canonical, Folder = folder, Format = "exe" }), ErrorCode.FormatInvalid, "invalid format");
            T.Rejects(() => Engine.DownloadArgs(new DownloadOptions { Url = canonical, Folder = "relative" }), ErrorCode.FolderInvalid, "relative folder");
            T.Rejects(() => Engine.DownloadArgs(new DownloadOptions { Url = canonical, Folder = folder, Height = -1 }), ErrorCode.FormatInvalid, "invalid resolution");
            var external = Engine.DownloadArgs(new DownloadOptions { Url = "https://www.instagram.com/reel/ABC123/?igsh=abc", Folder = folder });
            T.Check(external.Last() == "https://www.instagram.com/reel/ABC123/?igsh=abc" && external[external.Count - 2] == "--", "third-party download URL remains a single positional argument");
            T.Check(external[external.IndexOf("--playlist-items") + 1] == "1" && !external.Any(a => a.StartsWith("--cookies")), "one collection entry and no browser session access");
            T.Check(external.Contains("--abort-on-unavailable-fragments") && external.Contains("--no-mtime"), "missing fragments abort instead of producing a silent partial file");
            var inspect = Engine.InspectArgs("https://www.instagram.com/p/X/", 3);
            T.Check(inspect[inspect.IndexOf("--playlist-items") + 1] == "3" && inspect.Last() == "https://www.instagram.com/p/X/", "inspection of a chosen collection item");
        }

        static void Parsing() {
            var p = Engine.ParseProgress("FLOW_P:[\"137\",{\"downloaded_bytes\":250,\"total_bytes\":1000,\"speed\":2048,\"eta\":3,\"status\":\"downloading\"}]");
            T.Check(p.Percent == 25 && Loc.Speed(p.Speed) == "2 KB/sn" && Loc.Duration(p.Eta) == "0:03" && p.FormatId == "137", "progress speed percentage ETA and stream id");
            T.Check(Engine.ParseProgress("FLOW_P:[\"1\",{\"downloaded_bytes\":100,\"total_bytes\":null}]").Percent == -1, "unknown length indeterminate");
            T.Check(Engine.ParseProgress("FLOW_P:[\"1\",{\"downloaded_bytes\":999,\"total_bytes_estimate\":100}]").Percent == 100, "estimated total clamp");
            T.Check(Engine.ParseProgress("[download] 10%") == null, "non-progress lines ignored");
            var post = Engine.ParsePost("FLOW_PP:[\"started\",\"Merger\"]");
            T.Check(post != null && post[0] == "started" && post[1] == "Merger", "post-processing stage parsed");
            T.Check(Engine.ParseFile("FLOW_FILE:\"C:\\\\x\\\\a b.mp4\"") == "C:\\x\\a b.mp4", "final file path parsed");
            var info = MediaParser.Parse("{\"title\":\"Türkçe bir video\",\"channel\":\"Kanal\",\"duration\":65,\"is_live\":false}", Platform.Other);
            T.Check(info.Title == "Türkçe bir video" && !info.IsLive && Loc.Duration(info.Duration) == "1:05", "Unicode metadata");
            T.Check(MediaParser.Parse("{\"live_status\":\"is_upcoming\"}", Platform.Other).IsLive, "upcoming live refused");
            var reel = MediaParser.Parse("{\"title\":\"Reel\",\"uploader\":\"Creator\",\"thumbnail\":\"https://cdn.example.com/image.jpg\",\"duration\":12}", Platform.Instagram);
            T.Check(reel.Channel == "Creator" && reel.Thumbnail == "https://cdn.example.com/image.jpg" && !reel.IsCollection, "third-party creator and thumbnail metadata");
            var carousel = MediaParser.Parse("{\"_type\":\"playlist\",\"title\":\"Post\",\"entries\":[{\"title\":\"First clip\",\"uploader\":\"Creator\",\"duration\":8},{\"title\":\"Second clip\"}]}", Platform.Instagram);
            T.Check(carousel.Title == "First clip" && carousel.Duration == 8 && carousel.IsCollection && carousel.ItemIndex == 1, "carousel preview uses first item consistently");
            T.Check(MediaParser.Parse("{\"entries\":[{\"is_live\":true}]}", Platform.Other).IsLive, "live media within a collection remains detectable");
            T.Check(MediaParser.Parse("{}", Platform.Other).Title == "Video" && MediaParser.Parse("{}", Platform.Other).Channel == "", "missing third-party metadata does not claim YouTube");
            T.Rejects(() => MediaParser.Parse("{\"entries\":[]}", Platform.Other), ErrorCode.CollectionEmpty, "empty collection");
            T.Rejects(() => MediaParser.Parse("{\"entries\":[null]}", Platform.Other), ErrorCode.FirstItemUnavailable, "unavailable first collection entry");
            Uri thumbnailUri;
            T.Check(Platforms.TryPublicWebUrl("https://scontent.cdninstagram.com/image.jpg", out thumbnailUri), "third-party HTTPS thumbnail URL accepted");
            T.Check(!Platforms.TryPublicWebUrl("file:///C:/image.jpg", out thumbnailUri) && !Platforms.TryPublicWebUrl("https://127.0.0.1/image.jpg", out thumbnailUri), "local thumbnail targets rejected");
            // Recorded engine output
            var yt = MediaParser.Parse(T.Fixture("youtube.json"), Platform.YouTube);
            T.Check(yt.Platform == Platform.YouTube && yt.Duration == 19 && yt.Formats.Count > 10 && yt.HasAudio == Tri.Yes && yt.HasVideo == Tri.Yes, "recorded YouTube metadata: formats, audio and video");
            T.Check(yt.Subtitles.Any(s => s.Lang == "en" && !s.Automatic) && yt.Subtitles.Any(s => s.Automatic) && yt.Chapters.Count == 3, "recorded YouTube metadata: subtitles and chapters");
            T.Check(yt.Formats.Where(f => f.IsStoryboard).All(f => !yt.Usable.Contains(f)), "storyboards are not media formats");
            var ig = MediaParser.Parse(T.Fixture("instagram.json"), Platform.Instagram);
            T.Check(ig.HasAudio == Tri.No && ig.HasVideo == Tri.Yes, "recorded silent Instagram reel: no audio, has video");
            T.Check(ig.BestVideo.Width == 720 && ig.BestVideo.Height == 1280 && ig.BestVideo.ShortSide == 720 && ig.Orientation == "vertical", "vertical video measured by its short side (720x1280 is 720p)");
            var col = MediaParser.ParseCollection(T.Fixture("playlist-flat.json"), Platform.YouTube);
            T.Check(col.Title == "Test listesi" && col.Entries.Count == 6 && col.TotalCount == 5, "flat playlist entries parsed");
            T.Check(!col.Entries[1].Available && !col.Entries[2].Available && !col.Entries[3].Available && col.Entries[4].Title == null && col.Entries[0].Available, "private, deleted, members-only and missing entries flagged");
            var post2 = MediaParser.ParseCollection(T.Fixture("instagram-carousel.json"), Platform.Instagram);
            T.Check(post2.Entries.Count == 3 && post2.Entries[0].IsVideo == Tri.Yes && post2.Entries[1].IsVideo == Tri.No && post2.Entries[2].IsVideo == Tri.Yes, "Instagram multi-item post: video and photo items told apart");
            var flatArgs = Engine.ListArgs("https://www.youtube.com/playlist?list=PLx", true, 101, 200);
            T.Check(flatArgs.Contains("--flat-playlist") && flatArgs.Contains("--yes-playlist") && flatArgs[flatArgs.IndexOf("-I") + 1] == "101:200" && !flatArgs.Contains("--no-playlist") && !flatArgs.Contains("--playlist-items"), "list reading is paged and separate from the single-item download arguments");
            var postArgs = Engine.ListArgs("https://www.instagram.com/p/X/", false, 1, 0);
            T.Check(!postArgs.Contains("--flat-playlist") && !postArgs.Contains("-I") && postArgs.Last() == "https://www.instagram.com/p/X/", "multi-item post read with full item details");
        }

        static void ErrorsAndText() {
            T.Check(Engine.Classify("ERROR: Requested format is not available") == ErrorCode.FormatUnavailable && Loc.Error(ErrorCode.FormatUnavailable).Contains("biçim"), "format error guidance");
            T.Check(Engine.Classify("ERROR: Unsupported URL: https://example.com/") == ErrorCode.Unsupported && Loc.Error(ErrorCode.Unsupported).Contains("desteklenmiyor"), "unsupported site guidance");
            T.Check(Engine.Classify("ERROR: Login required. Use --cookies-from-browser") == ErrorCode.LoginRequired && Loc.Error(ErrorCode.LoginRequired).Contains("oturum") && !Loc.Error(ErrorCode.LoginRequired).Contains("YouTube"), "site-neutral login guidance");
            T.Check(Engine.Classify("ERROR: No video could be found in this post") == ErrorCode.NoVideo && Loc.Error(ErrorCode.NoVideo).Contains("video bulunamadı"), "photo-only post guidance");
            T.Check(Engine.Classify("ERROR: This video is DRM protected") == ErrorCode.Drm && Loc.Error(ErrorCode.Drm).Contains("DRM"), "DRM guidance");
            T.Check(Engine.Classify("ERROR: [TikTok] 6748451240264420610: Your IP address is blocked from accessing this post") == ErrorCode.IpBlocked && Loc.Error(ErrorCode.IpBlocked).Contains("IP adresinden"), "TikTok network block guidance");
            T.Check(Engine.Classify("ERROR: [vimeo] 76979871: The web client only works when logged-in. Use --cookies") == ErrorCode.LoginRequired, "recorded Vimeo sign-in error");
            T.Check(Engine.Classify("ERROR: unable to download video data: HTTP Error 403: Forbidden") == ErrorCode.Forbidden, "HTTP 403");
            T.Check(Engine.Classify("ERROR: HTTP Error 429: Too Many Requests") == ErrorCode.RateLimited && Errors.IsTransient(ErrorCode.RateLimited), "HTTP 429 is transient");
            T.Check(Engine.Classify("ERROR: [download] Got error: The read operation timed out") == ErrorCode.Network && Errors.IsTransient(ErrorCode.Network), "network error is transient");
            T.Check(Engine.Classify("ERROR: fragment 12 not found, unable to continue") == ErrorCode.FragmentsMissing, "missing fragments detected");
            T.Check(Engine.Classify("ERROR: unable to write data: [Errno 28] No space left on device") == ErrorCode.DiskFull && Errors.NeedsUser(ErrorCode.DiskFull), "disk full needs the user");
            T.Check(Engine.Classify("ERROR: [youtube] x: Private video. Sign in if you've been granted access") == ErrorCode.LoginRequired, "private video with sign-in hint");
            T.Check(Engine.Classify("ERROR: [youtube] x: Video unavailable") == ErrorCode.Unavailable && !Errors.IsTransient(ErrorCode.Unavailable), "unavailable video is not retried");
            T.Check(Engine.Classify("random text") == ErrorCode.Unknown, "unknown output stays unknown");
            T.Check(Errors.FromException(new OperationCanceledException()) == ErrorCode.Canceled && Errors.FromException(new TimeoutException()) == ErrorCode.Timeout, "exception to code mapping");
            // Diagnostics masking
            var masked = Engine.Mask("GET https://rr1.googlevideo.com/videoplayback?expire=1&sig=SECRET&v=abc&token=XYZ C:\\Users\\bm123\\file Cookie: SID=secret");
            T.Check(!masked.Contains("SECRET") && !masked.Contains("XYZ") && masked.Contains("v=abc") && !masked.Contains("bm123") && !masked.Contains("SID=secret"), "signed URL values, user name and cookies masked");
            // Translation table completeness
            var rows = Strings.Table;
            T.Check(rows.All(r => r.Length == 3 && !string.IsNullOrWhiteSpace(r[1]) && !string.IsNullOrWhiteSpace(r[2])), "every text has Turkish and English");
            T.Check(rows.Select(r => r[0]).Distinct().Count() == rows.Length, "no duplicate text keys");
            var placeholder = new System.Text.RegularExpressions.Regex(@"\{(\d+)\}");
            T.Check(rows.All(r => string.Join(",", placeholder.Matches(r[1]).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).Distinct().OrderBy(x => x)) == string.Join(",", placeholder.Matches(r[2]).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).Distinct().OrderBy(x => x))), "placeholders match between languages");
            T.Check(Enum.GetNames(typeof(ErrorCode)).All(n => Loc.Has("err." + n)), "every error code has a text");
            Loc.Set("en"); var en = Loc.Error(ErrorCode.DiskSpaceLow, Loc.Size(2147483648), Loc.Size(1048576)); Loc.Set("tr");
            T.Check(en.StartsWith("Not enough disk space") && en.Contains("2.00 GB") && Loc.Size(2147483648) == "2,00 GB", "language switch changes text and number formatting");
        }

        static void Processes() {
            var arguments = new[] { "", "plain", "C:\\a folder\\", "a\"quote", "Türkçe ğüşi", "$(secret)&whoami", "100%complete" };
            string self = Process.GetCurrentProcess().MainModule.FileName;
            var echo = ProcessRunner.Run(self, new[] { "echo" }.Concat(arguments), null, CancellationToken.None, 10).GetAwaiter().GetResult();
            T.Check(echo.ExitCode == 0 && Engine.Json().Deserialize<string[]>(echo.Output).SequenceEqual(arguments), "actual Windows argument round-trip / Unicode / quotes / shell metacharacters");
            int childId = 0;
            using (var cancel = new CancellationTokenSource(1500)) {
                try { ProcessRunner.Run(self, new[] { "wait-parent" }, line => { int.TryParse(line, out childId); }, cancel.Token, 30).GetAwaiter().GetResult(); T.Check(false, "process cancellation"); }
                catch (OperationCanceledException) { T.Check(true, "process cancellation"); }
            }
            bool gone;
            try { gone = Process.GetProcessById(childId).HasExited; } catch (ArgumentException) { gone = true; }
            T.Check(childId > 0 && gone, "child process terminated on cancel");
            try { ProcessRunner.Run(self, new[] { "wait-child" }, null, CancellationToken.None, 1).GetAwaiter().GetResult(); T.Check(false, "process timeout"); }
            catch (TimeoutException) { T.Check(true, "process timeout"); }
        }

        static AppPaths TestPaths(string name) {
            var root = T.Scratch(name);
            return new AppPaths { DataDir = Path.Combine(root, "Flow"), LegacyDir = Path.Combine(root, "Akis"), DefaultDownloads = Path.Combine(root, "Downloads", "Flow") };
        }

        static void StorageTests() {
            // Legacy (Akış 1.x) import
            var paths = TestPaths("import");
            Directory.CreateDirectory(paths.LegacyDir);
            var legacyFolder = Path.Combine(Path.GetDirectoryName(paths.LegacyDir), "Downloads", "Akis");
            Directory.CreateDirectory(legacyFolder);
            var existing = Path.Combine(legacyFolder, "Video by creator [Dd1MD8gArDV].mp4");
            File.WriteAllBytes(existing, new byte[4096]);
            var legacyJson = "{\"Folder\":" + Engine.Json().Serialize(legacyFolder) + ",\"Format\":\"mkv\",\"Height\":720,\"History\":[{\"Title\":\"Video by creator\",\"FilePath\":" + Engine.Json().Serialize(existing) + ",\"Summary\":\"28.09.2026  20:21  ·  MP4  ·  986 KB\"},{\"Title\":\"Mercan Köşk 4. Bölüm\",\"FilePath\":\"D:\\\\Yok\\\\Mercan Köşk 4. Bölüm [Ki9ZYd61MjI].mp4\",\"Summary\":\"28.09.2026  19:08  ·  MP4  ·  2351,7 MB\"},null,{\"Title\":\"no path\"}]}";
            File.WriteAllText(paths.LegacySettings, legacyJson, new UTF8Encoding(false));
            var before = T.Hash(paths.LegacySettings);
            var state = Store.Load(paths);
            T.Check(state.Imported && state.Settings.Folder == paths.DefaultDownloads && state.Settings.Format == "mkv" && state.Settings.Height == 720 && state.Settings.StrictQuality, "legacy settings imported with the Flow default folder, format and resolution bound");
            T.Check(state.History.Count == 2 && state.History.All(h => h.Legacy && h.SourceUrl == null && h.Platform == null), "legacy history imported without inventing source URLs");
            var first = state.History[0];
            T.Check(first.SizeBytes == 4096 && first.Format == "mp4" && first.ContentId == "Dd1MD8gArDV" && Iso.Parse(first.CompletedAt).Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm") == "28.09.2026 20:21", "legacy entry fields: date, format, real size, content id");
            T.Check(state.History[1].SizeBytes == (long)(2351.7 * 1048576), "legacy size parsed when the file is missing");
            T.Check(T.Hash(paths.LegacySettings) == before && File.Exists(existing) && first.FilePath == existing, "legacy settings, media and history file paths left untouched");
            T.Check(File.Exists(paths.SettingsFile) && File.Exists(paths.HistoryFile), "Flow data files written");
            var again = Store.Load(paths);
            T.Check(!again.Imported && again.History.Count == 2 && again.Settings.Folder == paths.DefaultDownloads, "import happens only once and the Flow folder persists");

            // Already-imported Flow settings also migrate, preserving the old setting as a backup.
            paths = TestPaths("old-default-folder");
            var oldSettings = SettingsRules.Defaults(paths);
            oldSettings.Folder = Path.Combine(Path.GetDirectoryName(paths.DefaultDownloads), "aKiS") + Path.DirectorySeparatorChar;
            oldSettings.Language = "en"; oldSettings.Height = 480;
            Store.SaveSettings(paths, oldSettings);
            before = T.Hash(paths.SettingsFile);
            var queueJson = "{\"Jobs\":[{\"Options\":{\"Folder\":" + Engine.Json().Serialize(oldSettings.Folder) + "}}]}";
            File.WriteAllText(paths.QueueFile, queueJson, Encoding.UTF8);
            state = Store.Load(paths);
            T.Check(state.Settings.Folder == paths.DefaultDownloads && state.Settings.Language == "en" && state.Settings.Height == 480, "existing Akis default migrates despite case or trailing separator; other settings survive");
            T.Check(Engine.Text(JsonStore.Load(paths.SettingsFile).Data, "Folder", null) == paths.DefaultDownloads && T.Hash(paths.SettingsFile + ".bak") == before, "renamed default is persisted with the previous settings backed up");
            T.Check(File.ReadAllText(paths.QueueFile, Encoding.UTF8) == queueJson, "default folder migration leaves queued job destinations untouched");
            before = T.Hash(paths.SettingsFile + ".bak");
            Store.Load(paths);
            T.Check(T.Hash(paths.SettingsFile + ".bak") == before, "reopening after folder migration does not overwrite its backup");

            paths = TestPaths("custom-legacy-folder");
            Directory.CreateDirectory(paths.LegacyDir);
            var customFolder = Path.Combine(Path.GetDirectoryName(paths.LegacyDir), "Custom media", "Akis");
            File.WriteAllText(paths.LegacySettings, Engine.Json().Serialize(new { Folder = customFolder }), Encoding.UTF8);
            state = Store.Load(paths);
            T.Check(state.Imported && state.Settings.Folder == customFolder && Store.Load(paths).Settings.Folder == customFolder, "custom folders named Akis outside the default location stay unchanged on import and reload");

            // Missing fields fall back to defaults individually
            paths = TestPaths("missing");
            Directory.CreateDirectory(paths.DataDir);
            File.WriteAllText(paths.SettingsFile, "{\"Language\":\"en\",\"Parallel\":7,\"Theme\":\"neon\"}", Encoding.UTF8);
            state = Store.Load(paths);
            T.Check(state.Settings.Language == "en" && state.Settings.Theme == "system" && state.Settings.Parallel == 1 && state.Settings.Height == 1080 && state.Settings.Retries == 2 && state.Settings.AutoPreview && state.Settings.Folder == paths.DefaultDownloads && !state.Imported, "missing / invalid fields use defaults, valid ones kept");

            // Damaged JSON: recover from the last good copy, keep the damaged file
            paths = TestPaths("corrupt");
            Directory.CreateDirectory(paths.DataDir);
            var good = SettingsRules.Defaults(paths); good.Language = "en"; good.Height = 480;
            Store.SaveSettings(paths, good);
            good.Height = 720; Store.SaveSettings(paths, good);
            T.Check(File.Exists(paths.SettingsFile + ".bak"), "previous version kept as backup on save");
            File.WriteAllText(paths.SettingsFile, "{\"Language\":\"en\",\"Heig", Encoding.UTF8);
            state = Store.Load(paths);
            T.Check(state.SettingsLoad.Recovered && state.Settings.Language == "en" && state.Settings.Height == 480, "damaged settings recovered from last good backup");
            T.Check(state.SettingsLoad.CorruptCopy != null && File.Exists(state.SettingsLoad.CorruptCopy) && File.ReadAllText(state.SettingsLoad.CorruptCopy).Contains("Heig"), "damaged file preserved, not overwritten");
            File.Delete(paths.SettingsFile + ".bak");
            File.WriteAllText(paths.SettingsFile, "\u0000\u0001garbage", Encoding.UTF8);
            state = Store.Load(paths);
            T.Check(state.SettingsLoad.Reset && !state.SettingsLoad.Recovered && state.Settings.Language == "system" && File.Exists(state.SettingsLoad.CorruptCopy), "damaged settings without backup: defaults plus a visible notice");
            File.WriteAllText(paths.HistoryFile, "[1,2,3]", Encoding.UTF8);
            state = Store.Load(paths);
            T.Check(state.History.Count == 0 && state.HistoryLoad.Reset, "damaged history does not crash");
            File.WriteAllText(paths.HistoryFile, "{\"SchemaVersion\":1,\"Entries\":[null,{\"Title\":\"x\",\"FilePath\":\"C:\\\\a.mp4\",\"SourceUrl\":\"file:///C:/x\"}]}", Encoding.UTF8);
            state = Store.Load(paths);
            T.Check(state.History.Count == 1 && state.History[0].SourceUrl == null && !string.IsNullOrEmpty(state.History[0].Id), "history entries sanitized (null rows, unsafe source URLs)");

            // Damaged legacy file is not imported and not touched
            paths = TestPaths("legacy-bad");
            Directory.CreateDirectory(paths.LegacyDir);
            File.WriteAllText(paths.LegacySettings, "{broken", Encoding.UTF8);
            state = Store.Load(paths);
            T.Check(state.ImportFailed && !state.Imported && File.ReadAllText(paths.LegacySettings) == "{broken", "unreadable legacy file reported, kept as is");

            // Environment variables
            var env = new Dictionary<string, string> { { "AKIS_DATA_DIR", "C:\\akis-test" }, { "AKIS_DOWNLOAD_DIR", "C:\\akis-dl" } };
            var resolved = AppPaths.Resolve(k => env.ContainsKey(k) ? env[k] : null);
            T.Check(resolved.DataDir == "C:\\akis-test" && resolved.LegacyDir == "C:\\akis-test" && resolved.DefaultDownloads == "C:\\akis-dl", "legacy AKIS_* test variables still honored");
            env["FLOW_DATA_DIR"] = "C:\\flow-test";
            T.Check(AppPaths.Resolve(k => env.ContainsKey(k) ? env[k] : null).DataDir == "C:\\flow-test", "FLOW_* variables take precedence");
            T.Check(Path.GetFileName(resolved.SettingsFile) != "settings.json", "Flow never writes over the Akış settings file name");
        }

        static void InstanceTests() {
            var dir = T.Scratch("instance");
            var first = InstanceLock.TryAcquire(dir);
            T.Check(first != null && InstanceLock.TryAcquire(dir) == null, "second instance in the same data folder refused");
            T.Check(InstanceLock.TryAcquire(T.Scratch("instance-other")) != null, "separate data folders do not block each other");
            var signaled = new ManualResetEvent(false);
            first.OnActivate(() => signaled.Set());
            T.Check(InstanceLock.SignalExisting(dir) && signaled.WaitOne(3000), "second start brings the first window forward");
            // A real second process
            string self = Process.GetCurrentProcess().MainModule.FileName;
            var child = Process.Start(new ProcessStartInfo(self, "hold-lock " + Engine.Quote(dir)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true, CreateNoWindow = true });
            T.Check(child.StandardOutput.ReadLine() == "BUSY", "another process is refused while the lock is held");
            child.StandardInput.WriteLine(); child.WaitForExit(5000);
            first.Dispose();
            child = Process.Start(new ProcessStartInfo(self, "hold-lock " + Engine.Quote(dir)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true, CreateNoWindow = true });
            T.Check(child.StandardOutput.ReadLine() == "LOCKED", "lock released on exit and taken by the next process");
            T.Check(InstanceLock.TryAcquire(dir) == null, "this process is refused while the other process holds it");
            child.StandardInput.WriteLine(); child.WaitForExit(5000);
        }

        static void NamingTests() {
            T.Check(Naming.Sanitize("a<b>c:d\"e/f\\g|h?i*j") == "a＜b＞c：d＂e／f＼g｜h？i＊j", "invalid file name characters replaced");
            T.Check(Naming.Sanitize("CON") == "_CON" && Naming.Sanitize("nul.txt") == "_nul.txt" && Naming.Sanitize("  ...  ") == "video" && Naming.Sanitize("name. ") == "name", "reserved names and trailing dots");
            var longName = Naming.Sanitize(new string('ğ', 400));
            T.Check(longName.Length <= Naming.MaxNameLength + 1, "long titles shortened");
            T.Check(Naming.Sanitize("😀" + new string('a', 148) + "😀😀").Length <= Naming.MaxNameLength + 1 && !char.IsHighSurrogate(Naming.Truncate("a" + "😀😀😀", 3).TrimEnd('…').Last()), "surrogate pairs never split");
            T.Check(Naming.IsValidTemplate("{title} [{id}]") && Naming.IsValidTemplate("{channel} - {title}") && !Naming.IsValidTemplate("{foo}") && !Naming.IsValidTemplate("sub/{title}") && !Naming.IsValidTemplate("{channel}") && !Naming.IsValidTemplate(""), "name templates validated");
            var values = new Dictionary<string, string> { { "title", "100% Test: bölüm" }, { "id", "abc" }, { "channel", "" } };
            T.Check(Naming.Expand("{title} [{id}]", values) == "100% Test： bölüm [abc]" && Naming.Expand("{channel} - {title}", values) == "100% Test： bölüm", "templates expand, empty parts disappear");
            T.Check(Naming.EngineTemplate("100% done") == "100%% done", "percent signs escaped for the engine");
            T.Check(Naming.QualityTag(1080, 60) == "1080p60" && Naming.QualityTag(720, 30) == "720p" && Naming.QualityTag(0, 0) == "", "quality tags");
            T.Check(Naming.ClipTag(65, 3725) == "01m05s-1h02m05s", "clip tags");
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "C:\\d\\a.mp4", "C:\\d\\a (2).mp4" };
            T.Check(Naming.Unique("C:\\d", "a", "mp4", taken.Contains) == "C:\\d\\a (3).mp4", "rename picks the first free name");
        }
    }
}
