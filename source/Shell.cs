using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Windows.Forms;

namespace Flow {
    // Notification-area icon with a small menu. Balloon notifications appear as Windows notifications;
    // if the user turned those off, Flow still shows its own in-app notice.
    public sealed class Tray : IDisposable {
        readonly NotifyIcon icon;
        readonly ContextMenuStrip menu;
        readonly ToolStripMenuItem open, pause, resume, exit;
        public event Action OpenRequested, ExitRequested, PauseAllRequested, ResumeAllRequested, NotificationClicked;

        public Tray() {
            menu = new ContextMenuStrip();
            open = new ToolStripMenuItem(); open.Click += (s, e) => Raise(OpenRequested); open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
            pause = new ToolStripMenuItem(); pause.Click += (s, e) => Raise(PauseAllRequested);
            resume = new ToolStripMenuItem(); resume.Click += (s, e) => Raise(ResumeAllRequested);
            exit = new ToolStripMenuItem(); exit.Click += (s, e) => Raise(ExitRequested);
            menu.Items.AddRange(new ToolStripItem[] { open, new ToolStripSeparator(), resume, pause, new ToolStripSeparator(), exit });
            icon = new NotifyIcon { Icon = LoadIcon(), Text = "Flow", ContextMenuStrip = menu, Visible = false };
            icon.DoubleClick += (s, e) => Raise(OpenRequested);
            icon.BalloonTipClicked += (s, e) => Raise(NotificationClicked);
            Retext();
        }
        static void Raise(Action a) { if (a != null) a(); }
        static System.Drawing.Icon LoadIcon() {
            try { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico")) if (s != null) return new System.Drawing.Icon(s); } catch (Exception) {}
            return System.Drawing.SystemIcons.Application;
        }
        public void Retext() { open.Text = Loc.T("tray.open"); pause.Text = Loc.T("queue.pauseAll"); resume.Text = Loc.T("queue.resumeAll"); exit.Text = Loc.T("tray.exit"); }
        public bool Visible { get { return icon.Visible; } set { icon.Visible = value; } }
        // The notification-area tooltip is limited to 63 characters.
        public void SetTooltip(string text) { icon.Text = text.Length > 63 ? text.Substring(0, 62) + "…" : text; }
        public string Tooltip { get { return icon.Text; } }
        public void Notify(string title, string text, bool error) {
            if (!icon.Visible) icon.Visible = true;
            icon.ShowBalloonTip(6000, title, string.IsNullOrEmpty(text) ? " " : text, error ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        public void Dispose() { icon.Visible = false; icon.Dispose(); menu.Dispose(); }
    }

    // While downloads run the PC does not go to sleep on its own; the screen may still turn off.
    // The request belongs to the calling thread, so it is always made from the UI thread.
    public static class Awake {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        const uint Continuous = 0x80000000, SystemRequired = 0x00000001;
        public static bool Held { get; private set; }
        public static void Hold(bool on) {
            if (on == Held) return;
            if (SetThreadExecutionState(on ? Continuous | SystemRequired : Continuous) != 0) Held = on;
        }
    }

    // Optional Windows Task Scheduler entry that opens Flow for the next scheduled job while it is closed.
    // The task XML uses ISO dates, so it does not depend on the regional date format.
    public interface ITaskRegistrar { void Register(string name, string xml); void Remove(string name); bool Exists(string name); }

    public sealed class SchtasksRegistrar : ITaskRegistrar {
        static string Schtasks { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"); } }
        static int Run(params string[] args) {
            var start = new ProcessStartInfo(Schtasks, string.Join(" ", args.Select(Engine.Quote))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = new Process { StartInfo = start }) {
                p.OutputDataReceived += (s, e) => {};
                p.ErrorDataReceived += (s, e) => {};
                p.Start();
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (Exception) {} return -1; }
                p.WaitForExit();   // lets the asynchronous readers finish
                return p.ExitCode;
            }
        }
        public void Register(string name, string xml) {
            var file = Path.Combine(Path.GetTempPath(), "flow-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try {
                File.WriteAllText(file, xml, Encoding.Unicode);
                if (Run("/Create", "/TN", name, "/XML", file, "/F") != 0) throw new InvalidOperationException("schtasks /Create failed");
            } finally { try { File.Delete(file); } catch (IOException) {} }
        }
        public void Remove(string name) { Run("/Delete", "/TN", name, "/F"); }
        public bool Exists(string name) { return Run("/Query", "/TN", name) == 0; }
    }

    public static class ScheduledStart {
        public const string TaskName = "Flow\\Scheduled start";
        public const string Argument = "--scheduled";
        public static string TaskXml(DateTime localStart, string exePath, string description) {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n");
            sb.Append("  <RegistrationInfo><Description>").Append(SecurityElement.Escape(description)).Append("</Description></RegistrationInfo>\n");
            sb.Append("  <Triggers><TimeTrigger><StartBoundary>").Append(localStart.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)).Append("</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>\n");
            sb.Append("  <Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\n");
            sb.Append("  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings>\n");
            sb.Append("  <Actions Context=\"Author\"><Exec><Command>").Append(SecurityElement.Escape(exePath)).Append("</Command><Arguments>").Append(Argument).Append("</Arguments></Exec></Actions>\n</Task>\n");
            return sb.ToString();
        }
        // Keeps a single task pointing at the earliest pending scheduled job (and at the current EXE path).
        public static DateTime? Sync(ITaskRegistrar registrar, bool enabled, System.Collections.Generic.IEnumerable<DownloadJob> jobs, string exePath) {
            var next = jobs.Where(j => j.State == JobState.Scheduled).Select(j => Iso.Parse(j.ScheduledAt)).Where(t => t.HasValue && t.Value > DateTime.UtcNow).Select(t => t.Value).OrderBy(t => t).FirstOrDefault();
            if (!enabled || next == default(DateTime)) { registrar.Remove(TaskName); return null; }
            var local = next.ToLocalTime();
            registrar.Register(TaskName, TaskXml(local, exePath, "Flow: " + Loc.T("sched.taskDescription")));
            return local;
        }
    }

    public static class Schedule {
        // Local date (yyyy-MM-dd) and "HH:mm" to a UTC ISO string; the time must be in the future.
        public static string Parse(string date, string time, DateTime nowLocal) {
            DateTime day, clock;
            if (!DateTime.TryParseExact(date ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) throw new FlowException(ErrorCode.ScheduleInvalid);
            var t = (time ?? "").Trim().Replace('.', ':');
            if (!DateTime.TryParseExact(t, new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out clock)) throw new FlowException(ErrorCode.ScheduleInvalid);
            var local = new DateTime(day.Year, day.Month, day.Day, clock.Hour, clock.Minute, 0, DateTimeKind.Local);
            if (local <= nowLocal.AddSeconds(30)) throw new FlowException(ErrorCode.SchedulePast);
            return Iso.From(local);
        }
    }
}
