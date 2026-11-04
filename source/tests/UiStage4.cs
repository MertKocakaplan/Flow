using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Flow;

namespace FlowTests {
    public static partial class UiTests {
        static string BigList(int start, int end, int total) {
            var entries = Enumerable.Range(start, end - start + 1).Select(i => "{\"_type\":\"url\",\"id\":\"v" + i.ToString("0000000000") + "\",\"url\":\"https://www.youtube.com/watch?v=v" + i.ToString("0000000000") + "\",\"title\":\"Video " + i + "\",\"duration\":" + (60 + i) + ",\"playlist_index\":" + i + "}");
            return "{\"_type\":\"playlist\",\"id\":\"PLbig\",\"title\":\"Uzun liste\",\"extractor_key\":\"YoutubeTab\",\"playlist_count\":" + total + ",\"entries\":[" + string.Join(",", entries) + "]}";
        }
        static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button)); Pump(); }

        public static void Stage4Ui() {
            var paths = NewPaths("ui-lists");
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = "light";
            Store.SaveSettings(paths, s);
            var c = Open(paths, 1180, 900, new FakeExecutor());
            var w = c.Window;
            var calls = new List<string>();
            c.ListSource = (url, flat, start, end, token) => { calls.Add(url + " " + flat + " " + start + ":" + end); return Task.FromResult(T.Fixture("playlist-flat.json")); };
            var box = Find<TextBox>(w, "UrlBox");
            box.Text = "https://www.youtube.com/playlist?list=PLtest0123456789";
            Wait(() => c.CollectionRows.Count > 0, 4000);
            T.Check(c.CollectionOpen && c.CollectionRows.Count == 6 && calls.Count == 1 && calls[0].Contains("True 1:100"), "playlist link opens the list reader (first page)");
            T.Check(c.CollectionRows.Count(r => r.Selectable) == 2 && c.CollectionRows.Where(r => !r.Selectable).All(r => !r.Selected), "private, deleted, members-only and missing items cannot be selected");
            T.Check(c.CollectionRows[1].Status == Loc.T("col.private") && c.CollectionRows[4].Label.Contains(Loc.T("col.unavailable")), "unavailable items are explained, not hidden");
            T.Check(Find<TextBlock>(w, "StartText").Text == Loc.F("dl.start.selected", 2), "download button shows how many items are selected");
            Console.WriteLine("SCREEN " + Render(w, "stage4-playlist"));
            c.CollectionRows[0].Selected = false; Pump();
            int before = c.Queue.Items.Count;
            c.Enqueue(true);
            var added = c.Queue.Items.Skip(before).ToList();
            T.Check(added.Count == 1 && added[0].Job.Url == "https://www.youtube.com/watch?v=eeeeeeeeeee" && added[0].Job.CollectionTitle == "Test listesi" && added[0].Job.CollectionIndex == 6 && added[0].Job.ItemIndex == 0, "only the selected item is queued, as its own single-video job");
            T.Check(!c.CollectionOpen && box.Text == "", "list closes after queueing");

            // Paging and stopping
            c.ListSource = (url, flat, start, end, token) => Task.FromResult(BigList(start, Math.Min(end, 130), 130));
            box.Text = "https://www.youtube.com/playlist?list=PLbig000000";
            Wait(() => c.CollectionRows.Count == 100, 4000);
            T.Check(Find<Button>(w, "LoadMoreButton").IsVisible, "long lists load 100 at a time with a load-more option");
            Click(Find<Button>(w, "LoadMoreButton"));
            Wait(() => c.CollectionRows.Count == 130, 4000);
            T.Check(c.CollectionRows.Count == 130 && !Find<Button>(w, "LoadMoreButton").IsVisible && Find<TextBlock>(w, "CollectionMeta").Text.StartsWith("130"), "second page appended, no more pages");
            c.ListSource = async (url, flat, start, end, token) => { await Task.Delay(10000, token); return "{}"; };
            box.Text = "https://www.youtube.com/playlist?list=PLslow00000";
            Wait(() => Find<Button>(w, "StopListingButton").IsVisible, 3000);
            Click(Find<Button>(w, "StopListingButton"));
            Wait(() => !Find<Button>(w, "StopListingButton").IsVisible, 3000);
            T.Check(Find<Button>(w, "LoadMoreButton").IsVisible && c.CollectionRows.Count == 0, "reading a list can be stopped and retried");
            c.ListSource = (url, flat, start, end, token) => { throw new FlowException(ErrorCode.Unavailable); };
            box.Text = "https://www.youtube.com/playlist?list=PLgone00000";
            Wait(() => Find<TextBlock>(w, "CollectionTitle").Text.Contains(Loc.Error(ErrorCode.Unavailable)), 3000);
            T.Check(Find<TextBlock>(w, "CollectionTitle").Text.Contains(Loc.Error(ErrorCode.Unavailable)), "an unreadable list is explained");

            // Watch link inside a list: video first, list on request
            c.InfoSource = (u, token) => Task.FromResult(MediaParser.Parse(T.Fixture("youtube.json"), Platform.YouTube));
            c.ListSource = (url, flat, start, end, token) => Task.FromResult(T.Fixture("playlist-flat.json"));
            box.Text = "https://www.youtube.com/watch?v=jNQXAC9IVRw&list=PLtest0123456789";
            Wait(() => Find<Button>(w, "OpenListButton").IsVisible, 4000);
            T.Check(!c.CollectionOpen && Find<Button>(w, "OpenListButton").IsVisible, "a video link with a list downloads the video unless the list is opened");
            Click(Find<Button>(w, "OpenListButton"));
            Wait(() => c.CollectionRows.Count > 0, 3000);
            T.Check(c.CollectionOpen && c.CollectionRows.Count == 6, "the list opens on request");

            // Instagram multi-item post: photos listed, not downloadable
            c.InfoSource = (u, token) => Task.FromResult(MediaParser.Parse(T.Fixture("instagram-carousel.json"), Platform.Instagram));
            c.ListSource = (url, flat, start, end, token) => { calls.Add(url + " " + flat); return Task.FromResult(T.Fixture("instagram-carousel.json")); };
            box.Text = "https://www.instagram.com/p/CAROUSEL1/";
            Wait(() => c.CollectionRows.Count == 3, 4000);
            T.Check(c.CollectionOpen && calls.Last().EndsWith("False") && c.CollectionRows[1].Status == Loc.T("col.photo") && !c.CollectionRows[1].Selectable && c.CollectionRows.Count(r => r.Selected) == 2, "multi-item post lists its videos; the photo is shown but not selectable");
            Console.WriteLine("SCREEN " + Render(w, "stage4-instagram-post"));
            before = c.Queue.Items.Count;
            c.Enqueue(true);
            added = c.Queue.Items.Skip(before).ToList();
            T.Check(added.Count == 2 && added.All(i => i.Job.Url == "https://www.instagram.com/p/CAROUSEL1/") && added[0].Job.ItemIndex == 1 && added[1].Job.ItemIndex == 3, "each chosen post item is queued with its own position");

            // File name template and folders
            var tpl = Find<TextBox>(w, "TemplateBox");
            c.InfoSource = (u, token) => Task.FromResult(MediaParser.Parse(T.Fixture("youtube.json"), Platform.YouTube));
            box.Text = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
            Wait(() => Find<TextBlock>(w, "NamePreview").Text.Contains("jNQXAC9IVRw"), 4000);
            tpl.Text = "{channel} - {title}"; Pump();
            T.Check(Find<TextBlock>(w, "NamePreview").Text.Contains("jawed - Me at the zoo 240p.mp4") && Store.Load(paths).Settings.NameTemplate == "{channel} - {title}", "template change shows the new name at once and is saved");
            tpl.Text = "{channel}/{nope}"; Pump();
            T.Check(Find<TextBlock>(w, "TemplateHint").Text == Loc.T("opt.template.invalid") && Store.Load(paths).Settings.NameTemplate == "{channel} - {title}", "invalid template explained and not saved");
            tpl.Text = "{title} [{id}]"; Pump();
            SelectByTag(Find<ComboBox>(w, "OrganizeBox"), "platform-channel"); Pump();
            T.Check(Store.Load(paths).Settings.Organize == "platform-channel", "folder layout choice saved");
            w.Close();
        }

        public static void Stage4History() {
            var paths = NewPaths("ui-history");
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = "light";
            Store.SaveSettings(paths, s);
            Directory.CreateDirectory(paths.DefaultDownloads);
            var kept = Path.Combine(paths.DefaultDownloads, "Kalan video [abc] 720p.mp4"); File.WriteAllText(kept, "x");
            var entries = new List<HistoryEntry> {
                new HistoryEntry { Id = "1", Title = "Kalan video", Platform = "YouTube", SourceUrl = "https://www.youtube.com/watch?v=jNQXAC9IVRw", FilePath = kept, CompletedAt = Iso.Now(), Status = "completed", Format = "mp4", Profile = "compatible", Width = 1280, Height = 720, SizeBytes = 5242880, Channel = "Kanal A" },
                new HistoryEntry { Id = "2", Title = "Taşınmış dosya", Platform = "Instagram", SourceUrl = "https://www.instagram.com/reel/Chunk8-jurw/", FilePath = Path.Combine(paths.DefaultDownloads, "yok.mp4"), CompletedAt = Iso.From(DateTime.UtcNow.AddDays(-3)), Status = "completed", Format = "mp4" },
                new HistoryEntry { Id = "3", Title = "Engellenen video", Platform = "TikTok", SourceUrl = "https://www.tiktok.com/@a/video/1", CompletedAt = Iso.From(DateTime.UtcNow.AddDays(-10)), Status = "failed", Error = "IpBlocked", Format = "mp4" },
                new HistoryEntry { Id = "4", Title = "Eski Akış kaydı", FilePath = kept, CompletedAt = Iso.From(DateTime.UtcNow.AddDays(-40)), Status = "completed", Format = "mp3", Legacy = true }
            };
            Store.SaveHistory(paths, entries);
            var c = Open(paths, 1180, 900, new FakeExecutor());
            var w = c.Window;
            c.ShowPage("history"); Pump();
            T.Check(c.HistoryRows.Count == 4 && Find<TextBlock>(w, "HistoryCount").Text == Loc.F("hist.count", 4), "history lists every entry with a count");
            var missing = c.HistoryRows.First(r => r.Entry.Id == "2");
            T.Check(missing.Kind == "missing" && missing.StatusText == Loc.T("hist.missing") && !missing.CanReveal && missing.HasSource, "moved or deleted files are marked, source still available");
            var legacy = c.HistoryRows.First(r => r.Entry.Id == "4");
            T.Check(!legacy.HasSource && legacy.Summary.Contains(Loc.T("hist.legacy")) && legacy.PathText == kept, "Akış entries have no invented source link");
            T.Check(c.HistoryRows.First(r => r.Entry.Id == "3").ErrorText == Loc.Error(ErrorCode.IpBlocked), "failed entries show the reason");
            Console.WriteLine("SCREEN " + Render(w, "stage4-history"));
            Find<TextBox>(w, "HistorySearch").Text = "kanal a"; Pump();
            T.Check(c.HistoryRows.Count == 1 && c.HistoryRows[0].Entry.Id == "1", "search matches titles and channels, case-insensitively (Turkish)");
            Find<TextBox>(w, "HistorySearch").Text = ""; Pump();
            SelectByTag(Find<ComboBox>(w, "HistoryStatusBox"), "failed"); Pump();
            T.Check(c.HistoryRows.Count == 1 && c.HistoryRows[0].Entry.Id == "3", "status filter");
            SelectByTag(Find<ComboBox>(w, "HistoryStatusBox"), "all"); SelectByTag(Find<ComboBox>(w, "HistoryDateBox"), "7"); Pump();
            T.Check(c.HistoryRows.Count == 2 && Find<TextBlock>(w, "HistoryCount").Text == Loc.F("hist.count.filtered", 2, 4), "date filter with filtered count");
            SelectByTag(Find<ComboBox>(w, "HistoryDateBox"), "all"); SelectByTag(Find<ComboBox>(w, "HistoryPlatformBox"), "Instagram"); Pump();
            T.Check(c.HistoryRows.Count == 1 && c.HistoryRows[0].Entry.Id == "2", "platform filter");
            SelectByTag(Find<ComboBox>(w, "HistoryPlatformBox"), "all"); Pump();
            int queued = c.Queue.Items.Count;
            var job = c.Requeue(entries[2]);
            T.Check(job != null && c.Queue.Items.Count == queued + 1 && job.Job.Url == "https://www.tiktok.com/@a/video/1" && job.Job.Platform == "TikTok", "an entry can be queued again from its recorded source");
            var forget = Visuals(Find<ItemsControl>(w, "HistoryList")).OfType<Button>().First(b => Convert.ToString(b.Tag) == "forget" && ((HistoryRow)b.DataContext).Entry.Id == "1");
            Click(forget);
            T.Check(c.HistoryRows.All(r => r.Entry.Id != "1") && File.Exists(kept) && Store.Load(paths).History.Count == 3, "removing an entry keeps the media file");
            Click(Find<Button>(w, "ClearHistoryButton"));
            T.Check(Store.Load(paths).History.Count == 0 && File.Exists(kept) && Find<Border>(w, "HistoryEmpty").IsVisible, "clearing history deletes records only");
            w.Close();
        }
    }
}
