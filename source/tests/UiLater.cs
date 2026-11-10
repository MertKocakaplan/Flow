using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Flow;

namespace FlowTests {
    public static partial class UiTests {
        static partial void RunLaterStages() {
            Stage3Ui();
            Stage4Ui();
            Stage4History();
            Stage5Ui();
            Stage6Ui();
            Stage7Ui();
            FixUi();
            NextUi();
            LinkUi();
        }

        static void Stage3Ui() {
            var paths = NewPaths("ui-queue");
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = "light"; s.Parallel = 1;
            Store.SaveSettings(paths, s);
            var fake = new FakeExecutor();
            var c = Open(paths, 1180, 900, fake);
            var w = c.Window;
            T.Check(Find<RadioButton>(w, "NavQueue") != null && Find<Border>(w, "QueueEmpty").Visibility == Visibility.Visible, "queue page exists and starts empty");
            // Preview-backed choice summary
            c.InfoSource = (u, token) => Task.FromResult(MediaParser.Parse(T.Fixture("youtube.json"), Platform.YouTube));
            var url = Find<TextBox>(w, "UrlBox");
            url.Text = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
            Wait(() => Find<TextBlock>(w, "VideoTitle").Text != Loc.T("preview.empty.title") && !Find<TextBlock>(w, "VideoTitle").Text.StartsWith(Loc.T("preview.loading")) && Find<WrapPanel>(w, "Chips").Children.Count > 0, 4000);
            T.Check(c.SelectionText.Contains("320×240") && c.SelectionText.Contains("H.264 + AAC"), "choice summary shows the real stream that will be downloaded (" + c.SelectionText + ")");
            T.Check(Find<TextBlock>(w, "NamePreview").Text.Contains("[jNQXAC9IVRw] 240p.mp4"), "file name preview includes quality");
            Console.WriteLine("SCREEN " + Render(w, "stage3-download-preview"));
            // Add while nothing runs, then more while one runs
            var first = c.Enqueue(false);
            T.Check(first != null && url.Text == "" && c.MessageTextValue.Contains(first.Title), "added to the queue with a confirmation, link box cleared");
            T.Check(first.Media != null, "fresh preview metadata travels with the job");
            foreach (var name in new[] { "slow1", "slow2" }) { url.Text = "https://vimeo.com/" + name; Pump(); var job = c.Enqueue(false); job.Job.Title = name; }
            url.Text = "https://vimeo.com/held"; Pump();
            var held = c.Enqueue(true);
            T.Check(held.Job.State == JobState.Paused, "Add for later keeps the job paused");
            Wait(() => c.Queue.Items.Any(i => i.Job.Title == "slow1" && i.IsActive && i.ProgressValue > 3), 5000);
            c.ShowPage("queue"); Pump();
            T.Check(Find<Border>(w, "QueueBadge").Visibility == Visibility.Visible && Find<TextBlock>(w, "QueueBadgeText").Text != "0", "sidebar badge counts waiting and running jobs");
            T.Check(Find<Border>(w, "SidebarActivity").Visibility == Visibility.Visible, "sidebar shows overall activity");
            // A failed job with actions
            var failed = c.Queue.Add(new DownloadJob { Url = "https://vimeo.com/x", Platform = "Other", Title = "Kaldırılmış video", Options = Media.Opt("compatible", "mp4", 1080, "h264") }, true);
            failed.Job.State = JobState.Failed; failed.Job.Error = "Unavailable"; failed.Refresh();
            var disk = c.Queue.Add(new DownloadJob { Url = "https://vimeo.com/y", Platform = "Other", Title = "Büyük dosya", Options = Media.Opt("best", "mkv", 0, "auto") }, true);
            disk.Job.State = JobState.NeedsAction; disk.Job.Error = "DiskSpaceLow"; disk.Job.ErrorArgs = new List<string> { "4,2 GB", "1,1 GB" }; disk.Refresh();
            Pump();
            var buttons = Visuals(w).OfType<Button>().Where(b => b.IsVisible && b.DataContext == disk).Select(b => Convert.ToString(b.Tag)).ToList();
            T.Check(buttons.Contains("folder") && buttons.Contains("resume") && buttons.Contains("copy") && !buttons.Contains("pause"), "needs-action job offers change folder / continue / copy details");
            Console.WriteLine("SCREEN " + Render(w, "stage3-queue-tr-light"));
            // Language switch rewrites job texts
            c.ShowPage("settings"); Pump();
            SelectByTag(Find<ComboBox>(w, "LanguageBox"), "en"); SelectByTag(Find<ComboBox>(w, "ThemeBox"), "dark"); Pump();
            c.ShowPage("queue"); Pump();
            T.Check(failed.StateText == "Failed" && disk.ErrorText.StartsWith("Not enough disk space"), "job texts follow the language at once");
            Console.WriteLine("SCREEN " + Render(w, "stage3-queue-en-dark"));
            // Filter
            SelectByTag(Find<ComboBox>(w, "QueueFilterBox"), "YouTube"); Pump();
            var shown = Visuals(Find<ItemsControl>(w, "QueueList")).OfType<ContentPresenter>().Count(p => p.DataContext is JobItem);
            T.Check(shown == 1, "platform filter shows only matching jobs");
            SelectByTag(Find<ComboBox>(w, "QueueFilterBox"), "all"); Pump();
            // Closing pauses the running job and saves the queue
            var running = c.Queue.Items.First(i => i.IsActive);
            w.Close(); Pump();
            var saved = JsonStore.Convert<QueueFile>(JsonStore.Load(paths.QueueFile).Data);
            T.Check(saved.Jobs.Any(j => j.Id == running.Job.Id && j.State == JobState.Paused), "closing Flow pauses running jobs and keeps them");
            var reopened = Open(paths, 1180, 900, new FakeExecutor());
            T.Check(reopened.Queue.Items.Count >= 4, "queue restored after reopening");
            var history = Store.Load(paths).History;
            T.Check(history.Any(h => h.Status == "completed" && File.Exists(h.FilePath)), "completed jobs are written to history with their files");
            reopened.Window.Close();
            Loc.Set("tr");
        }
    }
}
