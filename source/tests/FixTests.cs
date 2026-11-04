using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Flow;
using Flow.BrowserIntegration;

namespace FlowTests {
    // Checks for the findings confirmed in the 2026-11 review.
    public static class FixUnit {
        public static void Run() {
            Errors_();
            Classification();
            Selection_();
            Progress();
            Paths_();
            Subtitles();
            ImageRules();
            Gate();
            QueueRules();
            Runner();
            Pipes();
            Palette();
            Overlays();
        }

        static void Errors_() {
            Loc.Set("tr");
            T.Check(!Loc.Error(ErrorCode.VerifyFailed).Contains("{0}"), "an error saved without its arguments never shows {0}");
            var row = new HistoryRow { Entry = new HistoryEntry { Status = "failed", Error = "VerifyFailed", ErrorArgs = new List<string> { "verify.empty" } } };
            var turkish = row.ErrorText;
            T.Check(turkish.Contains(Loc.T("verify.empty")) && !turkish.Contains("verify.empty") && !turkish.Contains("{0}"), "history shows a failed job's reason (" + turkish + ")");
            Loc.Set("en");
            var english = row.ErrorText;
            T.Check(english != turkish && english.Contains(Loc.T("verify.empty")), "the saved reason follows the language");
            Loc.Set("tr");
            var failure = new MediaInfo { Duration = 60 };
            FlowException caught = null;
            try { EngineExecutor.Verify(Media.Opt("compatible", "mp4", 0, "h264"), new Selection(), failure, new ProbeResult { Size = 0 }); } catch (FlowException ex) { caught = ex; }
            T.Check(caught != null && caught.Args.Length == 1 && caught.Args[0] == "verify.empty", "verification failures store a text key, not translated text");
        }

        static void Classification() {
            T.Check(Engine.Classify("ERROR: [youtube] xdrm12: Video unavailable") == ErrorCode.Unavailable, "a video id containing 'drm' is not a DRM error");
            T.Check(Engine.Classify("ERROR: [site] abc: HTTP Error 404: https://cdn.example.com/drm/login/x") == ErrorCode.Unavailable, "an address in the message does not decide the error");
            T.Check(Engine.Classify("ERROR: [site] classless: This video is DRM protected") == ErrorCode.Drm, "a real DRM message still is one");
            T.Check(Engine.Classify("ERROR: [site] classless: Unable to extract title") == ErrorCode.Unknown, "'ssl' inside a word is not a network error");
            T.Check(Engine.Classify("ERROR: unable to download: <urlopen error [SSL: CERTIFICATE_VERIFY_FAILED]>") == ErrorCode.Network, "a real SSL failure stays a network error");
            T.Check(Engine.Classify("ERROR: [download] Got error: ProxyError: refused") == ErrorCode.Network, "a real proxy failure stays a network error");
        }

        static void Selection_() {
            var opus = Media.Make(60, "m|mp4|avc1.4d401f|opus|1280|720|30|1000|5000000");
            var converted = FormatSelector.Select(opus, Media.Opt("compatible", "mp4", 0, "h264"));
            T.Check(converted.VideoConversion, "compatible MP4 converts a muxed file whose sound is not AAC");
            var aac = Media.Make(60, "m|mp4|avc1.4d401f|mp4a.40.2|1280|720|30|1000|5000000");
            var direct = FormatSelector.Select(aac, Media.Opt("compatible", "mp4", 0, "h264"));
            T.Check(!direct.VideoConversion && !direct.ConvertIfNeeded, "a muxed H.264 and AAC file is not converted");
        }

        static void Progress() {
            var selection = new Selection { Video = new FormatInfo { Id = "v", Tbr = 800 } };
            var snapshot = new ProgressTracker(selection, 100).Update(new ProgressEvent { FormatId = "v", Downloaded = 5000000, Status = "downloading" });
            T.Check(snapshot.Total == 10000000 && Math.Abs(snapshot.Percent - 50) < 0.001, "a bit rate and duration estimate the size when the engine gives none");
            var none = new ProgressTracker(selection).Update(new ProgressEvent { FormatId = "v", Downloaded = 5000000, Status = "downloading" });
            T.Check(none.Total < 0, "without a duration no size is invented");
        }

