using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using Flow;

namespace FlowTests {
    public class FakeRegistrar : ITaskRegistrar {
        public readonly List<string> Calls = new List<string>();
        public string LastXml;
        public void Register(string name, string xml) { Calls.Add("register " + name); LastXml = xml; }
        public void Remove(string name) { Calls.Add("remove " + name); }
        public bool Exists(string name) { return LastXml != null; }
    }

    public static class Stage6Unit {
        public static void Run() {
            var now = new DateTime(2026, 9, 28, 20, 0, 0, DateTimeKind.Local);
            var iso = Schedule.Parse("2026-09-29", "09:15", now);
            T.Check(Iso.Parse(iso).Value.ToLocalTime() == new DateTime(2026, 9, 29, 9, 15, 0), "start time parsed from day and HH:mm");
            T.Check(Iso.Parse(Schedule.Parse("2026-09-28", "21.30", now)).Value.ToLocalTime().Hour == 21, "21.30 accepted as 21:30");
            T.Rejects(() => Schedule.Parse("2026-09-28", "19:59", now), ErrorCode.SchedulePast, "a time already passed today is refused");
            T.Rejects(() => Schedule.Parse("2026-09-28", "25:00", now), ErrorCode.ScheduleInvalid, "invalid time refused");
            var xml = ScheduledStart.TaskXml(new DateTime(2026, 10, 1, 21, 30, 0), "C:\\Apps & Tools\\Flow\\Flow.exe", "Flow: test");
            T.Check(xml.Contains("<StartBoundary>2026-10-01T21:30:00</StartBoundary>") && xml.Contains("C:\\Apps &amp; Tools\\Flow\\Flow.exe") && xml.Contains("<Arguments>--scheduled</Arguments>") && xml.Contains("InteractiveToken") && xml.Contains("LeastPrivilege") && xml.Contains("<StartWhenAvailable>true"), "task XML: ISO date, escaped path, no elevation, runs when the PC becomes available");
            var fake = new FakeRegistrar();
            var jobs = new List<DownloadJob> {
                new DownloadJob { State = JobState.Scheduled, ScheduledAt = Iso.From(DateTime.UtcNow.AddHours(5)) },
                new DownloadJob { State = JobState.Scheduled, ScheduledAt = Iso.From(DateTime.UtcNow.AddHours(2)) },
                new DownloadJob { State = JobState.Queued }
            };
            var when = ScheduledStart.Sync(fake, true, jobs, "C:\\Flow\\Flow.exe");
            T.Check(when.HasValue && Math.Abs((when.Value - DateTime.Now.AddHours(2)).TotalMinutes) < 1 && fake.Calls.Last() == "register " + ScheduledStart.TaskName && fake.LastXml.Contains("C:\\Flow\\Flow.exe"), "one task, set for the earliest scheduled job");
            ScheduledStart.Sync(fake, false, jobs, "C:\\Flow\\Flow.exe");
            T.Check(fake.Calls.Last() == "remove " + ScheduledStart.TaskName, "turning the option off removes the task");
            ScheduledStart.Sync(fake, true, new List<DownloadJob> { new DownloadJob { State = JobState.Queued } }, "C:\\Flow\\Flow.exe");
            T.Check(fake.Calls.Last() == "remove " + ScheduledStart.TaskName, "no scheduled jobs left: task removed");

            // Missed time while the PC slept (Flow open): policy applies
            foreach (var policy in new[] { "hold", "start" }) {
                var root = T.Scratch("sleep-" + policy);
                var paths = new AppPaths { DataDir = Path.Combine(root, "d"), LegacyDir = Path.Combine(root, "o"), DefaultDownloads = Path.Combine(root, "out") };
                var s = SettingsRules.Defaults(paths); s.MissedSchedule = policy;
                var q = new QueueService(paths, () => s, new FakeExecutor());
                var job = new DownloadJob { Url = "https://example.com/v/okSleep", Platform = "Other", Title = "okSleep", Options = Media.Opt("compatible", "mp4", 1080, "h264"), ScheduledAt = Iso.From(DateTime.UtcNow.AddMinutes(10)) };
                job.Options.Folder = paths.DefaultDownloads;
                var item = q.Add(job, false);
                job.ScheduledAt = Iso.From(DateTime.UtcNow.AddMinutes(-6));   // as if the PC slept past it
                q.Pump();
                T.Check(policy == "hold" ? item.Job.State == JobState.Paused && item.Job.Note == "note.missed" : item.IsActive || item.Job.State == JobState.Queued, "time missed during sleep follows the '" + policy + "' policy");
                q.Shutdown();
            }
            // Opened by the Windows task: overdue jobs start even with the hold policy
            var rootL = T.Scratch("task-launch");
            var pathsL = new AppPaths { DataDir = Path.Combine(rootL, "d"), LegacyDir = Path.Combine(rootL, "o"), DefaultDownloads = Path.Combine(rootL, "out") };
            var sl = SettingsRules.Defaults(pathsL); sl.MissedSchedule = "hold";
            var file = new QueueFile { SchemaVersion = 1, Jobs = new List<DownloadJob> { new DownloadJob { Id = "t1", Url = "https://example.com/v/x", Platform = "Other", State = JobState.Scheduled, ScheduledAt = Iso.From(DateTime.UtcNow.AddMinutes(-3)), Options = Media.Opt("compatible", "mp4", 1080, "h264") } } };
            JsonStore.Save(pathsL.QueueFile, file);
            var ql = new QueueService(pathsL, () => sl, new FakeExecutor()); ql.Load(true);
            T.Check(ql.Items[0].Job.State == JobState.Queued, "launch by the Windows task starts the overdue job");
        }
    }

