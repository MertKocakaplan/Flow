using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Flow {
    // History rows are built from stored data fields, so they can be shown in either language.
    public class HistoryRow {
        public HistoryEntry Entry;
        public bool Exists;
        public string Title { get { return string.IsNullOrWhiteSpace(Entry.Title) ? (Entry.SourceUrl ?? "") : Entry.Title; } }
        public string FilePath { get { return Entry.FilePath; } }
        public string Summary {
            get {
                var parts = new List<string>();
                var when = Iso.Parse(Entry.CompletedAt);
                if (when.HasValue) parts.Add(Loc.Date(when.Value));
                if (!string.IsNullOrEmpty(Entry.Platform)) parts.Add(Entry.Platform == "Other" ? Loc.T("tab.other") : Entry.Platform);
                if (!string.IsNullOrEmpty(Entry.Channel)) parts.Add(Entry.Channel);
                if (!string.IsNullOrEmpty(Entry.Format)) parts.Add(Entry.Format.ToUpperInvariant());
                if (Entry.Width > 0 && Entry.Height > 0) parts.Add(Entry.Width + "×" + Entry.Height);
                if (!string.IsNullOrEmpty(Entry.Clip)) parts.Add(Entry.Clip);
                if (Entry.SizeBytes > 0) parts.Add(Loc.Size(Entry.SizeBytes));
                if (Entry.Legacy) parts.Add(Loc.T("hist.legacy"));
                return string.Join("  ·  ", parts);
            }
        }
        public string Kind { get { return Entry.Status == "failed" ? "error" : (Entry.Status == "completed" || Entry.Status == "skipped") && !Exists ? "missing" : Entry.Status == "completed" || Entry.Status == "skipped" ? "done" : "muted"; } }
        public string StatusText { get { return Kind == "missing" ? Loc.T("hist.missing") : Loc.Has("hist.st." + Entry.Status) ? Loc.T("hist.st." + Entry.Status) : Entry.Status; } }
        // Display only: the day the file was made for the numeral column, and the source for its colour key.
        public string DayText { get { var at = Iso.Parse(Entry.CompletedAt); return at.HasValue ? at.Value.ToLocalTime().ToString("dd.MM", Loc.Culture) : "—"; } }
        public string PlatformKey { get { return Entry.Platform ?? ""; } }
        public string PathText { get { return !string.IsNullOrEmpty(Entry.FilePath) ? Entry.FilePath : Entry.SourceUrl == null ? Loc.T("hist.noSource") : ""; } }
        public string ErrorText { get { ErrorCode code; return Entry.Status == "failed" && Enum.TryParse(Entry.Error ?? "", out code) ? Loc.Error(code, (Entry.ErrorArgs ?? new List<string>()).Cast<object>().ToArray()) : ""; } }
        public bool CanReveal { get { return Exists; } }
        public bool CanOpen { get { return Exists && Controller.CanOpen(Entry.FilePath); } }
        public bool HasSource { get { return !string.IsNullOrEmpty(Entry.SourceUrl); } }
    }

    public partial class Controller {
        void InitHistory() {
            C<ItemsControl>("HistoryList").AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnHistoryButton));
            C<Button>("ClearHistoryButton").Click += (s, e) => {
                if (history.Count == 0) return;
                if (ConfirmClose && MessageBox.Show(Window, Loc.T("hist.clear.confirm"), "Flow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                history.Clear(); SaveHistory(); RefreshHistory();
            };
        }
        void InitHistoryFilters() {
            foreach (var name in new[] { "HistoryPlatformBox", "HistoryStatusBox", "HistoryDateBox" }) { C<ComboBox>(name).SelectedIndex = 0; C<ComboBox>(name).SelectionChanged += (s, e) => FilterHistory(); }
            C<TextBox>("HistorySearch").TextChanged += (s, e) => FilterHistory();
        }

        public List<HistoryRow> HistoryRows { get { var list = C<ItemsControl>("HistoryList").ItemsSource as List<HistoryRow>; return list ?? new List<HistoryRow>(); } }

        // Whether each file still exists is read once per refresh; filtering and typing reuse the answer.
        readonly Dictionary<string, bool> historyFiles = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool HistoryFileExists(string path) {
            if (string.IsNullOrEmpty(path)) return false;
            bool exists;
            if (!historyFiles.TryGetValue(path, out exists)) historyFiles[path] = exists = File.Exists(path);
            return exists;
        }
        void RefreshHistory() { historyFiles.Clear(); FilterHistory(); }
        void FilterHistory() {
            var list = C<ItemsControl>("HistoryList");
            if (list == null || C<ComboBox>("HistoryPlatformBox") == null) return;
            var search = (C<TextBox>("HistorySearch").Text ?? "").Trim();
            var platform = TagOf(C<ComboBox>("HistoryPlatformBox")) ?? "all";
            var status = TagOf(C<ComboBox>("HistoryStatusBox")) ?? "all";
            int days; var since = int.TryParse(TagOf(C<ComboBox>("HistoryDateBox")), out days) ? DateTime.UtcNow.AddDays(-days) : DateTime.MinValue;
            var rows = history.Where(h => {
                if (platform != "all" && h.Platform != platform) return false;
                if (status != "all" && h.Status != status) return false;
                var when = Iso.Parse(h.CompletedAt);
                if (since > DateTime.MinValue && (!when.HasValue || when.Value < since)) return false;
                if (search.Length > 0) {
                    var hay = (h.Title ?? "") + " " + (h.Channel ?? "") + " " + (string.IsNullOrEmpty(h.FilePath) ? "" : Path.GetFileName(h.FilePath));
                    if (hay.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0 && hay.ToLower(Loc.Culture).IndexOf(search.ToLower(Loc.Culture), StringComparison.Ordinal) < 0) return false;
                }
                return true;
            }).Select(h => new HistoryRow { Entry = h, Exists = HistoryFileExists(h.FilePath) }).ToList();
            list.ItemsSource = rows;
            bool filtered = rows.Count != history.Count;
            C<Border>("HistoryEmpty").Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            C<TextBlock>("HistoryEmptyText").Text = Loc.T(history.Count == 0 ? "hist.empty" : "hist.empty.filtered");
            C<TextBlock>("HistoryCount").Text = filtered ? Loc.F("hist.count.filtered", rows.Count, history.Count) : Loc.F("hist.count", history.Count);
            C<Button>("ClearHistoryButton").IsEnabled = history.Count > 0;
        }

        void OnHistoryButton(object sender, RoutedEventArgs e) {
            var button = e.OriginalSource as FrameworkElement;
            var row = button == null ? null : button.DataContext as HistoryRow;
            if (row == null) return;
            e.Handled = true;
            switch (Convert.ToString(button.Tag)) {
                case "reveal": Reveal(row.Entry.FilePath); break;
                case "open": OpenFile(row.Entry.FilePath); break;
                case "source": OpenSource(row.Entry.SourceUrl); break;
                case "requeue": Requeue(row.Entry); break;
                case "forget": history.Remove(row.Entry); SaveHistory(); RefreshHistory(); break;
            }
        }

        // Only public http(s) links recorded by Flow are opened; nothing is invented for legacy entries.
        void OpenSource(string url) {
            Uri uri;
            if (string.IsNullOrEmpty(url) || !Platforms.TryPublicWebUrl(url, out uri)) return;
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) { ShowMessage(ex.Message, true); }
        }

        internal JobItem Requeue(HistoryEntry entry) {
            UrlInfo url;
            try { url = Platforms.Parse(entry.SourceUrl); } catch (Exception ex) { ShowMessage(Loc.Error(ex), true); return null; }
            JobOptions options;
            var saved = entry.Options;
            // Entries written since the full option copy was kept ask for exactly the same job again: folder, range,
            // subtitles, speed and naming included. Older entries rebuild what they recorded on top of today's settings.
            if (saved != null && (entry.MediaKind == "image" || SettingsRules.Formats.Contains(saved.Format)) && !string.IsNullOrWhiteSpace(saved.Folder) && Path.IsPathRooted(saved.Folder)) {
                options = saved.Copy();
            } else {
                options = JobOptions.From(settings);
                if (SettingsRules.Formats.Contains(entry.Format)) options.Format = entry.Format;
                if (SettingsRules.Profiles.Contains(entry.Profile)) options.Profile = entry.Profile;
                if (options.Format == "mkv" && options.Profile == "best") { options.Codec = "auto"; options.Height = 0; }
                if (entry.RequestedCodec != null) { options.Codec = entry.RequestedCodec; options.Height = entry.RequestedHeight; options.FpsLimit = entry.RequestedFps; options.Strict = entry.RequestedStrict; }
                if(entry.MediaKind=="image"){options.Format=entry.Format;options.Profile="custom";options.Height=0;options.Codec="auto";}
            }
            var job = new DownloadJob { Url = url.Url, Platform = entry.Platform ?? url.Platform.ToString(), Title = entry.Title, Channel = entry.Channel, ContentId = entry.ContentId, Duration = entry.Duration, ItemIndex = entry.ItemIndex, Options = options,MediaKind=entry.MediaKind,Referrer=entry.Referrer, CollectionTitle = entry.CollectionTitle, CollectionIndex = entry.CollectionIndex };
            var item = queue.Add(job, false);
            ShowMessage(Loc.F("hist.requeued", item.Title), false, Loc.T("dl.showQueue"), () => ShowPage("queue"));
            return item;
        }
    }
}