        static void Paths_() {
            var folder = "C:\\" + new string('f', 100);
            var name = OutputPlanner.Fit(folder, new string('n', 300), "mp4");
            var part = Path.Combine(folder, ".flow-parts", new string('i', 32), name + ".f399.mp4.part");
            T.Check(part.Length < 260, "unfinished parts also stay under the Windows path limit (" + part.Length + ")");
        }

        static void Subtitles() {
            T.Check(Engine.SafeSubLang("en") && Engine.SafeSubLang("zh-Hans") && Engine.SafeSubLang("live_chat"), "real subtitle language keys pass");
            T.Check(!Engine.SafeSubLang("-all") && !Engine.SafeSubLang("en,tr") && !Engine.SafeSubLang("en\n") && !Engine.SafeSubLang(new string('a', 40)) && !Engine.SafeSubLang(null), "keys the engine would interpret are dropped");
            var options = Media.Opt("compatible", "mp4", 0, "h264"); options.SubLangs = new List<string> { "en", "--exec=x" }; options.SubMode = "file";
            var media = Media.Make(60, "m|mp4|avc1.4d401f|mp4a.40.2|1280|720|30|1000|5000000");
            var args = Engine.JobArgs(new DownloadJob { Url = "https://example.com/v", Platform = "Other", Options = options }, FormatSelector.Select(media, options), "C:\\out", "n", "C:\\out\\t");
            T.Check(args[args.IndexOf("--sub-langs") + 1] == "en", "only clean keys reach the engine");
            options.SubLangs = new List<string> { "--exec=x" };
            args = Engine.JobArgs(new DownloadJob { Url = "https://example.com/v", Platform = "Other", Options = options }, FormatSelector.Select(media, options), "C:\\out", "n", "C:\\out\\t");
            T.Check(!args.Contains("--sub-langs"), "no subtitle arguments when no key is left");
        }

        static void ImageRules() {
            T.Check(ImageTransfer.StatusError(HttpStatusCode.NotFound) == ErrorCode.Unavailable && ImageTransfer.StatusError(HttpStatusCode.Gone) == ErrorCode.Unavailable, "a removed image is not retried as a network error");
            T.Check(ImageTransfer.StatusError((HttpStatusCode)429) == ErrorCode.RateLimited && Errors.IsTransient(ErrorCode.RateLimited), "HTTP 429 waits and retries as a rate limit");
            T.Check(ImageTransfer.StatusError(HttpStatusCode.BadGateway) == ErrorCode.None, "server errors stay network errors");
            T.Check(ImageTransfer.GuardConnection(null, new IPEndPoint(IPAddress.Parse("93.184.216.34"), 443), 0) == null, "a public address may be dialled");
            T.Rejects(() => ImageTransfer.GuardConnection(null, new IPEndPoint(IPAddress.Parse("192.168.1.10"), 443), 0), ErrorCode.UrlNotPublic, "a private address is refused at connect time");
        }

        static void Gate() {
            var token = CancellationToken.None;
            var lease = EngineGate.Enter(token).Result;
            var begin = EngineGate.BeginUpdate(10000);
            T.Check(!begin.IsCompleted && EngineGate.Updating, "the update waits for a running read");
            var waiting = EngineGate.Enter(token);
            lease.Dispose();
            UiTests.Wait(() => begin.IsCompleted, 3000);
            T.Check(begin.Status == TaskStatus.RanToCompletion && !waiting.IsCompleted, "a read arriving during the update waits for it");
            var canceled = new CancellationTokenSource();
            var given = EngineGate.Enter(canceled.Token);
            canceled.Cancel();
            UiTests.Wait(() => given.IsCompleted, 3000);
            T.Check(given.IsCanceled, "a waiting read can be cancelled");
            EngineGate.EndUpdate();
            UiTests.Wait(() => waiting.IsCompleted, 3000);
            T.Check(waiting.Status == TaskStatus.RanToCompletion && !EngineGate.Updating, "the read continues after the update");
            waiting.Result.Dispose();
            var held = EngineGate.Enter(token).Result;
            var late = EngineGate.BeginUpdate(50);
            UiTests.Wait(() => late.IsCompleted, 3000);
            T.Check(late.IsFaulted && !EngineGate.Updating, "a read that does not end cancels the update instead of breaking it");
            held.Dispose();
            var root = T.Scratch("restore"); Directory.CreateDirectory(root);
            string backup = Path.Combine(root, "b.exe"), exe = Path.Combine(root, "a.exe");
            File.WriteAllText(backup, "good"); File.WriteAllText(exe, "bad");
            var locked = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var restore = Components.Restore(backup, exe);
            Thread.Sleep(50); locked.Dispose();
            UiTests.Wait(() => restore.IsCompleted, 5000);
            T.Check(restore.Status == TaskStatus.RanToCompletion && File.ReadAllText(exe) == "good", "restoring the engine survives a briefly locked file");
        }

