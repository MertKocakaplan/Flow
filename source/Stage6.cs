using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Flow {
    // Tray, notifications, taskbar progress, per-job speed and scheduling.
    public partial class Controller {
        // Tests turn this off so nothing appears in the user's notification area.
        internal static bool ShellIntegration = true;
        internal ITaskRegistrar TaskRegistrar = new SchtasksRegistrar();
        Tray tray;
        bool exiting, promptingClose;
        internal Func<Window, int, CloseChoice> ClosePrompt = CloseDialog.Ask;
        DispatcherTimer taskSync;
        public static readonly long[] SpeedSteps = { 0, 262144, 524288, 1048576, 2097152, 5242880, 10485760 };
        internal readonly List<string[]> Notifications = new List<string[]>();

        void InitStage6() {
            tray = new Tray();
            tray.OpenRequested += () => { BringToFront(); };
            tray.NotificationClicked += () => { BringToFront(); ShowPage("queue"); };
            tray.PauseAllRequested += () => queue.PauseAll();
            tray.ResumeAllRequested += () => queue.ResumeAll();
            tray.ExitRequested += ExitFromTray;
            UpdateTrayVisibility();

            BuildSpeedItems(C<ComboBox>("SpeedBox"), settings.SpeedLimit);
            BuildSpeedItems(C<ComboBox>("DefaultSpeedBox"), settings.SpeedLimit);
            C<ComboBox>("DefaultSpeedBox").SelectionChanged += (s, e) => {
                if (loading) return;
                long v; if (long.TryParse(TagOf(C<ComboBox>("DefaultSpeedBox")), out v)) { settings.SpeedLimit = v; SaveSettings(); bool was = loading; loading = true; SelectTag(C<ComboBox>("SpeedBox"), v.ToString(CultureInfo.InvariantCulture)); loading = was; UpdateScheduleHeader(); }
            };
            C<ComboBox>("SpeedBox").SelectionChanged += (s, e) => UpdateScheduleHeader();
            SelectTag(C<ComboBox>("StartModeBox"), "now");
            BuildDateItems();
            C<TextBox>("ScheduleTimeBox").Text = DateTime.Now.AddHours(1).ToString("HH:00", CultureInfo.InvariantCulture);
            C<ComboBox>("StartModeBox").SelectionChanged += (s, e) => UpdateScheduleHeader();
            C<ComboBox>("ScheduleDateBox").SelectionChanged += (s, e) => UpdateScheduleHeader();
            C<TextBox>("ScheduleTimeBox").TextChanged += (s, e) => UpdateScheduleHeader();
            UpdateScheduleHeader();

            C<CheckBox>("MinimizeTrayBox").IsChecked = settings.MinimizeToTray;
            C<CheckBox>("NotifyBox").IsChecked = settings.Notify;
            C<CheckBox>("WindowsTaskBox").IsChecked = settings.WindowsTask;
            SelectTag(C<ComboBox>("MissedBox"), settings.MissedSchedule);
            C<CheckBox>("MinimizeTrayBox").Click += (s, e) => { settings.MinimizeToTray = C<CheckBox>("MinimizeTrayBox").IsChecked == true; SaveSettings(); UpdateTrayVisibility(); };
            C<CheckBox>("NotifyBox").Click += (s, e) => { settings.Notify = C<CheckBox>("NotifyBox").IsChecked == true; SaveSettings(); UpdateTrayVisibility(); };
            C<CheckBox>("WindowsTaskBox").Click += (s, e) => { settings.WindowsTask = C<CheckBox>("WindowsTaskBox").IsChecked == true; SaveSettings(); SyncWindowsTask(); };
            C<ComboBox>("MissedBox").SelectionChanged += (s, e) => { if (loading) return; settings.MissedSchedule = TagOf(C<ComboBox>("MissedBox")) ?? "start"; SaveSettings(); };
            ExtraCombos.AddRange(new[] { "StartModeBox", "MissedBox" });

            Window.StateChanged += (s, e) => { if (Window.WindowState == WindowState.Minimized && settings.MinimizeToTray) { tray.Visible = ShellIntegration; Window.Hide(); } };
            taskSync = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            taskSync.Tick += (s, e) => { taskSync.Stop(); SyncWindowsTask(); };
            queue.Changed += () => { if (settings.WindowsTask) { taskSync.Stop(); taskSync.Start(); } };
            queue.NeedsAttention += NotifyNeedsAction;
            SystemEvents.PowerModeChanged += OnPowerChanged;
            if (settings.WindowsTask) Window.Loaded += (s, e) => SyncWindowsTask();
        }

        void OnPowerChanged(object sender, PowerModeChangedEventArgs e) {
            if (e.Mode == PowerModes.Resume) Window.Dispatcher.BeginInvoke(new Action(() => queue.Pump()));
        }

        void UpdateTrayVisibility() {
            if (tray == null) return;
            tray.Visible = ShellIntegration && (settings.MinimizeToTray || settings.Notify || !Window.IsVisible);
        }
        public bool TrayWanted { get { return settings.MinimizeToTray || settings.Notify || !Window.IsVisible; } }
        public string TrayTooltip { get { return tray == null ? "" : tray.Tooltip; } }

        void BuildSpeedItems(ComboBox box, long selected) {
            bool was = loading; loading = true;
            box.Items.Clear();
            foreach (var step in SpeedSteps) box.Items.Add(new ComboBoxItem { Tag = step.ToString(CultureInfo.InvariantCulture), Content = step == 0 ? Loc.T("sched.unlimited") : Loc.Speed(step) });
            SelectTag(box, selected.ToString(CultureInfo.InvariantCulture));
            loading = was;
        }
        void BuildDateItems() {
            var box = C<ComboBox>("ScheduleDateBox");
            var keep = TagOf(box);
            bool was = loading; loading = true;
            box.Items.Clear();
            for (int i = 0; i < 7; i++) {
                var day = DateTime.Today.AddDays(i);
                var label = i == 0 ? Loc.T("sched.today") : i == 1 ? Loc.T("sched.tomorrow") : day.ToString("dddd", Loc.Culture);
                box.Items.Add(new ComboBoxItem { Tag = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Content = label + " · " + day.ToString(Loc.Lang == "tr" ? "dd.MM" : "MMM d", Loc.Culture) });
            }
            SelectTag(box, keep ?? DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            loading = was;
        }
        partial void OnLanguageChangedExtra() {
            BuildSpeedItems(C<ComboBox>("SpeedBox"), SelectedSpeed());
            BuildSpeedItems(C<ComboBox>("DefaultSpeedBox"), settings.SpeedLimit);
            BuildDateItems();
            UpdateScheduleHeader();
            if (tray != null) tray.Retext();
            BuildSubtitleChoices(); UpdateClipState();
            if (video != null) UpdateExtrasForMedia(video);
        }

        internal long SelectedSpeed() { long v; return long.TryParse(TagOf(C<ComboBox>("SpeedBox")), out v) ? v : settings.SpeedLimit; }
        bool ScheduleAt { get { return TagOf(C<ComboBox>("StartModeBox")) == "at"; } }
        // Throws ScheduleInvalid / SchedulePast; null means "start when its turn comes".
        internal string ScheduledTime() { return ScheduleAt ? Schedule.Parse(TagOf(C<ComboBox>("ScheduleDateBox")), C<TextBox>("ScheduleTimeBox").Text, DateTime.Now) : null; }

        void UpdateScheduleHeader() {
            var header = C<TextBlock>("ScheduleHeader");
            if (header == null) return;
            C<StackPanel>("ScheduleWhen").IsEnabled = ScheduleAt;
            var parts = new List<string>();
            var speed = SelectedSpeed();
            if (speed > 0) parts.Add(Loc.Speed(speed));
            if (ScheduleAt) {
                try { parts.Add(Loc.Date(Iso.Parse(ScheduledTime()).Value)); C<TextBlock>("ScheduleNote").SetResourceReference(TextBlock.TextProperty, "s.sched.note"); C<TextBlock>("ScheduleNote").SetResourceReference(TextBlock.ForegroundProperty, "TextMuted"); }
                catch (FlowException ex) { C<TextBlock>("ScheduleNote").Text = Loc.Error(ex); C<TextBlock>("ScheduleNote").SetResourceReference(TextBlock.ForegroundProperty, "Danger"); }
            } else { C<TextBlock>("ScheduleNote").SetResourceReference(TextBlock.TextProperty, "s.sched.note"); C<TextBlock>("ScheduleNote").SetResourceReference(TextBlock.ForegroundProperty, "TextMuted"); }
            if (parts.Count > 0) header.Text = Loc.F("sched.header", string.Join(" · ", parts)); else header.SetResourceReference(TextBlock.TextProperty, "s.sched.title");
        }

        partial void ApplySchedule(DownloadJob job) {
            try { job.ScheduledAt = ScheduledTime(); } catch (FlowException) { job.ScheduledAt = null; }
        }

        // ---- Taskbar and tray status ----
        partial void UpdateTaskbar(int active, double overall) {
            var info = Window.TaskbarItemInfo;
            if (info != null) {
                if (active == 0) info.ProgressState = TaskbarItemProgressState.None;
                else if (overall < 0) info.ProgressState = TaskbarItemProgressState.Indeterminate;
                else { info.ProgressState = TaskbarItemProgressState.Normal; info.ProgressValue = Math.Max(0, Math.Min(1, overall / 100)); }
            }
            if (tray != null) tray.SetTooltip(active == 0 ? Loc.T("tray.idle") : overall >= 0 ? Loc.F("tray.activePercent", active, Math.Floor(overall)) : Loc.F("tray.active", active));
        }

        // ---- Notifications ----
        partial void OnJobNotify(JobItem item, string status) {
            if (status == "canceled") return;
            bool ok = status == "completed" || status == "skipped";
            var title = Loc.T(ok ? "note.done.title" : "note.failed.title");
            if (ok) ShowMessage(Loc.F("msg.done.job", item.Title), false, Loc.T("common.reveal"), () => Reveal(item.Job.OutputPath));
            else ShowMessage(Loc.F("msg.failed.job", item.Title, item.ErrorText), true, Loc.T("dl.showQueue"), () => ShowPage("queue"));
            // A Windows notification only when the window is not in front; the in-app notice always stays.
            if (settings.Notify && (!Window.IsActive || !Window.IsVisible)) {
                Notifications.Add(new[] { title, item.Title });
                if (ShellIntegration && tray != null) tray.Notify(title, item.Title, !ok);
            }
        }
        void NotifyNeedsAction(JobItem item) {
            if (!settings.Notify || Window.IsActive) return;
            Notifications.Add(new[] { Loc.T("note.action.title"), item.Title });
            if (ShellIntegration && tray != null) tray.Notify(Loc.T("note.action.title"), item.Title + " — " + item.ErrorText, true);
        }

        // ---- Closing, tray, exit ----
        partial void CloseHook(System.ComponentModel.CancelEventArgs e, ref bool handled) {
            if (exiting || !ConfirmClose) return;
            // Cancel unless the user explicitly chooses Exit. Old CloseToTray settings
            // no longer bypass this choice, and repeated close requests cannot nest dialogs.
            e.Cancel = true; handled = true;
            if (promptingClose) return;
            CloseChoice choice;
            promptingClose = true;
            try { choice = ClosePrompt(Window, queue.ActiveCount); }
            finally { promptingClose = false; }
            if (choice == CloseChoice.Exit) { exiting = true; e.Cancel = false; handled = false; return; }
            if (choice != CloseChoice.Tray) return;
            Window.Hide();
            if (tray != null) { tray.Visible = ShellIntegration; if (!settings.TrayHintShown && ShellIntegration) { tray.Notify("Flow", Loc.T("tray.hidden"), false); settings.TrayHintShown = true; SaveSettings(); } }
        }
        void ExitFromTray() { exiting = true; ConfirmClose = false; Window.Show(); Window.Close(); }
        internal void ExitNow() { ExitFromTray(); }
        partial void OnClosed() {
            SystemEvents.PowerModeChanged -= OnPowerChanged;
            if (taskSync != null) taskSync.Stop();
            if (tray != null) { tray.Dispose(); tray = null; }
        }

        // ---- Optional Windows task ----
        internal string WindowsTaskText = "";
        void SyncWindowsTask() {
            var status = C<TextBlock>("WindowsTaskStatus");
            // Tests never touch the real Task Scheduler unless they supply their own registrar.
            if (!ShellIntegration && TaskRegistrar is SchtasksRegistrar) return;
            try {
                var when = ScheduledStart.Sync(TaskRegistrar, settings.WindowsTask, queue.Items.Select(i => i.Job), System.Reflection.Assembly.GetEntryAssembly() == null ? "" : System.Reflection.Assembly.GetEntryAssembly().Location);
                WindowsTaskText = when.HasValue ? Loc.F("set.windowsTask.on", Loc.Date(when.Value.ToUniversalTime())) : settings.WindowsTask ? Loc.T("set.windowsTask.off") : "";
            } catch (Exception ex) { WindowsTaskText = Loc.F("set.windowsTask.failed", ex.Message); }
            if (status != null) status.Text = WindowsTaskText;
        }
    }
}
