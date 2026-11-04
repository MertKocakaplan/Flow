using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Flow {
    // One queue entry: the persisted job plus live progress for the UI. Mutated on the UI thread only.
    public class JobItem : INotifyPropertyChanged {
        public readonly DownloadJob Job;
        public event PropertyChangedEventHandler PropertyChanged;
        internal CancellationTokenSource Cts;
        internal bool PauseRequested, CancelRequested;
        public MediaInfo Media;
        public DateTime MediaAt;
        double resumedBytes;
        public double ResumedBytes { get { return resumedBytes; } set { resumedBytes = value; Raise(); } }
        public Selection Selection;
        string stageKey;
        ProgressSnapshot progress;
        double stagePercent = -1;
        readonly StringBuilder log = new StringBuilder();

        public JobItem(DownloadJob job) { Job = job; }
        void Raise() { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(string.Empty)); }
        public void Refresh() { Raise(); }

        public void AppendLog(string line) {
            if (log.Length > 200000) log.Remove(0, 50000);
            log.AppendLine(Engine.Mask(line));
        }
        public string LogText { get { return log.ToString(); } }

        // ---- Updates from the executor ----
        public void SetStage(string state, string key) { Job.State = state; stageKey = key; stagePercent = -1; if (state != JobState.Downloading) progress = null; Raise(); }
        public void ApplyMedia(MediaInfo info) {
            Media = info; MediaAt = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(info.Title)) Job.Title = info.Title;
            if (!string.IsNullOrWhiteSpace(info.Channel)) Job.Channel = info.Channel;
            if (!string.IsNullOrWhiteSpace(info.Id)) Job.ContentId = info.Id;
            if (info.Duration > 0) Job.Duration = info.Duration;
            if (info.Platform != Platform.Other || string.IsNullOrEmpty(Job.Platform)) Job.Platform = info.Platform.ToString();
            Raise();
        }
        public void ApplySelection(Selection s, string target, string note) {
            Selection = s; Job.ResolvedFormat = s.Spec; Job.OutputPath = target; Job.ExpectedBytes = s.Bytes; Job.SizeExact = s.SizeExact;
            Job.Width = s.Width; Job.Height = s.Height; Job.Fps = s.Fps; Job.VCodec = s.VCodec; Job.ACodec = s.ACodec;
            Job.Note = note ?? (s.Bytes < 0 ? "note.sizeUnknown" : Job.Note == "note.interrupted" || Job.Note == "note.missed" ? Job.Note : null);
            Raise();
        }
        public void ApplyProgress(ProgressSnapshot p) {
            if (Job.State != JobState.Downloading) return;
            progress = p;
            stageKey = p.Streams > 1 ? (p.IsVideo ? "stage.video" : "stage.audio") : "stage.download";
            Raise();
        }
        public void ApplyStagePercent(double percent) { stagePercent = percent; Raise(); }
        public void MarkSkipped(string path, Selection s) {
            Selection = s; Job.OutputPath = path; Job.Skipped = true; Job.State = JobState.Completed; Job.FinishedAt = Iso.Now(); Job.Note = null; stageKey = null; progress = null;
            try { Job.ExpectedBytes = new FileInfo(path).Length; } catch (Exception) {}
            Raise();
        }
        public void MarkCompleted(string path, string format, ProbeResult probe) {
            Job.OutputPath = path; Job.ResolvedFormat = format; Job.State = JobState.Completed; Job.FinishedAt = Iso.Now(); Job.Skipped = false;
            Job.ExpectedBytes = probe.Size; Job.SizeExact = true;
            if (probe.Width > 0) { Job.Width = probe.Width; Job.Height = probe.Height; }
            if (probe.VCodec != null) Job.VCodec = probe.VCodec;
            if (probe.ACodec != null) Job.ACodec = probe.ACodec;
            if (Job.Note == "note.resumed" || Job.Note == "note.sizeUnknown") Job.Note = null;
            stageKey = null; progress = null; stagePercent = -1;
            Raise();
        }
        internal void ResetRuntime() { stageKey = null; progress = null; stagePercent = -1; resumedBytes = 0; PauseRequested = CancelRequested = false; }

        // ---- Display ----
        public string Title { get { return string.IsNullOrWhiteSpace(Job.Title) ? Job.Url : Job.Title; } }
        public string Subtitle {
            get {
                var parts = new List<string>();
                parts.Add(Job.Platform == "Other" || string.IsNullOrEmpty(Job.Platform) ? Loc.T("tab.other") : Job.Platform);
                var o = Job.Options;
                if (o != null) {
                    parts.Add(Loc.T("profile." + (Job.MediaKind == "image" ? "image" : SettingsRules.Profiles.Contains(o.Profile) ? o.Profile : "custom")));
                    if (!o.IsAudio) parts.Add(Job.Height > 0 && Job.Width > 0 ? Job.Width + "×" + Job.Height : o.Height > 0 ? "≤ " + o.Height + "p" : Loc.T("quality.best"));
                    if (o.HasClip) parts.Add(Loc.Duration(o.ClipStart) + "–" + Loc.Duration(o.ClipEnd));
                }
                if (!string.IsNullOrEmpty(Job.CollectionTitle)) parts.Add(Job.CollectionTitle);
                return string.Join("  ·  ", parts);
            }
        }
        public string StateKind {
            get {
                var s = Job.State;
                if (JobState.IsActive(s)) return "active";
                if (s == JobState.Completed) return "done";
                if (s == JobState.Failed || s == JobState.NeedsAction) return "error";
                if (s == JobState.Retry) return "warning";
                return "muted";
            }
        }
        public string StateText {
            get {
                var s = Job.State;
                if (s == JobState.Scheduled) { var t = Iso.Parse(Job.ScheduledAt); return Loc.F("state.scheduled", t.HasValue ? Loc.Date(t.Value) : ""); }
                if (s == JobState.Retry) { var t = Iso.Parse(Job.NextRetryAt); var left = t.HasValue ? Math.Max(0, (t.Value - DateTime.UtcNow).TotalSeconds) : 0; return Loc.F("state.retry", Loc.Duration(left), Job.Attempts); }
                if (s == JobState.Completed && Job.Skipped) return Loc.T("state.skipped");
                return Loc.T("state." + s);
            }
        }
        public string StageText {
            get {
                if (!JobState.IsActive(Job.State) || stageKey == null) return "";
                if (progress != null && progress.Streams > 1 && (stageKey == "stage.video" || stageKey == "stage.audio")) return Loc.F(stageKey, progress.Stream, progress.Streams);
                return Loc.T(stageKey);
            }
        }
        public bool ShowProgress { get { return JobState.IsActive(Job.State); } }
        public bool Indeterminate { get { return !(Job.State == JobState.Downloading && progress != null && (progress.Percent >= 0 || progress.StreamPercent >= 0)) && stagePercent < 0; } }
        public double ProgressValue {
            get {
                if (stagePercent >= 0) return stagePercent;
                if (progress == null) return 0;
                return progress.Percent >= 0 ? progress.Percent : Math.Max(0, progress.StreamPercent);
            }
        }
        public string PercentText { get { return ShowProgress && !Indeterminate ? Math.Floor(ProgressValue).ToString("0", Loc.Culture) + "%" : ""; } }
        public string DetailText {
            get {
                var s = Job.State;
                if (s == JobState.Downloading && progress != null) {
                    var parts = new List<string>();
                    parts.Add(progress.Total > 0 ? Loc.F("det.downloaded", Loc.Size(progress.Downloaded), Loc.Size(progress.Total)) : Loc.F("det.downloadedUnknown", Loc.Size(progress.Downloaded)));
                    parts.Add(progress.Speed > 0 ? Loc.Speed(progress.Speed) : Loc.T("msg.speed.pending"));
                    if (progress.Eta > 0) parts.Add(Loc.F("msg.eta", Loc.Duration(progress.Eta)));
                    if (progress.Percent < 0 && progress.StreamPercent >= 0 && progress.Streams > 1) parts.Add(Loc.T("det.streamPercent"));
                    return string.Join("  ·  ", parts);
                }
                if (s == JobState.Completed) return (Job.Skipped ? Loc.T("det.skipped") + "  ·  " : "") + (string.IsNullOrEmpty(Job.OutputPath) ? "" : Path.GetFileName(Job.OutputPath)) + (Job.ExpectedBytes > 0 ? "  ·  " + Loc.Size(Job.ExpectedBytes) : "");
                if ((s == JobState.Queued || s == JobState.Paused || s == JobState.Scheduled) && Job.ExpectedBytes > 0) return (Job.SizeExact ? "" : "≈ ") + Loc.Size(Job.ExpectedBytes);
                return "";
            }
        }
        public string NoteText {
            get {
                if (string.IsNullOrEmpty(Job.Note) || !Loc.Has(Job.Note)) return "";
                return Job.Note == "note.resumed" && resumedBytes > 0 ? Loc.F("note.resumedFrom", Loc.Size(resumedBytes)) : Loc.T(Job.Note);
            }
        }
        public bool HasNote { get { return NoteText.Length > 0 && Job.State != JobState.Failed; } }
        public string ErrorText {
            get {
                if (string.IsNullOrEmpty(Job.Error) || (Job.State != JobState.Failed && Job.State != JobState.NeedsAction && Job.State != JobState.Retry && Job.State != JobState.Paused)) return "";
                ErrorCode code; if (!Enum.TryParse(Job.Error, out code)) code = ErrorCode.Unknown;
                return Loc.Error(code, (Job.ErrorArgs ?? new List<string>()).Cast<object>().ToArray());
            }
        }
        public bool HasError { get { return ErrorText.Length > 0; } }
        ErrorCode Code { get { ErrorCode c; return Enum.TryParse(Job.Error ?? "", out c) ? c : ErrorCode.None; } }
        public bool IsActive { get { return JobState.IsActive(Job.State); } }
        public bool CanPause { get { return IsActive || Job.State == JobState.Queued || Job.State == JobState.Scheduled || Job.State == JobState.Retry; } }
        public bool CanResume { get { return Job.State == JobState.Paused || Job.State == JobState.NeedsAction; } }
        public bool CanRetry { get { return Job.State == JobState.Failed || Job.State == JobState.Canceled; } }
        public bool CanCancel { get { return !JobState.IsFinished(Job.State); } }
        public bool CanRemove { get { return !IsActive; } }
        public bool CanMove { get { return !JobState.IsFinished(Job.State); } }
        public bool CanReveal { get { return Job.State == JobState.Completed && !string.IsNullOrEmpty(Job.OutputPath); } }
        public bool CanFixFolder { get { var c = Code; return (Job.State == JobState.NeedsAction || Job.State == JobState.Failed) && (c == ErrorCode.WriteDenied || c == ErrorCode.DiskFull || c == ErrorCode.DiskSpaceLow || c == ErrorCode.PathTooLong || c == ErrorCode.FolderInvalid); } }
        public bool CanFixFormat { get { var c = Code; return (Job.State == JobState.NeedsAction || Job.State == JobState.Failed) && (c == ErrorCode.FormatUnavailable || c == ErrorCode.QualityUnavailable || c == ErrorCode.NoAudio || c == ErrorCode.VerifyFailed); } }
        public bool CanCopy { get { return HasError || log.Length > 0; } }
        public string Diagnostics {
            get {
                var sb = new StringBuilder();
                sb.AppendLine("Flow " + Program.Version + " · " + Job.Platform + " · " + Engine.MaskUrl(Job.Url));
                sb.AppendLine("State: " + Job.State + " · Error: " + (Job.Error ?? "-") + " · Attempts: " + Job.Attempts + " · Format: " + (Job.ResolvedFormat ?? "-"));
                if (Job.Options != null) sb.AppendLine("Options: " + Job.Options.Profile + " " + Job.Options.Format + " h<=" + Job.Options.Height + " strict=" + Job.Options.Strict + " codec=" + Job.Options.Codec + " fps<=" + Job.Options.FpsLimit);
                if (!string.IsNullOrEmpty(Job.ErrorDetail)) sb.AppendLine(Job.ErrorDetail);
                sb.Append(LogText);
                return Engine.Mask(sb.ToString());
            }
        }
    }

    public class QueueService {
        public readonly ObservableCollection<JobItem> Items = new ObservableCollection<JobItem>();
        public event Action<JobItem> Finished;
        public event Action<JobItem> NeedsAttention;
        public event Action ProgressTick;   // once a second while something runs (sidebar, taskbar, tray)
        public event Action Changed;
        readonly AppPaths paths;
        readonly Func<FlowSettings> settings;
        readonly IJobExecutor executor;
        readonly DispatcherTimer tick, saver;
        bool shuttingDown;
        List<BrowserReceipt> browserReceipts = new List<BrowserReceipt>();
        public LoadResult LoadInfo { get; private set; }
        public static readonly int[] Backoff = { 15, 60, 180, 300, 600 };

        public QueueService(AppPaths paths, Func<FlowSettings> settings, IJobExecutor executor) {
            this.paths = paths; this.settings = settings; this.executor = executor;
            tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tick.Tick += (s, e) => {
                Pump();
                foreach (var i in Items.Where(x => x.Job.State == JobState.Retry || x.Job.State == JobState.Scheduled)) i.Refresh();
                var h = ProgressTick; if (h != null && ActiveCount > 0) h();
            };
            saver = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            saver.Tick += (s, e) => { saver.Stop(); SaveNow(); };
        }

        public void Start() { tick.Start(); Pump(); }
        public int ActiveCount { get { return Items.Count(i => i.IsActive); } }
        public int WaitingCount { get { return Items.Count(i => i.Job.State == JobState.Queued || i.Job.State == JobState.Retry || i.Job.State == JobState.Scheduled); } }
        public double OverallPercent {
            get {
                var active = Items.Where(i => i.IsActive && !i.Indeterminate).ToList();
                return active.Count == 0 ? -1 : active.Average(i => i.ProgressValue);
            }
        }

        // ---- Persistence and recovery ----
        public void Load() { Load(false); }
        // scheduledLaunch: Flow was opened by its Windows task, so overdue scheduled jobs start regardless of the policy.
        public void Load(bool scheduledLaunch) {
            LoadInfo = JsonStore.Load(paths.QueueFile);
            var file = JsonStore.Convert<QueueFile>(LoadInfo.Data);
            browserReceipts = Receipts(file == null ? null : file.Jobs, file == null ? null : file.BrowserReceipts);
            var now = DateTime.UtcNow;
            var s = settings();
            foreach (var job in (file == null || file.Jobs == null ? new List<DownloadJob>() : file.Jobs)) {
                if (job == null || string.IsNullOrEmpty(job.Url) || job.Options == null) continue;
                Uri uri; if (!Platforms.TryPublicWebUrl(job.Url, out uri)) continue;
                if (string.IsNullOrEmpty(job.Id)) job.Id = Guid.NewGuid().ToString("N");
                if (!JobState.All.Contains(job.State)) job.State = JobState.Paused;
                if (job.State == JobState.Completed) continue;   // finished work lives in history
                if (JobState.IsActive(job.State)) {
                    job.Interrupted = true; job.Note = "note.interrupted";
                    job.State = s.ResumeInterrupted ? JobState.Queued : JobState.Paused;
                }
                if (job.State == JobState.Scheduled) {
                    var at = Iso.Parse(job.ScheduledAt);
                    if (!at.HasValue || at.Value < now.AddMinutes(-1)) { job.Note = "note.missed"; job.State = s.MissedSchedule == "start" || scheduledLaunch ? JobState.Queued : JobState.Paused; }
                }
                if (job.Options.SubLangs == null) job.Options.SubLangs = new List<string>();
                Items.Add(new JobItem(job));
            }
            RaiseChanged();
        }
        public void SaveNow() {
            try { SaveRequired(); } catch (Exception) {}
        }
        // Browser acknowledgement must not claim durability when the queue could not be written.
        internal void SaveRequired() {
            var jobs = Items.Select(i => i.Job).ToList();
            var receipts = Receipts(jobs, browserReceipts);
            JsonStore.Save(paths.QueueFile, new QueueFile { SchemaVersion = QueueFile.CurrentSchema, Jobs = jobs, BrowserReceipts = receipts });
            browserReceipts = receipts;
        }
        static List<BrowserReceipt> Receipts(IEnumerable<DownloadJob> jobs, IEnumerable<BrowserReceipt> saved) {
            // Keep the most recent 1,000 submissions even after completed jobs leave the queue.
            return (jobs ?? new DownloadJob[0]).Where(j => j != null && !string.IsNullOrEmpty(j.BrowserRequestId))
                .Select(j => new BrowserReceipt { RequestId = j.BrowserRequestId, JobId = j.Id, CreatedAt = j.CreatedAt })
                .Concat(saved ?? new BrowserReceipt[0]).Where(r => r != null && !string.IsNullOrEmpty(r.RequestId) && !string.IsNullOrEmpty(r.JobId))
                .OrderByDescending(r => r.CreatedAt).GroupBy(r => r.RequestId).Select(g => g.First()).Take(1000).ToList();
        }
        internal string FindBrowserJobId(string requestId) {
            var item = Items.FirstOrDefault(i => i.Job.BrowserRequestId == requestId);
            if (item != null) return item.Job.Id;
            var receipt = browserReceipts.FirstOrDefault(r => r.RequestId == requestId);
            return receipt == null ? null : receipt.JobId;
        }
        void ScheduleSave() { if (shuttingDown) return; saver.Stop(); saver.Start(); }
        void RaiseChanged() { var h = Changed; if (h != null) h(); }

        // ---- Commands ----
        public JobItem Add(DownloadJob job, bool hold) { return Add(job, hold, null); }
        public JobItem Add(DownloadJob job, bool hold, MediaInfo media) {
            if (string.IsNullOrEmpty(job.Id)) job.Id = Guid.NewGuid().ToString("N");
            job.CreatedAt = Iso.Now();
            var at = Iso.Parse(job.ScheduledAt);
            job.State = hold ? JobState.Paused : at.HasValue && at.Value > DateTime.UtcNow ? JobState.Scheduled : JobState.Queued;
            var item = new JobItem(job);
            if (media != null) { item.Media = media; item.MediaAt = DateTime.UtcNow; }
            Items.Add(item);
            ScheduleSave(); RaiseChanged(); Pump();
            return item;
        }
        public void Pause(JobItem item) {
            if (item.IsActive) { item.PauseRequested = true; if (item.Cts != null) item.Cts.Cancel(); return; }
            if (item.CanPause) { item.Job.State = JobState.Paused; item.Refresh(); Commit(); }
        }
        public void Resume(JobItem item) {
            if (!item.CanResume) return;
            item.Job.State = JobState.Queued; item.Job.Error = null; item.Job.ErrorArgs = null; item.Job.Attempts = 0;
            item.Refresh(); Commit();
        }
        public void Retry(JobItem item) {
            if (!item.CanRetry && item.Job.State != JobState.NeedsAction && item.Job.State != JobState.Retry) return;
            item.Job.State = JobState.Queued; item.Job.Error = null; item.Job.ErrorArgs = null; item.Job.Attempts = 0; item.Job.NextRetryAt = null; item.Job.FinishedAt = null;
            item.Refresh(); Commit();
        }
        public void Cancel(JobItem item) {
            if (item.IsActive) { item.CancelRequested = true; if (item.Cts != null) item.Cts.Cancel(); return; }
            if (JobState.IsFinished(item.Job.State)) return;
            item.Job.State = JobState.Canceled; item.Job.FinishedAt = Iso.Now();
            EngineExecutor.TryDelete(item.Job.TempDir, item.Job.Id);
            item.Refresh(); Commit(); Finish(item);
        }
        public void Remove(JobItem item) {
            if (item.IsActive) return;
            if (item.Job.State != JobState.Completed) EngineExecutor.TryDelete(item.Job.TempDir, item.Job.Id);
            Items.Remove(item); Commit();
        }
        public void Move(JobItem item, int delta) {
            int index = Items.IndexOf(item), target = index + delta;
            if (index < 0 || target < 0 || target >= Items.Count) return;
            Items.Move(index, target); Commit();
        }
        public void PauseAll() { foreach (var i in Items.ToList()) if (i.CanPause) Pause(i); }
        public void ResumeAll() { foreach (var i in Items.ToList()) if (i.Job.State == JobState.Paused) Resume(i); }
        public void ClearFinished() { foreach (var i in Items.Where(x => JobState.IsFinished(x.Job.State)).ToList()) Items.Remove(i); Commit(); }
        public void ChangeFolder(JobItem item, string folder) {
            if (item.IsActive) return;
            EngineExecutor.TryDelete(item.Job.TempDir, item.Job.Id);
            item.Job.Options.Folder = folder; item.Job.TempDir = null; item.Job.OutputPath = null;
            Retry(item); item.Job.State = JobState.Queued; item.Refresh(); Commit();
        }
        // The least surprising alternative for a format problem.
        public void ChangeFormat(JobItem item) {
            if (item.IsActive) return;
            var o = item.Job.Options; ErrorCode code; Enum.TryParse(item.Job.Error ?? "", out code);
            if (code == ErrorCode.NoAudio) { o.Format = "mp4"; o.Profile = "custom"; o.Codec = "auto"; }
            else if (code == ErrorCode.QualityUnavailable) o.Strict = false;
            else { o.Format = "mkv"; o.Profile = "best"; o.Codec = "auto"; o.Height = 0; o.Strict = false; }
            item.Job.ResolvedFormat = null; item.Job.Error = null; item.Job.State = JobState.Queued; item.Job.Attempts = 0;
            item.Refresh(); Commit();
        }
        void Commit() { ScheduleSave(); RaiseChanged(); Pump(); }

        // ---- Scheduling ----
        public void Pump() {
            if (shuttingDown) return;
            var now = DateTime.UtcNow;
            foreach (var item in Items) {
                var job = item.Job;
                if (job.State == JobState.Scheduled) {
                    var at = Iso.Parse(job.ScheduledAt);
                    if (!at.HasValue || at.Value <= now) {
                        // Much later than planned means the PC slept (or Flow was blocked): follow the missed-time policy.
                        bool missed = at.HasValue && (now - at.Value).TotalMinutes > 2;
                        if (missed) job.Note = "note.missed";
                        job.State = missed && settings().MissedSchedule == "hold" ? JobState.Paused : JobState.Queued;
                        item.Refresh(); ScheduleSave();
                    }
                }
                else if (job.State == JobState.Retry) { var at = Iso.Parse(job.NextRetryAt); if (!at.HasValue || at.Value <= now) { job.State = JobState.Queued; item.Refresh(); ScheduleSave(); } }
            }
            int limit = Math.Max(1, Math.Min(3, settings().Parallel));
            int running = ActiveCount;
            foreach (var item in Items.ToList()) {
                if (running >= limit) break;
                if (item.Job.State != JobState.Queued || item.Cts != null) continue;
                running++;
                Run(item);
            }
        }

        async void Run(JobItem item) {
            var job = item.Job;
            item.Cts = new CancellationTokenSource();
            item.ResetRuntime();
            job.State = JobState.Inspecting; job.Error = null; job.ErrorArgs = null; job.ErrorDetail = null; job.Attempts++; job.StartedAt = Iso.Now(); job.NextRetryAt = null;
            item.Refresh(); ScheduleSave(); RaiseChanged();
            var context = new JobContext {
                ReservedByOther = (path, id) => Items.Any(i => i.Job.Id != id && !JobState.IsFinished(i.Job.State) && string.Equals(i.Job.OutputPath, path, StringComparison.OrdinalIgnoreCase)),
                CachedInfo = (url, index) => item.Media != null && url == job.Url && (DateTime.UtcNow - item.MediaAt).TotalMinutes < 20 ? item.Media : null
            };
            var dispatcher = Dispatcher.CurrentDispatcher;
            context.Post = action => { if (dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action); };
            context.Log = line => context.Post(() => item.AppendLog(line));
            try {
                await executor.Run(item, context, item.Cts.Token);
                await Task.Yield();   // let queued progress callbacks run before finishing
                if (job.State != JobState.Completed) { job.State = JobState.Completed; job.FinishedAt = Iso.Now(); }
                job.Error = null; job.Interrupted = false;
                item.Refresh();
                Finish(item);
            } catch (Exception ex) {
                Fail(item, ex);
            } finally {
                var cts = item.Cts; item.Cts = null; if (cts != null) cts.Dispose();
                bool keepFlags = shuttingDown;
                if (!keepFlags) item.ResetRuntime();
                item.Refresh();
                ScheduleSave(); RaiseChanged();
                if (!shuttingDown) Pump();
            }
        }

        void Fail(JobItem item, Exception ex) {
            var job = item.Job;
            var code = ex is OperationCanceledException ? ErrorCode.Canceled : Errors.FromException(ex);
            var flow = ex as FlowException;
            job.ErrorDetail = flow != null ? Truncate(Engine.Mask(flow.Detail), 4000) : Truncate(Engine.Mask(ex.ToString()), 4000);
            item.AppendLog(job.ErrorDetail ?? "");
            if (code == ErrorCode.Canceled) {
                if (item.CancelRequested) {
                    job.State = JobState.Canceled; job.FinishedAt = Iso.Now(); job.Error = null;
                    EngineExecutor.TryDelete(job.TempDir, job.Id);
                    Finish(item);
                } else {
                    job.State = JobState.Paused; job.Error = null;
                    if (!item.PauseRequested) { job.Interrupted = true; job.Note = "note.interrupted"; }
                }
                return;
            }
            job.Error = code.ToString();
            job.ErrorArgs = flow != null && flow.Args.Length > 0 ? flow.Args.ToList() : null;
            var s = settings();
            if (Errors.IsTransient(code) && job.Attempts <= s.Retries) {
                job.State = JobState.Retry;
                job.NextRetryAt = Iso.From(DateTime.UtcNow.AddSeconds(Backoff[Math.Min(Backoff.Length - 1, job.Attempts - 1)]));
            } else if (Errors.NeedsUser(code)) {
                job.State = JobState.NeedsAction;
                var h = NeedsAttention; if (h != null) h(item);
            } else {
                job.State = JobState.Failed; job.FinishedAt = Iso.Now();
                Finish(item);
            }
        }
        static string Truncate(string text, int max) { return text == null || text.Length <= max ? text : text.Substring(0, max) + "…"; }
        void Finish(JobItem item) { var h = Finished; if (h != null) h(item); }

        // Closing Flow: running jobs are paused (resumable) and the queue is written before exit.
        public void Shutdown() {
            shuttingDown = true;
            tick.Stop(); saver.Stop();
            foreach (var item in Items.Where(i => i.IsActive).ToList()) {
                item.PauseRequested = true;
                if (item.Cts != null) item.Cts.Cancel();
                item.Job.State = JobState.Paused;
            }
            SaveNow();
        }
        public bool ShuttingDown { get { return shuttingDown; } }
    }
}