        static void QueueRules() {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            var root = T.Scratch("fix-queue");
            var paths = new AppPaths { DataDir = Path.Combine(root, "data"), LegacyDir = Path.Combine(root, "old"), DefaultDownloads = Path.Combine(root, "out") };
            Directory.CreateDirectory(paths.DefaultDownloads);
            var settings = SettingsRules.Defaults(paths); settings.Parallel = 1;
            var q = new QueueService(paths, () => settings, new FakeExecutor());
            Func<string, string, DownloadJob> job = (title, format) => { var o = Media.Opt(format == "mp3" ? "mp3" : "compatible", format, 0, "auto"); o.Folder = paths.DefaultDownloads; return new DownloadJob { Url = "https://example.com/v/" + title, Title = title, Platform = "Other", Options = o }; };
            q.Held = true;
            var waiting = q.Add(job("okHeld", "mp4"), false);
            q.Pump();
            T.Check(waiting.Job.State == JobState.Queued && q.ActiveCount == 0, "nothing starts while the engine is being replaced");
            q.Held = false; q.Pump();
            UiTests.Wait(() => waiting.Job.State == JobState.Completed, 4000);
            T.Check(waiting.Job.State == JobState.Completed, "held work starts when the update ends");

            q.Held = true;
            var failed = q.Add(job("x", "mp4"), true);
            failed.Job.Id = "partsjob"; failed.Job.State = JobState.Failed;
            failed.Job.TempDir = EngineExecutor.PartsFolder(paths.DefaultDownloads, failed.Job.Id);
            File.WriteAllText(Path.Combine(failed.Job.TempDir, "a.part"), "x");
            q.ClearFinished();
            T.Check(!Directory.Exists(failed.Job.TempDir) && q.Items.Count == 0, "clearing finished jobs removes a failed job's parts");

            var audio = q.Add(job("sound", "mp3"), true);
            audio.Job.State = JobState.Failed; audio.Job.Error = "VerifyFailed"; audio.Job.ErrorArgs = new List<string> { "verify.codec" };
            T.Check(audio.CanFixFormat, "a failed audio job can change format");
            q.ChangeFormat(audio);
            T.Check(audio.Job.Options.Format == "m4a" && audio.Job.Options.IsAudio && audio.Job.ErrorArgs == null, "changing an audio job's format keeps it audio");
            var picture = q.Add(job("picture", "mp4"), true);
            picture.Job.MediaKind = "image"; picture.Job.State = JobState.Failed; picture.Job.Error = "VerifyFailed";
            T.Check(!picture.CanFixFormat, "an image offers no format change");
            q.Shutdown();
        }

        static void Runner() {
            var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            int lines = 0;
            var script = Path.Combine(T.Scratch("fix-runner"), "lines.cmd"); Directory.CreateDirectory(Path.GetDirectoryName(script));
            File.WriteAllLines(script, new[] { "@echo off", "for /L %%i in (1,1,6000) do echo " + new string('a', 120) });
            var result = ProcessRunner.Run(cmd, new[] { "/c", script }, l => lines++, CancellationToken.None, 60).Result;
            T.Check(lines >= 6000 && result.Output.Length <= 512 * 1024 + 1024 && result.Output.Length > 1000, "a long run keeps only the tail of its output (" + result.Output.Length + " of " + lines + " lines)");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            T.Check(!new SchtasksRegistrar().Exists("Flow\\does-not-exist-" + Guid.NewGuid().ToString("N")) && watch.Elapsed.TotalSeconds < 15, "schtasks answers without blocking on its streams");
        }

