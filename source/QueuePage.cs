using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace Flow {
    public partial class Controller {
        QueueService queue;
        public QueueService Queue { get { return queue; } }

        void InitQueue(IJobExecutor executor) {
            queue = new QueueService(paths, () => settings, executor ?? new EngineExecutor());
            queue.Load(Program.ScheduledLaunch);
            var view = CollectionViewSource.GetDefaultView(queue.Items);
            view.Filter = o => { var f = TagOf(C<ComboBox>("QueueFilterBox")); return f == null || f == "all" || ((JobItem)o).Job.Platform == f; };
            C<ItemsControl>("QueueList").ItemsSource = view;
            C<ComboBox>("QueueFilterBox").SelectedIndex = 0;
            C<ComboBox>("QueueFilterBox").SelectionChanged += (s, e) => view.Refresh();
            C<ItemsControl>("QueueList").AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnJobButton));
            C<Button>("ResumeAllButton").Click += (s, e) => queue.ResumeAll();
            C<Button>("PauseAllButton").Click += (s, e) => queue.PauseAll();
            C<Button>("ClearFinishedButton").Click += (s, e) => queue.ClearFinished();
            C<Button>("QueueGoDownload").Click += (s, e) => { ShowPage("download"); C<TextBox>("UrlBox").Focus(); };
            C<Border>("SidebarActivity").MouseLeftButtonUp += (s, e) => ShowPage("queue");
            queue.Changed += UpdateQueueIndicators;
            queue.ProgressTick += UpdateQueueIndicators;
            queue.Finished += OnJobFinished;
            queue.Items.CollectionChanged += (s, e) => UpdateQueueIndicators();
            UpdateQueueIndicators();
        }

        void OnJobButton(object sender, RoutedEventArgs e) {
            var button = e.OriginalSource as FrameworkElement;
            var item = button == null ? null : button.DataContext as JobItem;
            if (item == null) return;
            e.Handled = true;
            switch (Convert.ToString(button.Tag)) {
                case "pause": queue.Pause(item); break;
                case "resume": queue.Resume(item); break;
                case "retry": queue.Retry(item); break;
                case "cancel": queue.Cancel(item); break;
                case "remove": queue.Remove(item); break;
                case "up": queue.Move(item, -1); break;
                case "down": queue.Move(item, 1); break;
                case "reveal": Reveal(item.Job.OutputPath); break;
                case "open": OpenFile(item.Job.OutputPath); break;
                case "folder": ChooseFolder(path => queue.ChangeFolder(item, path), item.Job.Options.Folder); break;
                case "format": queue.ChangeFormat(item); break;
                case "copy":
                    try { Clipboard.SetText(item.Diagnostics); ShowQueueNotice(Loc.T("job.copied")); } catch (Exception) { ShowQueueNotice(Loc.T("msg.clipboard.error")); }
                    break;
            }
        }
        void ShowQueueNotice(string text) { ShowMessage(text, false); }

        void UpdateQueueIndicators() {
            if (queue == null) return;
            int active = queue.ActiveCount, waiting = queue.WaitingCount;
            C<Border>("QueueEmpty").Visibility = queue.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var badge = C<Border>("QueueBadge");
            badge.Visibility = active + waiting > 0 && !compact ? Visibility.Visible : Visibility.Collapsed;
            C<TextBlock>("QueueBadgeText").Text = (active + waiting).ToString(Loc.Culture);
            var panel = C<Border>("SidebarActivity");
            panel.Visibility = active + waiting > 0 && !compact ? Visibility.Visible : Visibility.Collapsed;
            C<TextBlock>("SidebarActivityText").Text = Loc.F("sidebar.activity", active, waiting);
            var overall = queue.OverallPercent;
            var bar = C<ProgressBar>("SidebarProgress");
            bar.IsIndeterminate = active > 0 && overall < 0;
            bar.Value = Math.Max(0, overall);
            if (active > 0) SetChip("status.active", "AccentSoft", "AccentText", active);
            else if (waiting > 0) SetChip("status.waiting", "Surface2", "TextMuted", waiting);
            else SetChip("status.ready", "AccentSoft", "AccentText");
            UpdateTaskbar(active, overall);
        }
        partial void UpdateTaskbar(int active, double overall);

        void OnJobFinished(JobItem item) {
            var job = item.Job;
            var status = job.State == JobState.Completed ? (job.Skipped ? "skipped" : "completed") : job.State == JobState.Canceled ? "canceled" : "failed";
            history.Insert(0, new HistoryEntry {
                Id = Guid.NewGuid().ToString("N"), Title = item.Title, Platform = job.Platform, SourceUrl = job.Url, ContentId = job.ContentId, Channel = job.Channel, MediaKind=job.MediaKind,Referrer=job.Referrer,
                FilePath = job.State == JobState.Completed ? job.OutputPath : null, CompletedAt = Iso.Now(), SizeBytes = job.State == JobState.Completed ? job.ExpectedBytes : 0,
                Format = job.Options.Format, Profile = job.Options.Profile, Width = job.Width, Height = job.Height, Duration = job.Duration,
                Status = status, Error = job.State == JobState.Failed ? job.Error : null, ErrorArgs = job.State == JobState.Failed ? job.ErrorArgs : null, ItemIndex = job.ItemIndex,
                RequestedCodec = job.Options.Codec, RequestedHeight = job.Options.Height, RequestedFps = job.Options.FpsLimit, RequestedStrict = job.Options.Strict,
                Clip = job.Options.HasClip ? Naming.ClipTag(job.Options.ClipStart, job.Options.ClipEnd) : null,
                Options = job.Options.Copy(), CollectionTitle = job.CollectionTitle, CollectionIndex = job.CollectionIndex
            });
            if (history.Count > HistoryRules.Limit) history.RemoveRange(HistoryRules.Limit, history.Count - HistoryRules.Limit);
            SaveHistory();
            if (page == "history") RefreshHistory();
            OnJobNotify(item, status);
        }
        partial void OnJobNotify(JobItem item, string status);
    }
}