    public static partial class UiTests {
        public static void Stage6Ui() {
            var paths = NewPaths("ui-shell");
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = "light";
            Store.SaveSettings(paths, s);
            var c = Open(paths, 1180, 1100, new FakeExecutor());
            var w = c.Window;
            var speed = Find<ComboBox>(w, "SpeedBox");
            T.Check(speed.Items.Count == 7 && TagOf(speed) == "0", "speed limit choices, unlimited by default");
            SelectByTag(speed, "1048576"); Pump();
            T.Check(Find<TextBlock>(w, "ScheduleHeader").Text.Contains("1,0 MB/sn"), "chosen speed shown in the section header");
            var box = Find<TextBox>(w, "UrlBox");
            box.Text = "https://vimeo.com/okSpeed"; Pump();
            var sp = c.Enqueue(true);
            T.Check(sp.Job.Options.SpeedLimit == 1048576, "speed limit stored on the job");
            // Scheduled start
            SelectByTag(Find<ComboBox>(w, "StartModeBox"), "at");
            SelectByTag(Find<ComboBox>(w, "ScheduleDateBox"), DateTime.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Find<TextBox>(w, "ScheduleTimeBox").Text = "09:15"; Pump();
            Find<Expander>(w, "ScheduleExpander").IsExpanded = true; Pump();
            Console.WriteLine("SCREEN " + Render(w, "stage6-schedule"));
            box.Text = "https://vimeo.com/okLater"; Pump();
            var later = c.Enqueue(false);
            var at = Iso.Parse(later.Job.ScheduledAt).Value.ToLocalTime();
            T.Check(later.Job.State == JobState.Scheduled && at == DateTime.Today.AddDays(1).AddHours(9).AddMinutes(15), "job scheduled for tomorrow 09:15");
            SelectByTag(Find<ComboBox>(w, "ScheduleDateBox"), DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Find<TextBox>(w, "ScheduleTimeBox").Text = DateTime.Now.AddMinutes(-5).ToString("HH:mm"); Pump();
            box.Text = "https://vimeo.com/okPast"; Pump();
            T.Check(Find<TextBlock>(w, "ScheduleNote").Text == Loc.Error(ErrorCode.SchedulePast) && c.Enqueue(false) == null, "past start time explained and not queued");
            SelectByTag(Find<ComboBox>(w, "StartModeBox"), "now"); Pump();
            // Default speed from settings follows into the download form
            SelectByTag(Find<ComboBox>(w, "DefaultSpeedBox"), "524288"); Pump();
            T.Check(TagOf(speed) == "524288" && Store.Load(paths).Settings.SpeedLimit == 524288, "default speed limit saved and applied to new jobs");
            // Taskbar progress, tray tooltip and notifications while a job runs
            box.Text = "https://vimeo.com/slowTask"; Pump();
            var running = c.Enqueue(false); running.Job.Title = "slowTask";
            Wait(() => running.IsActive && running.ProgressValue > 2 && w.TaskbarItemInfo.ProgressState == TaskbarItemProgressState.Normal, 5000);
            T.Check(w.TaskbarItemInfo.ProgressState == TaskbarItemProgressState.Normal && w.TaskbarItemInfo.ProgressValue > 0 && c.TrayTooltip.StartsWith("Flow · 1"), "taskbar and tray show the running download");
            c.Queue.Cancel(running);
            Wait(() => !running.IsActive, 3000);
            T.Check(w.TaskbarItemInfo.ProgressState == TaskbarItemProgressState.None, "taskbar progress cleared when idle");
            box.Text = "https://vimeo.com/okNotify"; Pump();
            var done = c.Enqueue(false);
            w.Hide();   // the window is in the background
            Wait(() => done.Job.State == JobState.Completed, 4000); Pump();
            c.BringToFront(); Pump();
            T.Check(c.Notifications.Any(n => n[0] == Loc.T("note.done.title")) && c.MessageTextValue == Loc.F("msg.done.job", done.Title), "finished job: Windows notification (window in background) and in-app notice");
            // Settings page render
            c.ShowPage("settings"); Pump();
            Console.WriteLine("SCREEN " + Render(w, "stage6-settings"));
            // Windows task (fake registrar): created for the next scheduled job, removed when turned off
            var fake = new FakeRegistrar(); c.TaskRegistrar = fake;
            var task = Find<CheckBox>(w, "WindowsTaskBox");
            task.IsChecked = true; task.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, task)); Pump();
            T.Check(fake.Calls.LastOrDefault() == "register " + ScheduledStart.TaskName && fake.LastXml.Contains("T09:15:00") && c.WindowsTaskText.Length > 0, "Windows task set for the scheduled job when enabled");
            task.IsChecked = false; task.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, task)); Pump();
            T.Check(fake.Calls.Last() == "remove " + ScheduledStart.TaskName, "Windows task removed when disabled");
            // Minimize and close to the notification area
            var min = Find<CheckBox>(w, "MinimizeTrayBox"); min.IsChecked = true; min.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, min));
            w.WindowState = WindowState.Minimized; Pump();
            T.Check(!w.IsVisible, "minimize hides the window to the notification area");
            c.BringToFront(); Pump();
            c.ConfirmClose = true;
            int prompts = 0;
            c.ClosePrompt = (owner, active) => { prompts++; return AnswerCloseDialog(owner, active, "TrayButton", "close-tray-active"); };
            box.Text = "https://vimeo.com/slowBackground"; Pump(); c.ShowPage("download"); Pump();
            var bg = c.Enqueue(false); bg.Job.Title = "slowBackground";
            Wait(() => bg.IsActive, 3000);
            w.Close(); Pump();
            T.Check(!w.IsVisible && bg.IsActive && !c.Queue.ShuttingDown && c.TrayWanted && prompts == 1, "choosing the tray keeps the job running and the main window hidden");
            c.ExitNow(); Pump();
            var saved = JsonStore.Convert<QueueFile>(JsonStore.Load(paths.QueueFile).Data);
            T.Check(saved.Jobs.Any(j => j.Id == bg.Job.Id && j.State == JobState.Paused) && prompts == 1, "Exit from the tray pauses and saves the job without prompting again");
        }
    }
}
