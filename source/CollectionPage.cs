using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Flow {
    public class CollectionRow : INotifyPropertyChanged {
        public CollectionEntry Entry;
        public event PropertyChangedEventHandler PropertyChanged;
        public Action Changed;
        bool selected;
        public bool Selected { get { return selected; } set { if (selected == value || (value && !Selectable)) return; selected = value; var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs("Selected")); if (Changed != null) Changed(); } }
        public bool Selectable { get { return Entry.Title != null && Entry.Available && Entry.IsVideo != Tri.No; } }
        public string Label { get { return Entry.Index + ".  " + (Entry.Title ?? Loc.T("col.unavailable")); } }
        public string Status { get { return Entry.Title == null ? "" : Entry.IsVideo == Tri.No ? Loc.T("col.photo") : !Entry.Available ? Loc.T("col.private") : ""; } }
        public string DurationText { get { return Entry.Duration > 0 ? Loc.Duration(Entry.Duration) : ""; } }
        public void Refresh() { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(string.Empty)); }
    }

    public partial class Controller {
        const int PageSize = 100;
        readonly ObservableCollection<CollectionRow> collectionRows = new ObservableCollection<CollectionRow>();
        CollectionInfo collection;
        UrlInfo collectionUrl;
        bool collectionFlat;
        int collectionLoaded;
        CancellationTokenSource listCts;
        // Replaceable in tests: (url, flat, start, end) -> engine JSON
        internal Func<string, bool, int, int, CancellationToken, Task<string>> ListSource = null;

        public ObservableCollection<CollectionRow> CollectionRows { get { return collectionRows; } }
        public bool CollectionOpen { get { return C<Border>("CollectionCard").Visibility == Visibility.Visible; } }

        partial void InitExtras() {
            C<ListBox>("CollectionList").ItemsSource = collectionRows;
            C<Button>("SelectAllButton").Click += (s, e) => { foreach (var r in collectionRows) r.Selected = true; };
            C<Button>("SelectNoneButton").Click += (s, e) => { foreach (var r in collectionRows) r.Selected = false; };
            C<Button>("LoadMoreButton").Click += (s, e) => LoadCollectionPage();
            C<Button>("StopListingButton").Click += (s, e) => { if (listCts != null) listCts.Cancel(); };
            C<Button>("CloseCollectionButton").Click += (s, e) => CloseCollection();
            C<Button>("OpenListButton").Click += (s, e) => { if (inspected != null && inspected.ListId != null) OpenCollection(Platforms.Parse("https://www.youtube.com/playlist?list=" + inspected.ListId)); };
            InitFileOptions();
            InitHistoryFilters();
            InitExtrasUi();
            InitStage6();
            InitStage7();
        }

        partial void OpenCollection(UrlInfo url) {
            CloseCollection();
            collectionUrl = url; collectionFlat = url.Platform == Platform.YouTube; collectionLoaded = 0; collection = null;
            C<Border>("CollectionCard").Visibility = Visibility.Visible;
            C<TextBlock>("CollectionTitle").Text = Loc.T(collectionFlat ? "col.loading" : "col.loadingPost");
            C<TextBlock>("CollectionNote").Text = Loc.T(collectionFlat ? "col.note.youtube" : "col.note.post");
            LoadCollectionPage();
        }
        void CloseCollection() {
            if (listCts != null) { listCts.Cancel(); listCts = null; }
            collectionRows.Clear(); collection = null; collectionUrl = null;
            C<Border>("CollectionCard").Visibility = Visibility.Collapsed;
            UpdateCollectionMeta();
        }
        partial void OnPreviewReset() { C<Button>("OpenListButton").Visibility = Visibility.Collapsed; if (collectionUrl != null) CloseCollection(); if (C<TextBox>("ClipStartBox") != null) ResetExtras(); }
        partial void OnPreviewApplied(MediaInfo info) {
            UpdateExtrasForMedia(info);
            C<Button>("OpenListButton").Visibility = inspected != null && inspected.ListId != null ? Visibility.Visible : Visibility.Collapsed;
            // A multi-item Instagram post: list its items so the videos can be chosen.
            if (info.IsCollection && info.Platform == Platform.Instagram && inspected != null) OpenCollection(inspected);
        }

        async void LoadCollectionPage() {
            if (collectionUrl == null) return;
            var url = collectionUrl;
            var cts = listCts = new CancellationTokenSource();
            int start = collectionLoaded + 1, end = collectionFlat ? collectionLoaded + PageSize : 0;
            C<ProgressBar>("CollectionProgress").Visibility = Visibility.Visible;
            C<Button>("StopListingButton").Visibility = Visibility.Visible;
            C<Button>("LoadMoreButton").Visibility = Visibility.Collapsed;
            try {
                var source = ListSource ?? new Func<string, bool, int, int, CancellationToken, Task<string>>(ReadCollection);
                var json = await source(url.Url, collectionFlat, start, end, cts.Token);
                if (url != collectionUrl) return;
                var page = MediaParser.ParseCollection(json, url.Platform);
                if (collection == null) collection = page; else if (page.TotalCount > 0) collection.TotalCount = page.TotalCount;
                if (page.Entries.Count == 0 && collectionRows.Count == 0) throw new FlowException(ErrorCode.CollectionEmpty);
                foreach (var entry in page.Entries) {
                    if (!collectionFlat) entry.Index = entry.Index > 0 ? entry.Index : collectionRows.Count + 1;
                    var row = new CollectionRow { Entry = entry, Changed = UpdateCollectionMeta };
                    row.Selected = row.Selectable;
                    collectionRows.Add(row);
                }
                collectionLoaded += page.Entries.Count;
                C<TextBlock>("CollectionTitle").Text = (string.IsNullOrWhiteSpace(collection.Title) ? Loc.T(collectionFlat ? "col.playlist" : "col.post") : collection.Title);
                bool more = collectionFlat && page.Entries.Count >= PageSize && (collection.TotalCount < 0 || collectionLoaded < collection.TotalCount);
                var load = C<Button>("LoadMoreButton");
                load.Content = Loc.F("col.loadMore", PageSize);
                load.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
            } catch (OperationCanceledException) {
                C<Button>("LoadMoreButton").Visibility = collectionFlat && collectionUrl != null ? Visibility.Visible : Visibility.Collapsed;
            } catch (Exception ex) {
                if (url == collectionUrl) {
                    if (collectionRows.Count == 0) C<TextBlock>("CollectionTitle").Text = Loc.F("col.failed", Loc.Error(ex));
                    else ShowMessage(Loc.F("col.failed", Loc.Error(ex)), true);
                }
            } finally {
                if (listCts == cts) listCts = null;
                cts.Dispose();
                C<ProgressBar>("CollectionProgress").Visibility = Visibility.Collapsed;
                C<Button>("StopListingButton").Visibility = Visibility.Collapsed;
                UpdateCollectionMeta();
            }
        }

        static async Task<string> ReadCollection(string url, bool flat, int start, int end, CancellationToken token) {
            Engine.EnsureTools();
            var result = await ProcessRunner.Run(Engine.Exe, Engine.ListArgs(url, flat, start, end), null, token, 300);
            if (result.ExitCode != 0) throw new FlowException(Engine.Classify(result.Error), result.Error);
            return result.Output;
        }

        void UpdateCollectionMeta() {
            int selected = collectionRows.Count(r => r.Selected);
            var meta = C<TextBlock>("CollectionMeta");
            if (meta != null) meta.Text = collection != null && collection.TotalCount > collectionRows.Count ? Loc.F("col.meta.total", collectionRows.Count, selected, collection.TotalCount) : Loc.F("col.meta", collectionRows.Count, selected);
            var start = C<TextBlock>("StartText");
            if (start != null) {
                if (collectionUrl != null) start.Text = Loc.F("dl.start.selected", selected);
                else start.SetResourceReference(TextBlock.TextProperty, "s.dl.start");
            }
        }

        // Each selected item becomes its own job; unselected items are never downloaded.
        JobItem EnqueueCollection(bool hold) {
            var rows = collectionRows.Where(r => r.Selected && r.Selectable).ToList();
            if (rows.Count == 0) { ShowMessage(Loc.T("col.none"), true); return null; }
            JobOptions options;
            try {
                options = CurrentOptions();
                if (string.IsNullOrWhiteSpace(options.Folder) || !System.IO.Path.IsPathRooted(options.Folder)) throw new FlowException(ErrorCode.FolderInvalid);
                ValidateExtras(options);
            } catch (Exception ex) { ShowMessage(Loc.Error(ex), true); return null; }
            JobItem last = null;
            foreach (var row in rows) {
                var e = row.Entry;
                string url = collectionUrl.Url; int item = 0;
                if (collectionFlat) {
                    UrlInfo single;
                    try { single = Platforms.Parse(e.Url ?? ("https://www.youtube.com/watch?v=" + e.Id)); } catch (FlowException) { continue; }
                    url = single.Url;
                } else item = e.Index;
                var job = new DownloadJob {
                    Url = url, ItemIndex = item, Platform = collectionUrl.Platform.ToString(), Options = options.Copy(),
                    Title = e.Title, ContentId = e.Id, Duration = e.Duration, Thumbnail = e.Thumbnail,
                    CollectionTitle = collection != null ? collection.Title : null, CollectionIndex = e.Index
                };
                ApplySchedule(job);
                last = queue.Add(job, hold);
            }
            ShowMessage(Loc.F("col.added", rows.Count), false, Loc.T("dl.showQueue"), () => ShowPage("queue"));
            CloseCollection();
            C<TextBox>("UrlBox").Text = "";
            return last;
        }
    }
}
