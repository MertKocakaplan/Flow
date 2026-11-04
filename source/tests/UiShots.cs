using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flow;

namespace FlowTests {
    // Design shots for the UI rewrite: the same views at two window sizes, two DPI scales and both
    // themes, rendered independently of the monitor's DPI. Output: %FLOW_TEST_OUT%\screens\redesign.
    public static partial class UiTests {
        static readonly int[][] ShotSizes = { new[] { 1180, 840 }, new[] { 760, 560 } };
        static readonly string[] ShotThemes = { "light", "dark" };

        public static string RenderScaled(Window window, string name, double scale) {
            Pump();
            var content = (FrameworkElement)window.Content;
            double width = content.ActualWidth, height = content.ActualHeight;
            var bitmap = new RenderTargetBitmap((int)Math.Round(width * scale), (int)Math.Round(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) {
                dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
            }
            bitmap.Render(visual);
            var dir = Path.Combine(Environment.GetEnvironmentVariable("FLOW_TEST_OUT") ?? Path.GetTempPath(), "screens", "redesign");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
            return path;
        }

        static void Shot(Window window, string prefix, string theme) {
            foreach (var scale in new[] { 1.0, 1.5 }) {
                var name = prefix + "-" + (scale == 1.0 ? "100" : "150") + "-" + theme;
                var path = RenderScaled(window, name, scale);
                T.Check(File.Exists(path) && new FileInfo(path).Length > 0, "tasarım çekimi " + name);
            }
        }

        static void RedesignShots() {
            foreach (var theme in ShotThemes) {
                foreach (var size in ShotSizes) ShotViews(theme, size[0], size[1]);
                ShotCloseDialog(theme);
            }
            Loc.Set("tr");
            Theme.Set("light");
        }

        static List<HistoryEntry> ShotHistory(AppPaths paths) {
            Directory.CreateDirectory(paths.DefaultDownloads);
            var kept = Path.Combine(paths.DefaultDownloads, "Kalan video [abc] 720p.mp4");
            File.WriteAllText(kept, "x");
            return new List<HistoryEntry> {
                new HistoryEntry { Id = "1", Title = "Kalan video", Platform = "YouTube", SourceUrl = "https://www.youtube.com/watch?v=jNQXAC9IVRw", FilePath = kept, CompletedAt = Iso.Now(), Status = "completed", Format = "mp4", Profile = "compatible", Width = 1280, Height = 720, SizeBytes = 5242880, Channel = "Kanal A" },
                new HistoryEntry { Id = "2", Title = "Taşınmış dosya", Platform = "Instagram", SourceUrl = "https://www.instagram.com/reel/Chunk8-jurw/", FilePath = Path.Combine(paths.DefaultDownloads, "yok.mp4"), CompletedAt = Iso.From(DateTime.UtcNow.AddDays(-3)), Status = "completed", Format = "mp4" },
                new HistoryEntry { Id = "3", Title = "Engellenen video", Platform = "TikTok", SourceUrl = "https://www.tiktok.com/@a/video/1", CompletedAt = Iso.From(DateTime.UtcNow.AddDays(-10)), Status = "failed", Error = "IpBlocked", Format = "mp4" },
                new HistoryEntry { Id = "4", Title = "Eski Akış kaydı", FilePath = kept, CompletedAt = Iso.From(DateTime.UtcNow.AddDays(-40)), Status = "completed", Format = "mp3", Legacy = true }
            };
        }

        // Fills the queue with one job in each state: completed, downloading, waiting, paused, failed, needs action.
        static void ShotQueue(Controller c) {
            var url = Find<TextBox>(c.Window, "UrlBox");
            c.Enqueue(false);
            foreach (var name in new[] { "slow1", "slow2" }) { url.Text = "https://vimeo.com/" + name; Pump(); var job = c.Enqueue(false); job.Job.Title = name; }
            url.Text = "https://vimeo.com/held"; Pump();
            c.Enqueue(true);
            var failed = c.Queue.Add(new DownloadJob { Url = "https://vimeo.com/x", Platform = "Other", Title = "Kaldırılmış video", Options = Media.Opt("compatible", "mp4", 1080, "h264") }, true);
            failed.Job.State = JobState.Failed; failed.Job.Error = "Unavailable"; failed.Refresh();
            var disk = c.Queue.Add(new DownloadJob { Url = "https://vimeo.com/y", Platform = "Other", Title = "Büyük dosya", Options = Media.Opt("best", "mkv", 0, "auto") }, true);
            disk.Job.State = JobState.NeedsAction; disk.Job.Error = "DiskSpaceLow"; disk.Job.ErrorArgs = new List<string> { "4,2 GB", "1,1 GB" }; disk.Refresh();
            Wait(() => c.Queue.Items.Any(i => i.Job.Title == "slow1" && i.IsActive && i.ProgressValue > 3), 5000);
        }

        static void ShotViews(string theme, int width, int height) {
            var paths = NewPaths("ui-shots-" + theme + "-" + width);
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = theme; s.Parallel = 1; s.Notify = false;
            Store.SaveSettings(paths, s);
            Store.SaveHistory(paths, ShotHistory(paths));
            var c = Open(paths, width, height, new FakeExecutor());
            var w = c.Window;
            var size = width + "x" + height;
            Shot(w, "download-empty-" + size, theme);
            c.InfoSource = (u, token) => Task.FromResult(MediaParser.Parse(T.Fixture("youtube.json"), Platform.YouTube));
            Find<TextBox>(w, "UrlBox").Text = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
            Wait(() => Find<TextBlock>(w, "VideoTitle").Text != Loc.T("preview.empty.title") && !Find<TextBlock>(w, "VideoTitle").Text.StartsWith(Loc.T("preview.loading")) && Find<WrapPanel>(w, "Chips").Children.Count > 0, 4000);
            Shot(w, "download-preview-" + size, theme);
            ShotQueue(c);
            c.ShowPage("queue"); Pump();
            Shot(w, "queue-" + size, theme);
            c.ShowPage("history"); Pump();
            Shot(w, "history-" + size, theme);
            c.ShowPage("settings"); Pump();
            Shot(w, "settings-" + size, theme);
            w.Close();
        }

        static void ShotCloseDialog(string theme) {
            var paths = NewPaths("ui-shots-close-" + theme);
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = theme; s.Notify = false;
            Store.SaveSettings(paths, s);
            var c = Open(paths, 1000, 760);
            var dialog = new CloseDialog(c.Window, 2);
            var window = dialog.Window;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000; window.Top = -32000; window.ShowActivated = false;
            window.Show(); Pump();
            Shot(window, "close", theme);
            window.Close();
            c.Window.Close();
        }
    }
}