        static void Pipes() {
            var name = "Flow.Test." + Guid.NewGuid().ToString("N");
            var acl = new PipeSecurity();
            acl.AddAccessRule(new PipeAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            using (var first = FirstPipe.Create(name, 8, 4096, acl)) {
                bool refused = false;
                try { FirstPipe.Create(name, 8, 4096, acl).Dispose(); } catch (IOException) { refused = true; }
                T.Check(refused, "a pipe name that already exists is refused");
                var waiting = first.WaitForConnectionAsync();
                using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous)) {
                    client.Connect(2000);
                    waiting.Wait(2000);
                    T.Check(BrowserProtocol.OwnedByCurrentUser(client), "the host recognises a server of its own account");
                    BrowserProtocol.Write(client, "{\"a\":1}");
                    var read = BrowserProtocol.ReadAsync(first, BrowserProtocol.MaxRequest, CancellationToken.None);
                    T.Check(read.Wait(2000) && read.Result == "{\"a\":1}", "the first-instance pipe carries messages");
                }
            }
            var dir = T.Scratch("fix-pipe");
            var server = new BrowserPipeServer(dir, (json, token) => Task.FromResult(json));
            server.Start();
            var pipe = BrowserProtocol.PipeName(dir);
            string[] answers = new string[2];
            for (int i = 0; i < 2; i++)
                using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous)) {
                    client.Connect(3000);
                    BrowserProtocol.Write(client, "{\"n\":" + i + "}");
                    var reply = BrowserProtocol.ReadAsync(client, BrowserProtocol.MaxResponse, CancellationToken.None);
                    if (reply.Wait(3000)) answers[i] = reply.Result;
                }
            server.Dispose();
            T.Check(answers[0] == "{\"n\":0}" && answers[1] == "{\"n\":1}", "the browser server answers through its first and later instances");
        }

        static void Palette() {
            foreach (var name in new[] { "light", "dark" }) {
                var p = Theme.Palette(name);
                T.Check(p["OnSidebarActive"] == p["SidebarText"] && p["OnSurfaceHover"] == p["Text"], name + " theme: the plate inks are the ordinary inks");
            }
            var contrast = Theme.Palette("contrast");
            T.Check(contrast["OnSidebarActive"] == SystemColors.HighlightTextColor && contrast["OnSurfaceHover"] == SystemColors.HighlightTextColor && contrast["SidebarActive"] == SystemColors.HighlightColor && contrast["SurfaceHover"] == SystemColors.HighlightColor, "high contrast: text on highlight plates uses the highlight text colour");
        }

        // The press layer sits over the hover layer. White on the dark theme's accent stays readable under both.
        static void Overlays() {
            var xaml = Xaml();
            int buttonEnd = xaml.IndexOf("x:Key=\"Primary\"", StringComparison.Ordinal);
            var button = xaml.Substring(0, buttonEnd);
            var hover = Regex.Matches(button, "TargetName=\"Hover\" Storyboard.TargetProperty=\"Opacity\" To=\"([0-9.]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            var press = Regex.Matches(button, "TargetName=\"Press\" Storyboard.TargetProperty=\"Opacity\" To=\"([0-9.]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            T.Check(hover.Count == 2 && hover.Contains("0") && press.Count == 2 && press.Contains("0"), "hover and press each return to nothing on their own, so a keyboard press leaves no layer on");
            var p = Theme.Palette("dark");
            double a = double.Parse(hover.First(v => v != "0"), CultureInfo.InvariantCulture), b = double.Parse(press.First(v => v != "0"), CultureInfo.InvariantCulture);
            Func<Color, Color, double, Color> over = (under, ink, alpha) => Color.FromRgb((byte)(under.R + (ink.R - under.R) * alpha), (byte)(under.G + (ink.G - under.G) * alpha), (byte)(under.B + (ink.B - under.B) * alpha));
            var pressed = over(over(p["Accent"], p["Text"], a), p["Text"], b);
            double ratio = Theme.Contrast(p["OnAccent"], pressed);
            T.Check(ratio >= 4.5, "dark theme: a pressed primary button keeps its contrast (" + ratio.ToString("0.00") + ")");
        }
        static string Xaml() {
            using (var stream = typeof(Controller).Assembly.GetManifestResourceStream("MainWindow.xaml"))
            using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }
    }

    public static partial class UiTests {
        static void FixUi() {
            var paths = NewPaths("ui-fixes");
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = "light";
            Store.SaveSettings(paths, s);
            Directory.CreateDirectory(paths.DefaultDownloads);
            var kept = Path.Combine(paths.DefaultDownloads, "kept.mp4"); File.WriteAllText(kept, "x");
            Store.SaveHistory(paths, new List<HistoryEntry> {
                new HistoryEntry { Id = "1", Title = "Kept", Platform = "Other", SourceUrl = "https://vimeo.com/okAgain", FilePath = kept, CompletedAt = Iso.Now(), Status = "completed", Format = "mp4", Profile = "custom", RequestedCodec = "h264", RequestedHeight = 480, RequestedFps = 30, RequestedStrict = true },
                new HistoryEntry { Id = "2", Title = "Old", Platform = "Other", SourceUrl = "https://vimeo.com/okOld", CompletedAt = Iso.Now(), Status = "completed", Format = "mp4", Profile = "custom" }
            });
            var c = Open(paths, 1180, 840);
            var w = c.Window;

            // History: typing reuses what was read from disk; opening the page again reads it afresh.
            c.ShowPage("history"); Pump();
            T.Check(c.HistoryRows.First(r => r.Entry.Id == "1").Exists, "history sees the file");
            File.Delete(kept);
            Find<TextBox>(w, "HistorySearch").Text = "Kept"; Pump();
            T.Check(c.HistoryRows.Count == 1 && c.HistoryRows[0].Exists, "searching history does not re-read the disk on each key");
            Find<TextBox>(w, "HistorySearch").Text = ""; Pump();
            c.ShowPage("queue"); Pump(); c.ShowPage("history"); Pump();
            T.Check(!c.HistoryRows.First(r => r.Entry.Id == "1").Exists, "opening history again sees the file is gone");

            // Downloading a history entry again asks for what it asked for; older entries follow the settings.
            var again = c.Requeue(c.HistoryRows.First(r => r.Entry.Id == "1").Entry);
            T.Check(again.Job.Options.Codec == "h264" && again.Job.Options.Height == 480 && again.Job.Options.FpsLimit == 30 && again.Job.Options.Strict, "downloading again keeps the original quality choices");
            var old = c.Requeue(c.HistoryRows.First(r => r.Entry.Id == "2").Entry);
            T.Check(old.Job.Options.Height == s.Height && old.Job.Options.Codec == s.Codec, "older history entries follow the current settings");

            // The schedule's day list follows the calendar while Flow stays open.
            var box = Find<ComboBox>(w, "ScheduleDateBox");
            var tomorrow = DateTime.Today.AddDays(1);
            c.Today = () => tomorrow;
            c.ShowPage("download"); Pump();
            var expander = Find<Expander>(w, "ScheduleExpander"); expander.IsExpanded = true; Pump();
            box.IsDropDownOpen = true; Pump();
            var first = box.Items.OfType<FrameworkElement>().First();
            T.Check(Convert.ToString(first.Tag) == tomorrow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "after midnight the list starts from the new today");
            box.IsDropDownOpen = false; Pump();
            c.Today = () => DateTime.Today;

            // The queue badge keeps a plate of its own on the selected tab.
            Find<Border>(w, "QueueBadge").Visibility = Visibility.Visible;
            c.ShowPage("download"); Pump();
            var idle = ((SolidColorBrush)Find<Border>(w, "QueueBadge").Background).Color;
            c.ShowPage("queue"); Pump();
            var selected = ((SolidColorBrush)Find<Border>(w, "QueueBadge").Background).Color;
            var plate = Theme.Palette("light");
            T.Check(idle == plate["SidebarActive"] && selected == plate["SidebarHover"], "the queue badge stands out from the selected tab");

            // A full percentage fits its cell (52 px column, 10 px gap, 2 px margins).
            var dip = VisualTreeHelper.GetDpi(w).PixelsPerDip;
            var full = new FormattedText("100%", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.Light, FontStretches.Normal), 15, Brushes.Black, null, TextFormattingMode.Display, dip);
            T.Check(full.WidthIncludingTrailingWhitespace <= 38, "100% fits the queue's numeral cell (" + full.WidthIncludingTrailingWhitespace.ToString("0.0") + " px)");

            // The rail's bitmap follows the window's height, also with animations off.
            var rail = c.RailField;
            int before = rail.CanvasHeight;
            Resize(w, 1180, 700);
            T.Check(rail.CanvasHeight > 0 && rail.CanvasHeight != before, "the rail's bitmap follows the window height (" + before + " to " + rail.CanvasHeight + ")");
            w.Close(); Pump();
        }
    }
}
