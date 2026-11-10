using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Flow {
    public partial class Controller {
        Platform currentTab = Platform.YouTube;
        MediaInfo video;
        UrlInfo inspected;
        int previewGeneration;
        CancellationTokenSource previewCts;
        DispatcherTimer previewTimer;
        Action messageAction;

        static readonly int[] GenericHeights = { 0, 2160, 1440, 1080, 720, 480, 360 };
        // Replaceable in tests so preview behavior can be checked without the network.
        internal Func<UrlInfo, CancellationToken, Task<MediaInfo>> InfoSource = null;

        void InitDownload() {
            foreach (var tab in new[] { "TabYouTube", "TabInstagram", "TabTikTok", "TabOther" })
                C<RadioButton>(tab).Checked += (s, e) => OnTabChecked((Platform)Enum.Parse(typeof(Platform), Convert.ToString(((FrameworkElement)s).Tag)));
            Platform last;
            if (!Enum.TryParse(settings.LastTab, out last)) last = Platform.YouTube;
            SelectTab(last);

            var url = C<TextBox>("UrlBox");
            url.TextChanged += (s, e) => OnUrlChanged();
            // Several links (one per line, or inside a sentence) are kept as links on one line.
            DataObject.AddPastingHandler(url, OnUrlPaste);
            url.KeyDown += (s, e) => { if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == 0) { e.Handled = true; InspectNow(); } };
            C<Button>("PasteButton").Click += (s, e) => PasteUrl();
            C<Button>("InspectButton").Click += (s, e) => InspectNow();
            C<Button>("StartButton").Click += (s, e) => StartDownload();
            C<Button>("AddHoldButton").Click += (s, e) => Enqueue(true);
            C<Button>("FolderButton").Click += (s, e) => ChooseFolder(path => { C<TextBox>("FolderBox").Text = path; UpdateSelectionSummary(); }, C<TextBox>("FolderBox").Text);
            C<Button>("MessageClose").Click += (s, e) => HideMessage();
            C<Button>("MessageAction").Click += (s, e) => { var a = messageAction; HideMessage(); if (a != null) a(); };

            C<TextBox>("FolderBox").Text = settings.Folder;
            BuildQualityItems(null);
            ApplySettingsToOptions();
            C<ComboBox>("ProfileBox").SelectionChanged += (s, e) => { if (!loading) OnProfileChanged(); };
            foreach (var name in new[] { "FormatBox", "QualityBox", "CodecBox", "FpsBox" }) C<ComboBox>(name).SelectionChanged += (s, e) => { if (!loading) OnOptionChanged(); };
            C<CheckBox>("StrictBox").Click += (s, e) => { if (!loading) OnOptionChanged(); };
            C<ComboBox>("DuplicateBox").SelectionChanged += (s, e) => { if (loading) return; settings.Duplicate = TagOf(C<ComboBox>("DuplicateBox")) ?? "rename"; SaveSettings(); };

            previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            previewTimer.Tick += (s, e) => { previewTimer.Stop(); if (settings.AutoPreview) Inspect(true); };
            RefreshDownloadTexts();
        }

        // ---- Messages ----
        public void ShowMessage(string message, bool error) { ShowMessage(message, error, null, null); }
        void ShowMessage(string message, bool error, string actionText, Action action) {
            var bar = C<Border>("MessageBar");
            bar.SetResourceReference(Border.BackgroundProperty, error ? "DangerSoft" : "InfoSoft");
            var text = C<TextBlock>("MessageText");
            text.Text = message;
            text.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "Info");
            System.Windows.Automation.AutomationProperties.SetLiveSetting(text, System.Windows.Automation.AutomationLiveSetting.Polite);
            messageAction = action;
            var button = C<Button>("MessageAction");
            button.Content = actionText; button.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
            bar.Visibility = Visibility.Visible;
            Announce(text);
            // Errors stay until closed; information fades after a while.
            if (messageTimer == null) { messageTimer = new DispatcherTimer(); messageTimer.Tick += (s, e) => { messageTimer.Stop(); HideMessage(); }; }
            messageTimer.Stop();
            if (!error) { messageTimer.Interval = TimeSpan.FromSeconds(action == null ? 7 : 12); messageTimer.Start(); }
        }
        DispatcherTimer messageTimer;
        void HideMessage() { C<Border>("MessageBar").Visibility = Visibility.Collapsed; messageAction = null; if (messageTimer != null) messageTimer.Stop(); }
        public string MessageTextValue { get { var bar = C<Border>("MessageBar"); return bar.Visibility == Visibility.Visible ? C<TextBlock>("MessageText").Text : ""; } }

        // ---- Tabs ----
        void SelectTab(Platform platform) {
            var button = C<RadioButton>("Tab" + platform);
            if (button.IsChecked != true) button.IsChecked = true; else OnTabChecked(platform);
        }
        void OnTabChecked(Platform platform) {
            var previous = currentTab;
            currentTab = platform;
            C<TextBlock>("TabHint").Text = Loc.T("tabhint." + platform);
            settings.LastTab = platform.ToString();
            // Each tab remembers its own profile.
            if (!loading && previous != platform) {
                string profile;
                if (settings.TabProfiles.TryGetValue(platform.ToString(), out profile) && profile != TagOf(C<ComboBox>("ProfileBox"))) { loading = true; SelectTag(C<ComboBox>("ProfileBox"), profile); loading = false; OnProfileChanged(); }
            }
            OnTabShown();
            SaveSettings();
        }
        partial void OnTabShown();
        public Platform CurrentTab { get { return currentTab; } }

        void OnUrlChanged() {
            ResetPreview();
            var text = C<TextBox>("UrlBox").Text;
            var detected = Platforms.Detect(text);
            C<Border>("RedirectNote").Visibility = Visibility.Collapsed;
            if (detected.HasValue && detected.Value != currentTab) {
                // A link pasted into the wrong tab moves to the right tab; the text stays in place.
                SelectTab(detected.Value);
                ShowRedirect(Loc.F("dl.redirect", PlatformName(detected.Value)));
            }
            previewTimer.Stop();
            if (settings.AutoPreview && !string.IsNullOrWhiteSpace(text)) previewTimer.Start();
        }
        void ShowRedirect(string text) { C<TextBlock>("RedirectText").Text = text; C<Border>("RedirectNote").Visibility = Visibility.Visible; }
        static string PlatformName(Platform p) { return p == Platform.Other ? Loc.T("tab.other") : p.ToString(); }

        void OnUrlPaste(object sender, DataObjectPastingEventArgs e) {
            var text = e.DataObject.GetDataPresent(DataFormats.UnicodeText) ? e.DataObject.GetData(DataFormats.UnicodeText) as string : null;
            if (text == null || Platforms.ExtractUrls(text).Count == 0) return;
            var normalized = Platforms.NormalizePasted(text);
            if (normalized == text) return;
            e.CancelCommand();
            var box = C<TextBox>("UrlBox");
            box.SelectedText = normalized;
            box.Select(box.SelectionStart + box.SelectionLength, 0);
        }
        void PasteUrl() {
            try {
                if (Clipboard.ContainsText()) { var box = C<TextBox>("UrlBox"); box.Text = Platforms.NormalizePasted(Clipboard.GetText()); box.Focus(); box.CaretIndex = box.Text.Length; }
                else ShowMessage(Loc.T("msg.clipboard.empty"), false);
            } catch (Exception) { ShowMessage(Loc.T("msg.clipboard.error"), true); }
        }

        // ---- Preview ----
        void ResetPreview() {
            previewGeneration++;
            CancelPreview();
            inspected = null; video = null;
            C<TextBlock>("VideoTitle").Text = Loc.T("preview.empty.title");
            C<TextBlock>("VideoMeta").Text = Loc.T(settings.AutoPreview ? "preview.empty.meta" : "preview.empty.meta.manual");
            C<WrapPanel>("Chips").Children.Clear();
            C<TextBlock>("PreviewNote").Visibility = Visibility.Collapsed;
            var image = C<System.Windows.Controls.Image>("Thumbnail"); image.Source = null; image.Visibility = Visibility.Collapsed;
            BuildQualityItems(null);
            OnPreviewReset();
            UpdateSelectionSummary();
        }
        partial void OnPreviewReset();
        void CancelPreview() {
            if (previewCts != null) { previewCts.Cancel(); previewCts = null; }
            C<ProgressBar>("PreviewProgress").Visibility = Visibility.Collapsed;
        }
        void InspectNow() { previewTimer.Stop(); Inspect(false); }

        // Only the latest request may update the preview; older results are dropped.
        async void Inspect(bool automatic) {
            UrlInfo url;
            // Several links: no single preview; Download adds each as its own job.
            var links = Platforms.ExtractUrls(C<TextBox>("UrlBox").Text);
            if (links.Count >= 2) {
                CancelPreview();
                C<TextBlock>("VideoTitle").Text = Loc.F("dl.multi.title", links.Count);
                C<TextBlock>("VideoMeta").Text = Loc.T("dl.multi.meta");
                return;
            }
            try { url = Platforms.Parse(links.Count == 1 ? links[0] : C<TextBox>("UrlBox").Text); Engine.EnsureTools(); }
            catch (Exception ex) { if (!(automatic && ex is FlowException && ((FlowException)ex).Code == ErrorCode.UrlEmpty)) ShowPreviewError(Loc.Error(ex)); return; }
            if (inspected != null && url.Url == inspected.Url && video != null) return;
            if (url.Kind == UrlKind.Collection) { if (collectionUrl == null || collectionUrl.Url != url.Url) OpenCollection(url); return; }
            CancelPreview();
            var generation = ++previewGeneration;
            var cts = previewCts = new CancellationTokenSource();
            C<ProgressBar>("PreviewProgress").Visibility = Visibility.Visible;
            C<TextBlock>("VideoMeta").Text = Loc.T("preview.loading");
            try {
                var source = InfoSource ?? new Func<UrlInfo, CancellationToken, Task<MediaInfo>>((u, t) => EngineExecutor.Inspect(u.Url, u.Platform, 0, t));
                var info = await source(url, cts.Token);
                if (generation != previewGeneration) return;
                ApplyPreview(url, info, generation);
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                if (generation == previewGeneration) { ShowPreviewError(Loc.Error(ex)); OfferEngineUpdate(Errors.FromException(ex), Loc.Error(ex)); }
            } finally {
                if (generation == previewGeneration) { C<ProgressBar>("PreviewProgress").Visibility = Visibility.Collapsed; if (previewCts == cts) previewCts = null; }
                cts.Dispose();
            }
        }
        partial void OpenCollection(UrlInfo url);
        void ShowPreviewError(string message) {
            C<TextBlock>("VideoTitle").Text = Loc.T("preview.failed");
            C<TextBlock>("VideoMeta").Text = message;
            C<ProgressBar>("PreviewProgress").Visibility = Visibility.Collapsed;
        }
        void ApplyPreview(UrlInfo url, MediaInfo info, int generation) {
            video = info; inspected = url;
            // Short links reveal their real platform only after inspection.
            if (info.Platform != currentTab && info.Platform != url.Platform) { SelectTab(info.Platform); ShowRedirect(Loc.F("dl.redirect.resolved", PlatformName(info.Platform))); }
            C<TextBlock>("VideoTitle").Text = info.Title;
            var meta = new List<string> { new Uri(url.Url).IdnHost };
            if (!string.IsNullOrWhiteSpace(info.Channel)) meta.Add(info.Channel);
            if (info.Duration > 0) meta.Add(Loc.Duration(info.Duration));
            C<TextBlock>("VideoMeta").Text = string.Join("  ·  ", meta);
            BuildChips(info);
            UpdatePreviewNote();
            BuildQualityItems(info);
            OnPreviewApplied(info);
            UpdateSelectionSummary();
            LoadThumbnail(info.Thumbnail, generation);
        }
        partial void OnPreviewApplied(MediaInfo info);
        void UpdatePreviewNote() {
            var note = C<TextBlock>("PreviewNote");
            var text = video != null && video.IsCollection ? Loc.T("preview.first") : "";
            note.Text = text;
            note.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        void BuildChips(MediaInfo info) {
            var chips = C<WrapPanel>("Chips"); chips.Children.Clear();
            var best = info.BestVideo;
            if (best != null) {
                var o = best.Height > best.Width ? "chip.vertical" : best.Width > best.Height ? "chip.horizontal" : "chip.square";
                AddChip(Loc.F(o, best.Width, best.Height));
                if (best.Fps > 0) AddChip(Loc.F("chip.fps", Math.Round(best.Fps)));
            }
            if (info.HasVideo == Tri.No) AddChip(Loc.T("chip.audioOnly"));
            AddChip(Loc.T(info.HasAudio == Tri.Yes ? "chip.audio" : info.HasAudio == Tri.No ? "chip.silent" : "chip.audio.unknown"));
            var subs = info.Subtitles.Where(s => !s.Automatic).Select(s => s.Lang).Distinct().ToList();
            if (subs.Count > 0) AddChip(Loc.F("chip.subs", string.Join(", ", subs.Take(4)) + (subs.Count > 4 ? " +" + (subs.Count - 4) : "")));
            else if (info.Subtitles.Any(s => s.Automatic)) AddChip(Loc.T("chip.autosubs"));
            if (info.Chapters.Count > 0) AddChip(Loc.F("chip.chapters", info.Chapters.Count));
        }
        void AddChip(string text) {
            var border = new Border { Child = new TextBlock { Text = text, FontSize = 12 } };
            border.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
            C<WrapPanel>("Chips").Children.Add(border);
        }
        async void LoadThumbnail(string url, int generation) {
            Uri uri;
            if (!Platforms.TryPublicWebUrl(url, out uri) || uri.Scheme != "https") return;
            try {
                var bytes = await Thumbnails.Fetch(uri);
                if (bytes == null || generation != previewGeneration || Window.Dispatcher.HasShutdownStarted) return;
                var image = C<System.Windows.Controls.Image>("Thumbnail");
                image.Source = Thumbnails.Decode(bytes, 320); image.Visibility = Visibility.Visible;
            } catch (Exception) { /* Thumbnails are optional; a failure never blocks the download. */ }
        }

        // ---- Options ----
        void ApplySettingsToOptions() {
            loading = true;
            SelectTag(C<ComboBox>("ProfileBox"), settings.Profile);
            SelectTag(C<ComboBox>("FormatBox"), settings.Format);
            SelectTag(C<ComboBox>("QualityBox"), settings.Height.ToString());
            SelectTag(C<ComboBox>("CodecBox"), settings.Codec);
            SelectTag(C<ComboBox>("FpsBox"), settings.FpsLimit.ToString());
            SelectTag(C<ComboBox>("DuplicateBox"), settings.Duplicate);
            C<CheckBox>("StrictBox").IsChecked = settings.StrictQuality;
            loading = false;
            UpdateOptionStates();
        }
        void OnProfileChanged() {
            var profile = TagOf(C<ComboBox>("ProfileBox"));
            loading = true;
            if (profile == "compatible") { SelectTag(C<ComboBox>("FormatBox"), "mp4"); SelectTag(C<ComboBox>("CodecBox"), "h264"); if (SelectedHeight() == 0 || (SelectedHeight() <= 480 && settings.Profile == "small")) SelectClosestQuality(1080); }
            else if (profile == "best") { SelectTag(C<ComboBox>("FormatBox"), "mkv"); SelectTag(C<ComboBox>("CodecBox"), "auto"); SelectTag(C<ComboBox>("QualityBox"), "0"); SelectTag(C<ComboBox>("FpsBox"), "0"); }
            else if (profile == "small") { SelectTag(C<ComboBox>("FormatBox"), "mp4"); SelectTag(C<ComboBox>("CodecBox"), "h264"); SelectClosestQuality(480); SelectTag(C<ComboBox>("FpsBox"), "30"); }
            else if (profile == "mp3" || profile == "m4a") SelectTag(C<ComboBox>("FormatBox"), profile);
            loading = false;
            settings.TabProfiles[currentTab.ToString()] = profile;
            StoreOptions();
        }
        void OnOptionChanged() {
            // Any manual change turns the preset into "custom", except picking a quality for a video preset.
            var profile = TagOf(C<ComboBox>("ProfileBox"));
            var format = TagOf(C<ComboBox>("FormatBox"));
            bool matches = (profile == "compatible" && format == "mp4" && TagOf(C<ComboBox>("CodecBox")) == "h264")
                || (profile == "best" && format == "mkv" && TagOf(C<ComboBox>("CodecBox")) == "auto")
                || (profile == "small" && format == "mp4" && SelectedHeight() > 0 && SelectedHeight() <= 480)
                || ((profile == "mp3" || profile == "m4a") && format == profile);
            if (!matches) { loading = true; SelectTag(C<ComboBox>("ProfileBox"), format == "mp3" || format == "m4a" ? format : "custom"); loading = false; settings.TabProfiles[currentTab.ToString()] = TagOf(C<ComboBox>("ProfileBox")); }
            StoreOptions();
        }
        void StoreOptions() {
            settings.Profile = TagOf(C<ComboBox>("ProfileBox")) ?? "compatible";
            settings.Format = TagOf(C<ComboBox>("FormatBox")) ?? "mp4";
            var height = SelectedHeight(); if (SettingsRules.Heights.Contains(height)) settings.Height = height;
            settings.Codec = TagOf(C<ComboBox>("CodecBox")) ?? "auto";
            int fps; settings.FpsLimit = int.TryParse(TagOf(C<ComboBox>("FpsBox")), out fps) ? fps : 0;
            settings.StrictQuality = C<CheckBox>("StrictBox").IsChecked == true;
            UpdateOptionStates();
            SaveSettings();
        }
        int SelectedHeight() { int h; return int.TryParse(TagOf(C<ComboBox>("QualityBox")), out h) ? h : settings.Height; }
        bool AudioSelected { get { var f = TagOf(C<ComboBox>("FormatBox")); return f == "mp3" || f == "m4a"; } }
        void UpdateOptionStates() {
            bool audio = AudioSelected;
            foreach (var name in new[] { "QualityBox", "CodecBox", "FpsBox" }) C<ComboBox>(name).IsEnabled = !audio;
            C<CheckBox>("StrictBox").IsEnabled = !audio;
            OnOptionStates(audio);
            UpdateSelectionSummary();
        }
        partial void OnOptionStates(bool audio);
        void SelectClosestQuality(int cap) {
            var box = C<ComboBox>("QualityBox");
            var items = box.Items.OfType<ComboBoxItem>().Select(i => new { Item = i, H = int.Parse(Convert.ToString(i.Tag)) }).ToList();
            var fit = items.Where(i => i.H > 0 && i.H <= cap).OrderByDescending(i => i.H).FirstOrDefault() ?? items.Where(i => i.H > 0).OrderBy(i => i.H).FirstOrDefault();
            if (fit != null) box.SelectedItem = fit.Item;
        }

        // Generic limits before inspection; the video's real qualities (short side, size, fps) afterwards.
        void BuildQualityItems(MediaInfo info) {
            var box = C<ComboBox>("QualityBox");
            int wanted = box.SelectedItem != null ? SelectedHeight() : settings.Height;
            bool was = loading; loading = true;
            box.Items.Clear();
            box.Items.Add(new ComboBoxItem { Tag = "0", Content = Loc.T("quality.best") });
            var real = info == null ? new List<FormatInfo>() : info.Usable.Where(f => f.HasVideo && f.ShortSide > 0).GroupBy(f => f.ShortSide).Select(g => g.OrderByDescending(f => f.Fps).ThenByDescending(f => f.Tbr).First()).OrderByDescending(f => f.ShortSide).ToList();
            if (real.Count > 0) {
                foreach (var f in real) box.Items.Add(new ComboBoxItem { Tag = f.ShortSide.ToString(), Content = f.ShortSide + "p  ·  " + f.Width + "×" + f.Height + (f.Fps > 0 ? "  ·  " + Math.Round(f.Fps) + " fps" : "") });
            } else {
                foreach (var h in GenericHeights.Where(h => h > 0)) box.Items.Add(new ComboBoxItem { Tag = h.ToString(), Content = h + "p" + (h == 2160 ? "  ·  4K" : h == 1440 ? "  ·  2K" : h == 1080 ? "  ·  Full HD" : h == 720 ? "  ·  HD" : "") });
            }
            var items = box.Items.OfType<ComboBoxItem>().ToList();
            var exact = items.FirstOrDefault(i => Convert.ToString(i.Tag) == wanted.ToString());
            if (exact != null) box.SelectedItem = exact;
            else if (wanted == 0) box.SelectedIndex = 0;
            else box.SelectedItem = items.Skip(1).FirstOrDefault(i => int.Parse(Convert.ToString(i.Tag)) <= wanted) ?? items.Last();
            loading = was;
        }

        void RefreshDownloadTexts() {
            C<TextBlock>("TabHint").Text = Loc.T("tabhint." + currentTab);
            if (video == null) { C<TextBlock>("VideoTitle").Text = Loc.T("preview.empty.title"); C<TextBlock>("VideoMeta").Text = Loc.T(settings.AutoPreview ? "preview.empty.meta" : "preview.empty.meta.manual"); }
            else { BuildChips(video); UpdatePreviewNote(); }
            BuildQualityItems(video);
            C<Border>("RedirectNote").Visibility = Visibility.Collapsed;
            UpdateSelectionSummary();
        }

        // Current choices as a job option snapshot.
        JobOptions CurrentOptions() {
            var o = JobOptions.From(settings);
            o.Folder = C<TextBox>("FolderBox").Text;
            o.Height = Math.Max(0, SelectedHeight());
            AddExtraOptions(o);
            return o;
        }
        partial void AddExtraOptions(JobOptions o);

        void UpdateSelectionSummary() {
            var summary = C<TextBlock>("SelectionSummary");
            var warning = C<TextBlock>("SelectionWarning");
            if (summary == null || loading) return;
            var profile = TagOf(C<ComboBox>("ProfileBox")) ?? "compatible";
            var notes = new List<string>();
            string preview = null;
            if (video == null) {
                summary.Text = Loc.T("profile." + profile + ".desc");
                if (AudioSelected) notes.Add(Loc.T("opt.audio.note"));
            } else {
                var options = CurrentOptions();
                try {
                    var s = FormatSelector.Select(video, options);
                    summary.Text = Describe(s);
                    notes.AddRange(s.Notes.Select(Loc.T));
                    var job = new DownloadJob { Title = video.Title, ContentId = video.Id, Channel = video.Channel, Platform = video.Platform.ToString() };
                    preview = Loc.F("opt.name", OutputPlanner.Name(options, job, s, DateTime.Now) + "." + OutputPlanner.Extension(options));
                } catch (FlowException ex) {
                    summary.Text = Loc.T("profile." + profile + ".desc");
                    notes.Add(Loc.Error(ex));
                }
            }
            warning.Text = string.Join(" ", notes);
            warning.Visibility = notes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            C<TextBlock>("NamePreview").Text = preview ?? "";
        }
        public string SelectionText { get { return C<TextBlock>("SelectionSummary").Text + " | " + C<TextBlock>("SelectionWarning").Text; } }

        static string Describe(Selection s) {
            var parts = new List<string>();
            string size = s.Bytes < 0 ? Loc.T("sel.size.unknown") : Loc.F(s.SizeExact ? "sel.size.exact" : "sel.size.approx", Loc.Size(s.Bytes));
            if (s.AudioOnly) return Loc.F("sel.audio", CodecName(s.ACodec) ?? "?", size);
            if (s.Width > 0 && s.Height > 0) parts.Add(s.Width + "×" + s.Height);
            if (s.Fps > 0) parts.Add(Math.Round(s.Fps) + " fps");
            var codecs = CodecName(s.VCodec) ?? "?";
            if (s.ACodec != null && s.ACodec != "none") codecs += " + " + (CodecName(s.ACodec) ?? "?");
            parts.Add(codecs);
            parts.Add(size);
            return string.Join("  ·  ", parts);
        }
        public static string CodecName(string codec) {
            if (string.IsNullOrEmpty(codec) || codec == "none") return null;
            var c = codec.ToLowerInvariant();
            if (c.StartsWith("avc") || c == "h264") return "H.264";
            if (c.StartsWith("hev") || c.StartsWith("hvc") || c == "h265" || c == "hevc") return "H.265";
            if (c.StartsWith("av01") || c == "av1") return "AV1";
            if (c.StartsWith("vp09") || c == "vp9") return "VP9";
            if (c.StartsWith("mp4a") || c == "aac") return "AAC";
            if (c == "opus") return "Opus";
            if (c == "mp3") return "MP3";
            return codec.Split('.')[0].ToUpperInvariant();
        }

        // ---- Adding to the queue ----
        void StartDownload() { Enqueue(false); }
        internal JobItem Enqueue(bool hold) {
            if (CollectionOpen) return EnqueueCollection(hold);
            var links = Platforms.ExtractUrls(C<TextBox>("UrlBox").Text);
            if (links.Count >= 2) return EnqueueMany(links, hold);
            UrlInfo url;
            JobOptions options;
            try {
                url = Platforms.Parse(links.Count == 1 ? links[0] : C<TextBox>("UrlBox").Text);
                if (url.Kind == UrlKind.Collection) { OpenCollection(url); return null; }
                options = CurrentOptions();
                if (string.IsNullOrWhiteSpace(options.Folder) || !Path.IsPathRooted(options.Folder)) throw new FlowException(ErrorCode.FolderInvalid);
                ValidateExtras(options);
                // With a fresh preview the choice is checked now, not after queueing.
                if (video != null && inspected != null && inspected.Url == url.Url) FormatSelector.Select(video, options);
            } catch (Exception ex) { ShowMessage(Loc.Error(ex), true); return null; }
            var media = video != null && inspected != null && inspected.Url == url.Url ? video : null;
            var job = new DownloadJob {
                Url = url.Url, Platform = (media != null ? media.Platform : url.Platform).ToString(), Options = options,
                Title = media != null ? media.Title : null, Channel = media != null ? media.Channel : null, ContentId = media != null ? media.Id : url.VideoId,
                Duration = media != null ? media.Duration : 0, Thumbnail = media != null ? media.Thumbnail : null,
                ItemIndex = media != null && media.IsCollection ? media.ItemIndex : 0
            };
            ApplySchedule(job);
            var item = queue.Add(job, hold, media);
            ShowMessage(Loc.F(hold ? "msg.added.held" : "msg.added", item.Title), false, Loc.T("dl.showQueue"), () => ShowPage("queue"));
            C<TextBox>("UrlBox").Text = "";
            C<TextBox>("UrlBox").Focus();
            return item;
        }
        // Each link becomes its own job with the chosen profile, quality and folder. A time range or subtitle choice
        // belongs to one video, so it is not applied; lists open their own selection screen and are skipped here.
        internal JobItem EnqueueMany(List<string> links, bool hold) {
            JobOptions template;
            try {
                template = CurrentOptions();
                if (string.IsNullOrWhiteSpace(template.Folder) || !Path.IsPathRooted(template.Folder)) throw new FlowException(ErrorCode.FolderInvalid);
            } catch (Exception ex) { ShowMessage(Loc.Error(ex), true); return null; }
            int skipped = 0;
            JobItem last = null; var added = 0;
            foreach (var link in links) {
                UrlInfo url;
                try { url = Platforms.Parse(link); } catch (FlowException) { skipped++; continue; }
                if (url.Kind == UrlKind.Collection) { skipped++; continue; }
                var options = template.Copy();
                options.ClipStart = 0; options.ClipEnd = 0; options.SubLangs = new List<string>();
                var job = new DownloadJob { Url = url.Url, Platform = url.Platform.ToString(), Options = options, ContentId = url.VideoId };
                ApplySchedule(job);
                last = queue.Add(job, hold); added++;
            }
            var text = skipped == 0 ? Loc.F("msg.added.many", added) : Loc.F("msg.added.manySkipped", added, skipped);
            ShowMessage(text, added == 0, added > 0 ? Loc.T("dl.showQueue") : null, added > 0 ? new Action(() => ShowPage("queue")) : null);
            if (added > 0) { C<TextBox>("UrlBox").Text = ""; C<TextBox>("UrlBox").Focus(); }
            return last;
        }
        partial void ValidateExtras(JobOptions options);
        partial void ApplySchedule(DownloadJob job);
    }
}
