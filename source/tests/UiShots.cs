using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flow;

namespace FlowTests {
    // Design shots for the UI rewrite: the same views at two window sizes, two DPI scales and both
    // themes, rendered independently of the monitor's DPI. Output: %FLOW_TEST_OUT%\screens\redesign.
    public static partial class UiTests {
        static readonly int[][] ShotSizes = { new[] { 1180, 840 }, new[] { 760, 560 } };
        static readonly string[] ShotThemes = { "light", "dark" };

        public static string RenderScaled(Window window, string name, double scale) {
            Pump();
            // The window's template root: client area including the content's own margins (dialogs have them).
            var content = VisualTreeHelper.GetChildrenCount(window) > 0 ? (FrameworkElement)VisualTreeHelper.GetChild(window, 0) : (FrameworkElement)window.Content;
            return RenderElement(content, window.Background ?? Brushes.White, name, scale);
        }

        public static string RenderElement(FrameworkElement content, Brush background, string name, double scale) {
            double width = content.ActualWidth, height = content.ActualHeight;
            var bitmap = new RenderTargetBitmap((int)Math.Round(width * scale), (int)Math.Round(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) {
                dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
            }
            bitmap.Render(visual);
            var dir = Path.Combine(Environment.GetEnvironmentVariable("FLOW_TEST_OUT") ?? Path.GetTempPath(), "screens", "redesign");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
            return path;
        }

        // Visible button texts that do not fit their button (measured against the text's own width).
        public static List<string> ClippedButtons(DependencyObject root) {
            var clipped = new List<string>();
            foreach (var button in Visuals(root).OfType<System.Windows.Controls.Primitives.ButtonBase>().Where(b => b.IsVisible)) {
                foreach (var text in Visuals(button).OfType<TextBlock>().Where(t => t.IsVisible && !string.IsNullOrEmpty(t.Text))) {
                    // Measure the way the window draws text (Display mode at the window's DPI); allow one pixel of glyph rounding.
                    var formatted = new FormattedText(text.Text, Loc.Culture, text.FlowDirection, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black, null, TextOptions.GetTextFormattingMode(text), VisualTreeHelper.GetDpi(text).PixelsPerDip);
                    bool cut;
                    if (text.TextWrapping == TextWrapping.NoWrap) cut = formatted.Width > text.ActualWidth + 1.0;
                    else { formatted.MaxTextWidth = Math.Max(1, text.ActualWidth); cut = formatted.Height > text.ActualHeight + 1.0; }  // wrapped text is cut only when lines are lost
                    if (cut) clipped.Add((string.IsNullOrEmpty(button.Name) ? "" : button.Name + ":") + text.Text);
                }
            }
            return clipped;
        }

        // The element is shown, inside the window, and inside the viewport of the nearest scroll viewer.
        public static bool InView(Window window, string name) {
            var element = Find<FrameworkElement>(window, name);
            var content = (FrameworkElement)window.Content;
            if (element == null || !element.IsVisible) return false;
            var inWindow = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
            if (inWindow.Left < -0.5 || inWindow.Top < -0.5 || inWindow.Right > content.ActualWidth + 0.5 || inWindow.Bottom > content.ActualHeight + 0.5) return false;
            for (var parent = VisualTreeHelper.GetParent(element); parent != null; parent = VisualTreeHelper.GetParent(parent)) {
                var viewer = parent as ScrollViewer;
                if (viewer == null) continue;
                var inViewer = element.TransformToAncestor(viewer).TransformBounds(new Rect(element.RenderSize));
                return inViewer.Top >= -0.5 && inViewer.Bottom <= viewer.ViewportHeight + 0.5 && inViewer.Left >= -0.5 && inViewer.Right <= viewer.ViewportWidth + 0.5;
            }
            return true;
        }

        static void RedesignChecks() {
            ShellChecks();
            OpenerChecks();
            TypeChecks();
            RailFieldChecks();
            DownloadChecks();
            QueueChecks();
            HistoryChecks();
            SettingsChecks();
            CloseChecks();
            MotionChecks();
        }

        static void MotionChecks() {
            var c = OpenWith("ui-redesign-motion", "light", 1180, 840);
            var w = c.Window;
            Motion.Enabled = true;
            c.ShowPage("queue");
            T.Check(Find<ScrollViewer>(w, "QueuePage").HasAnimatedProperties, "sayfa geçişi kısa bir saydamlık geçişiyle");
            Motion.Enabled = false;
            c.ShowPage("history");
            T.Check(!Find<ScrollViewer>(w, "HistoryPage").HasAnimatedProperties && Find<ScrollViewer>(w, "HistoryPage").Opacity == 1, "Windows animasyonları kapalıyken sayfa geçişi anında");
            // Hover and press never run in these tests, so start every template storyboard once: a wrong
            // target or property path would otherwise surface only on the user's first hover.
            c.ShowPage("download"); Pump();
            var broken = new List<string>(); int started = 0;
            var seen = new HashSet<ControlTemplate>();
            foreach (var control in Visuals(w).OfType<Control>().Where(x => x.Template != null && x.IsVisible)) {
                if (!seen.Add(control.Template)) continue;
                foreach (var trigger in control.Template.Triggers) {
                    var actions = new List<System.Windows.TriggerAction>();
                    var plain = trigger as Trigger; var multi = trigger as MultiTrigger;
                    if (plain != null) { actions.AddRange(plain.EnterActions); actions.AddRange(plain.ExitActions); }
                    if (multi != null) { actions.AddRange(multi.EnterActions); actions.AddRange(multi.ExitActions); }
                    foreach (var begin in actions.OfType<System.Windows.Media.Animation.BeginStoryboard>()) {
                        try { begin.Storyboard.Begin(control, control.Template, true); begin.Storyboard.Stop(control); started++; }
                        catch (Exception ex) { broken.Add(control.GetType().Name + ": " + ex.Message); }
                    }
                }
            }
            T.Check(started >= 8 && broken.Count == 0, "şablon animasyonları hedeflerini buluyor (" + started + ")" + (broken.Count == 0 ? "" : ": " + string.Join(" | ", broken)));
            w.Close();
        }

        static void CloseChecks() {
            var c = OpenWith("ui-redesign-close", "light", 1180, 840);
            var w = c.Window;
            foreach (var language in new[] { "tr", "en" }) {
                Loc.Set(language); Pump();
                var dialog = new CloseDialog(w, 1);
                var window = dialog.Window;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000; window.Top = -32000; window.ShowActivated = false;
                window.Show(); Pump();
                var clipped = ClippedButtons(window);
                T.Check(clipped.Count == 0, "kapatma penceresi düğmeleri kırpılmıyor (" + language + ")" + (clipped.Count == 0 ? "" : ": " + string.Join(", ", clipped)));
                var tray = Find<Button>(window, "TrayButton");
                var plate = tray.Background as SolidColorBrush; var accent = w.Resources["Accent"] as SolidColorBrush;
                T.Check(plate != null && accent != null && plate.Color == accent.Color, "kapatma: tepsiye küçültme seçeneği pencerenin tek ters plakası (" + language + ")");
                window.Close(); Pump();
            }
            Loc.Set("tr"); Pump();
            c.ShowMessage("Örnek bildirim", false); Pump();
            var bar = Find<Border>(w, "MessageBar");
            var content = (FrameworkElement)w.Content;
            double barLeft = bar.TransformToAncestor(content).Transform(new Point(0, 0)).X, urlLeft = Find<TextBox>(w, "UrlBox").TransformToAncestor(content).Transform(new Point(0, 0)).X;
            T.Check(Math.Abs(barLeft - urlLeft) < 1, "bildirim çubuğu içerik sütununa hizalı (" + barLeft.ToString("0") + " / " + urlLeft.ToString("0") + ")");
            w.Close();
        }

        static void SettingsChecks() {
            var c = OpenWith("ui-redesign-settings", "light", 1180, 840);
            var w = c.Window;
            c.ShowPage("settings"); Pump();
            var page = Find<ScrollViewer>(w, "SettingsPage");
            var content = (FrameworkElement)page.Content;
            Func<string, double> left = name => Find<FrameworkElement>(w, name).TransformToAncestor(content).Transform(new Point(0, 0)).X;
            T.Check(left("LanguageBox") >= 180 && Math.Abs(left("LanguageBox") - left("DefaultFolderBox")) < 1 && Math.Abs(left("LanguageBox") - left("ParallelBox")) < 1,
                "Ayarlar: bölüm başlıkları sol kenar sütununda, denetimler tek bir dikey çizgide (" + left("LanguageBox").ToString("0") + ")");
            PageFits(c, "SettingsPage", "Ayarlar");
            w.Close();
        }

        static void HistoryChecks() {
            var paths = NewPaths("ui-redesign-history");
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = "light"; s.Notify = false;
            Store.SaveSettings(paths, s);
            Store.SaveHistory(paths, ShotHistory(paths));
            var c = Open(paths, 1180, 840, new FakeExecutor());
            var w = c.Window;
            c.ShowPage("history"); Pump();
            var list = Find<ItemsControl>(w, "HistoryList");
            var youtube = c.HistoryRows.First(r => r.Entry.Platform == "YouTube");
            var day = TemplatePart<TextBlock>(list, youtube, "Figure");
            var expected = Iso.Parse(youtube.Entry.CompletedAt).Value.ToLocalTime().ToString("dd.MM", Loc.Culture);
            T.Check(day != null && day.Text == expected, "Geçmiş: rakam sütununda tamamlanma günü (" + (day == null ? "yok" : day.Text) + ")");
            var key = TemplatePart<Border>(list, youtube, "SourceKey");
            T.Check(key != null && key.Background.ToString() == w.Resources["SourceYouTube"].ToString(), "Geçmiş: kaynak karesi kaydın kaynağının rengini alıyor");
            PageFits(c, "HistoryPage", "Geçmiş");
            w.Close();
        }

        static T TemplatePart<T>(ItemsControl list, object item, string name) where T : class {
            var presenter = list.ItemContainerGenerator.ContainerFromItem(item) as ContentPresenter;
            return presenter == null ? null : presenter.ContentTemplate.FindName(name, presenter) as T;
        }

        static void PageFits(Controller c, string pageName, string label) {
            var w = c.Window;
            Resize(w, 760, 560);
            var page = Find<ScrollViewer>(w, pageName);
            foreach (var language in new[] { "tr", "en" }) {
                Loc.Set(language); Pump();
                var clipped = ClippedButtons(page);
                T.Check(clipped.Count == 0 && page.ComputedHorizontalScrollBarVisibility != Visibility.Visible && ((FrameworkElement)page.Content).ActualWidth <= page.ViewportWidth + 1,
                    label + " 760×560'ta kırpılmıyor ve taşmıyor (" + language + ")" + (clipped.Count == 0 ? "" : ": " + string.Join(", ", clipped)));
            }
            Loc.Set("tr"); Pump();
        }

        static void QueueChecks() {
            var c = OpenWith("ui-redesign-queue", "light", 1180, 840);
            var w = c.Window;
            LoadPreview(c, null);
            ShotQueue(c);
            c.ShowPage("queue"); Pump();
            var list = Find<ItemsControl>(w, "QueueList");
            var states = new[] { JobState.Completed, JobState.Downloading, JobState.Paused, JobState.Failed };
            var colours = states.Select(s => {
                var item = c.Queue.Items.FirstOrDefault(i => i.Job.State == s);
                var label = item == null ? null : TemplatePart<TextBlock>(list, item, "StateLabel");
                return label == null ? "?" + s : label.Foreground.ToString();
            }).ToList();
            T.Check(colours.Distinct().Count() == 4 && !colours.Any(x => x.StartsWith("?")), "H3: tamamlandı, iniyor, duraklatıldı ve başarısız ayrı renkte (" + string.Join(" ", colours) + ")");
            var active = c.Queue.Items.First(i => i.Job.State == JobState.Downloading);
            var figure = TemplatePart<TextBlock>(list, active, "Figure");
            T.Check(figure != null && figure.IsVisible && figure.Text == active.PercentText, "asılı rakam: etkin işin yüzdesi sol sütunda (" + (figure == null ? "yok" : figure.Text) + ")");
            var youtube = c.Queue.Items.First(i => i.Job.Platform == "YouTube");
            var key = TemplatePart<Border>(list, youtube, "SourceKey");
            T.Check(key != null && key.Background.ToString() == w.Resources["SourceYouTube"].ToString(), "kaynak karesi işin kaynağının rengini alıyor (YouTube)");
            var paused = c.Queue.Items.First(i => i.Job.State == JobState.Paused);
            var mark = TemplatePart<TextBlock>(list, paused, "Mark");
            T.Check(mark != null && mark.Text == "", "duraklatılan iş rakam sütununda duraklatma işaretini taşıyor");
            PageFits(c, "QueuePage", "Kuyruk");
            w.Close();
        }

        static void LoadPreview(Controller c, string title) {
            c.InfoSource = (u, token) => {
                var info = MediaParser.Parse(T.Fixture("youtube.json"), Platform.YouTube);
                if (title != null) info.Title = title;
                return Task.FromResult(info);
            };
            var w = c.Window;
            Find<TextBox>(w, "UrlBox").Text = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
            Wait(() => Find<TextBlock>(w, "VideoTitle").Text != Loc.T("preview.empty.title") && !Find<TextBlock>(w, "VideoTitle").Text.StartsWith(Loc.T("preview.loading")) && Find<WrapPanel>(w, "Chips").Children.Count > 0, 4000);
            Find<ScrollViewer>(w, "DownloadPage").ScrollToTop(); Pump();
        }

        static void DownloadChecks() {
            var c = OpenWith("ui-redesign-download", "light", 1180, 840);
            var w = c.Window;
            LoadPreview(c, null);
            foreach (var n in new[] { "UrlBox", "ProfileBox", "FormatBox", "QualityBox", "FolderBox", "StartButton", "AddHoldButton" })
                T.Check(InView(w, n), "H1: " + n + " 1180×840'ta kaydırmadan görünür");
            var details = Find<Expander>(w, "FormatExpander");
            bool hiddenFirst = !Find<ComboBox>(w, "CodecBox").IsVisible;
            var header = details.Template.FindName("HeaderButton", details) as System.Windows.Controls.Primitives.ToggleButton;
            header.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (header.IsChecked != true) header.IsChecked = true;
            Pump();
            T.Check(hiddenFirst && Find<ComboBox>(w, "CodecBox").IsVisible && Find<ComboBox>(w, "FpsBox").IsVisible && Find<CheckBox>(w, "StrictBox").IsVisible, "H2: biçim ayrıntıları (kodek, kare hızı, aşma) bir tıkla açılır");
            details.IsExpanded = false; Pump();
            Resize(w, 760, 560);
            T.Check(InView(w, "StartButton") && InView(w, "AddHoldButton"), "H1: İndir ve Sonraya ekle 760×560'ta kaydırmadan görünür");
            foreach (var language in new[] { "tr", "en" }) {
                Loc.Set(language); Pump();
                var clipped = ClippedButtons(w);
                T.Check(clipped.Count == 0, "İndir sayfası düğmeleri kırpılmıyor (" + language + ", 760)" + (clipped.Count == 0 ? "" : ": " + string.Join(", ", clipped)));
            }
            Loc.Set("tr"); Pump();
            w.Close();

            // A very long title and a deep save folder at the smallest window.
            var paths = NewPaths("ui-redesign-long");
            var deep = Path.Combine(paths.DefaultDownloads, "Arşiv klasörü çok uzun bir adla", "Yıllara göre ayrılmış kayıtlar 2019-2026", "Belgeseller ve uzun söyleşiler", "Seçilmiş bölümler");
            Directory.CreateDirectory(deep);
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = "light"; s.Notify = false; s.Folder = deep;
            Store.SaveSettings(paths, s);
            var lc = Open(paths, 760, 560, new FakeExecutor());
            LoadPreview(lc, "Kapadokya üzerinde gün doğumu: balonlar, vadiler ve peri bacaları arasında sabah yürüyüşü — dört saatlik kesintisiz kayıt, yönetmenin anlatımıyla birlikte (4K HDR, 2026 yeniden düzenlenmiş baskı)");
            var page = Find<ScrollViewer>(lc.Window, "DownloadPage");
            T.Check(page.ComputedHorizontalScrollBarVisibility != Visibility.Visible && ((FrameworkElement)page.Content).ActualWidth <= page.ViewportWidth + 1 && InView(lc.Window, "StartButton"), "uzun başlık ve derin kayıt yolu 760×560'ta taşmıyor");
            lc.Window.Close();
        }

        static Controller OpenWith(string name, string theme, double width, double height) {
            var paths = NewPaths(name);
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = theme; s.Parallel = 1; s.Notify = false;
            Store.SaveSettings(paths, s);
            return Open(paths, width, height, new FakeExecutor());
        }

        static void Resize(Window window, double width, double height) { window.Width = width; window.Height = height; Pump(); window.UpdateLayout(); Pump(); }

        // The rail's ASCII field: behind the navigation, never taking input, quiet where the labels are,
        // rising from the foot and thinning upward, drifting by itself, deaf to the pointer, still with animations off.
        static void RailFieldChecks() {
            foreach (var size in ShotSizes) {
                var c = OpenWith("ui-redesign-rail-" + size[0], "light", size[0], size[1]);
                var w = c.Window;
                var field = c.RailField;
                var rail = Find<Border>(w, "Sidebar");
                T.Check(field != null && !field.IsHitTestVisible && Math.Abs(field.ActualHeight - rail.ActualHeight) < 1 && Math.Abs(field.ActualWidth - rail.ActualWidth) < 1, "ray efekti rayın arkasını kaplıyor ve girdi almıyor (" + size[0] + ")");
                var quiet = new[] { "NavDownload", "NavQueue", "NavHistory", "NavSettings" }.All(n => {
                    var nav = Find<FrameworkElement>(w, n);
                    var center = nav.TransformToVisual(field).Transform(new Point(nav.ActualWidth / 2, nav.ActualHeight / 2));
                    return field.AlphaAt(center) <= 0.02;
                });
                T.Check(quiet, "ray efekti gezinme yazılarının arkasında boş (" + size[0] + ")");
                // Rises from the bottom and thins out as it climbs: each band up is fainter than the one below.
                Func<double, double, double> band = (from, to) => {
                    double sum = 0; int n = 0;
                    double span = field.ActualHeight - field.FieldTop;
                    for (double y = field.ActualHeight - span * to; y < field.ActualHeight - span * from; y += 7)
                        for (double x = 2; x < field.ActualWidth; x += 4.2) { sum += field.AlphaAt(new Point(x, y)); n++; }
                    return n == 0 ? 0 : sum / n;
                };
                double foot = band(0.12, 0.35), middle = band(0.35, 0.58), upper = band(0.58, 0.8);   // the foot starts above the version line
                T.Check(foot > middle && middle > upper && upper > 0.002, "ray efekti aşağıdan yukarı giderek saydamlaşıyor (" + foot.ToString("0.000") + " > " + middle.ToString("0.000") + " > " + upper.ToString("0.000") + ")");
                double peak = 0;
                for (double y = 0; y < field.ActualHeight; y += 7) for (double x = 2; x < field.ActualWidth; x += 4.2) peak = Math.Max(peak, field.AlphaAt(new Point(x, y)));
                T.Check(peak > 0.1 && peak <= 0.35, "ray efekti hafif kalıyor (en koyu " + peak.ToString("0.00") + ")");
                T.Check(!field.IsAnimating, "Windows animasyonları kapalıyken ray efekti durgun (" + size[0] + ")");
                if (size[0] == 1180) {
                    // The pointer no longer feeds the field.
                    var before = band(0, 1);
                    var raise = new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseMoveEvent };
                    rail.RaiseEvent(raise);
                    T.Check(Math.Abs(band(0, 1) - before) < 1e-9 && !field.IsAnimating, "ray efekti fareye tepki vermiyor");
                    // Runs by itself once animations are on, and moves gently: no cell jumps between two frames.
                    Motion.Enabled = true;
                    field.Start();
                    T.Check(field.IsAnimating, "ray efekti kendiliğinden çalışıyor");
                    field.Advance(2);
                    var cells = new List<Point>();
                    for (double y = field.ActualHeight * 0.5; y < field.ActualHeight; y += 14) for (double x = 4; x < field.ActualWidth; x += 8.4) cells.Add(new Point(x, y));
                    var then = cells.Select(p => field.AlphaAt(p)).ToArray();
                    double jump = 0, moved = 0;
                    for (int f = 0; f < 30; f++) {
                        field.Advance(1 / 30.0);
                        var now = cells.Select(p => field.AlphaAt(p)).ToArray();
                        for (int i = 0; i < now.Length; i++) { jump = Math.Max(jump, Math.Abs(now[i] - then[i])); }
                        then = now;
                    }
                    var start = cells.Select(p => field.AlphaAt(p)).ToArray();
                    field.Advance(1.5);
                    for (int i = 0; i < cells.Count; i++) moved += Math.Abs(field.AlphaAt(cells[i]) - start[i]);
                    T.Check(jump < 0.07, "ray efekti kareler arasında yumuşak değişiyor (en büyük sıçrama " + jump.ToString("0.000") + ")");
                    T.Check(moved / cells.Count > 0.004, "ray efekti yavaşça yukarı akıyor (" + (moved / cells.Count).ToString("0.000") + ")");
                    var still = new[] { "NavDownload", "NavQueue", "NavHistory", "NavSettings" }.All(n => {
                        var nav = Find<FrameworkElement>(w, n);
                        var center = nav.TransformToVisual(field).Transform(new Point(nav.ActualWidth / 2, nav.ActualHeight / 2));
                        return field.AlphaAt(center) <= 0.02;
                    });
                    T.Check(still, "akarken de gezinme yazılarının arkası boş");
                    Motion.Enabled = false;
                    field.Start();
                    T.Check(!field.IsAnimating, "animasyonlar kapanınca ray efekti duruyor");
                }
                w.Close();
            }
        }

        // Typography (the user went back from Sentient): Bahnschrift for headings, buttons, tabs, the rail
        // and figures; Segoe UI for text and inputs. No Sentient anywhere.
        static void TypeChecks() {
            var c = OpenWith("ui-redesign-type", "light", 1180, 840);
            var w = c.Window;
            LoadPreview(c, null);
            var title = Find<TextBlock>(w, "HeaderEyebrow");
            T.Check(title.FontFamily.Source.Contains("Bahnschrift") && title.FontWeight == FontWeights.SemiBold && Find<TextBlock>(w, "VideoTitle").FontFamily.Source.Contains("Bahnschrift"),
                "başlıklar Bahnschrift SemiBold (" + title.FontFamily.Source + ")");
            T.Check(Find<TextBox>(w, "UrlBox").FontFamily.Source.Contains("Segoe UI") && w.FontFamily.Source.Contains("Segoe UI"), "metin ve giriş alanları Segoe UI");
            var sentient = new List<string>();
            foreach (var page in new[] { "download", "queue", "history", "settings" }) {
                c.ShowPage(page); Pump();
                sentient.AddRange(Visuals(w).OfType<TextBlock>().Where(t => t.IsVisible && t.FontFamily.Source.Contains("Sentient")).Select(t => page + ":" + t.Text));
            }
            T.Check(sentient.Count == 0, "Sentient hiçbir yerde kalmadı" + (sentient.Count == 0 ? "" : ": " + string.Join(", ", sentient.Distinct().Take(8))));
            w.Close();
        }

        // Opener headings follow the theme's text colour (they stayed black on the dark ground).
        static void OpenerChecks() {
            var c = OpenWith("ui-redesign-openers", "dark", 1180, 840);
            var w = c.Window;
            var wrong = new List<string>();
            foreach (var name in new[] { "FormatExpander", "SubtitleExpander", "ClipExpander", "ScheduleExpander", "FileExpander" }) {
                var expander = Find<Expander>(w, name);
                var header = expander.Template.FindName("HeaderButton", expander) as FrameworkElement;
                var text = header == null ? null : Visuals(header).OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrEmpty(t.Text));
                if (text == null || text.Foreground.ToString() != w.Resources["Text"].ToString()) wrong.Add(name + "=" + (text == null ? "?" : text.Foreground.ToString()));
            }
            T.Check(wrong.Count == 0, "açıcı başlıkları koyu temada metin renginde" + (wrong.Count == 0 ? "" : ": " + string.Join(", ", wrong)));
            w.Close();
        }

        static void ShellChecks() {
            var c = OpenWith("ui-redesign-shell", "light", 1180, 840);
            var w = c.Window;
            var rail = Find<Border>(w, "Sidebar");
            double railBefore = rail.ActualWidth;
            bool navBefore = Find<TextBlock>(w, "NavDownloadText").IsVisible;
            foreach (var size in ShotSizes) {
                Resize(w, size[0], size[1]);
                foreach (var language in new[] { "tr", "en" }) {
                    Loc.Set(language); Pump();
                    var clipped = ClippedButtons(rail);
                    T.Check(clipped.Count == 0, "kabuk düğmeleri kırpılmıyor (" + language + ", " + size[0] + ")" + (clipped.Count == 0 ? "" : ": " + string.Join(", ", clipped)));
                }
                Loc.Set("tr"); Pump();
            }
            Resize(w, 760, 560);
            var page = Find<ScrollViewer>(w, "DownloadPage");
            T.Check(page.ComputedHorizontalScrollBarVisibility != Visibility.Visible && ((FrameworkElement)page.Content).ActualWidth <= page.ViewportWidth + 1, "dar modda yatay taşma yok");
            Resize(w, 1180, 840);
            T.Check(Math.Abs(rail.ActualWidth - railBefore) < 0.5 && Find<TextBlock>(w, "NavDownloadText").IsVisible == navBefore, "dar moddan çıkınca geniş düzen geri gelir");
            w.Close();
        }

        static void Shot(Window window, string prefix, string theme) {
            foreach (var scale in new[] { 1.0, 1.5 }) {
                var name = prefix + "-" + (scale == 1.0 ? "100" : "150") + "-" + theme;
                var path = RenderScaled(window, name, scale);
                T.Check(File.Exists(path) && new FileInfo(path).Length > 0, "tasarım çekimi " + name);
            }
        }

        static void RedesignShots() {
            foreach (var theme in ShotThemes) {
                foreach (var size in ShotSizes) ShotViews(theme, size[0], size[1]);
                ShotCloseDialog(theme);
            }
            Loc.Set("tr");
            Theme.Set("light");
        }

        static List<HistoryEntry> ShotHistory(AppPaths paths) {
            Directory.CreateDirectory(paths.DefaultDownloads);
            var kept = Path.Combine(paths.DefaultDownloads, "Kalan video [abc] 720p.mp4");
            File.WriteAllText(kept, "x");
            return new List<HistoryEntry> {
                new HistoryEntry { Id = "1", Title = "Kalan video", Platform = "YouTube", SourceUrl = "https://www.youtube.com/watch?v=jNQXAC9IVRw", FilePath = kept, CompletedAt = Iso.Now(), Status = "completed", Format = "mp4", Profile = "compatible", Width = 1280, Height = 720, SizeBytes = 5242880, Channel = "Kanal A" },
                new HistoryEntry { Id = "2", Title = "Taşınmış dosya", Platform = "Instagram", SourceUrl = "https://www.instagram.com/reel/Chunk8-jurw/", FilePath = Path.Combine(paths.DefaultDownloads, "yok.mp4"), CompletedAt = Iso.From(DateTime.UtcNow.AddDays(-3)), Status = "completed", Format = "mp4" },
                new HistoryEntry { Id = "3", Title = "Engellenen video", Platform = "TikTok", SourceUrl = "https://www.tiktok.com/@a/video/1", CompletedAt = Iso.From(DateTime.UtcNow.AddDays(-10)), Status = "failed", Error = "IpBlocked", Format = "mp4" },
                new HistoryEntry { Id = "4", Title = "Eski Akış kaydı", FilePath = kept, CompletedAt = Iso.From(DateTime.UtcNow.AddDays(-40)), Status = "completed", Format = "mp3", Legacy = true }
            };
        }

        // Fills the queue with one job in each state: completed, downloading, waiting, paused, failed, needs action.
        static void ShotQueue(Controller c) {
            var url = Find<TextBox>(c.Window, "UrlBox");
            c.Enqueue(false);
            foreach (var name in new[] { "slow1", "slow2" }) { url.Text = "https://vimeo.com/" + name; Pump(); var job = c.Enqueue(false); job.Job.Title = name; }
            url.Text = "https://vimeo.com/held"; Pump();
            c.Enqueue(true);
            var failed = c.Queue.Add(new DownloadJob { Url = "https://vimeo.com/x", Platform = "Other", Title = "Kaldırılmış video", Options = Media.Opt("compatible", "mp4", 1080, "h264") }, true);
            failed.Job.State = JobState.Failed; failed.Job.Error = "Unavailable"; failed.Refresh();
            var disk = c.Queue.Add(new DownloadJob { Url = "https://vimeo.com/y", Platform = "Other", Title = "Büyük dosya", Options = Media.Opt("best", "mkv", 0, "auto") }, true);
            disk.Job.State = JobState.NeedsAction; disk.Job.Error = "DiskSpaceLow"; disk.Job.ErrorArgs = new List<string> { "4,2 GB", "1,1 GB" }; disk.Refresh();
            Wait(() => c.Queue.Items.Any(i => i.Job.Title == "slow1" && i.IsActive && i.ProgressValue > 3), 5000);
        }

        static void ShotViews(string theme, int width, int height) {
            var paths = NewPaths("ui-shots-" + theme + "-" + width);
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = theme; s.Parallel = 1; s.Notify = false;
            Store.SaveSettings(paths, s);
            Store.SaveHistory(paths, ShotHistory(paths));
            var c = Open(paths, width, height, new FakeExecutor());
            var w = c.Window;
            var size = width + "x" + height;
            Shot(w, "download-empty-" + size, theme);
            c.InfoSource = (u, token) => Task.FromResult(MediaParser.Parse(T.Fixture("youtube.json"), Platform.YouTube));
            Find<TextBox>(w, "UrlBox").Text = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
            Wait(() => Find<TextBlock>(w, "VideoTitle").Text != Loc.T("preview.empty.title") && !Find<TextBlock>(w, "VideoTitle").Text.StartsWith(Loc.T("preview.loading")) && Find<WrapPanel>(w, "Chips").Children.Count > 0, 4000);
            Shot(w, "download-preview-" + size, theme);
            if (width == 1180) ShotMenu(w, theme);
            ShotQueue(c);
            c.ShowPage("queue"); Pump();
            Shot(w, "queue-" + size, theme);
            c.ShowPage("history"); Pump();
            Shot(w, "history-" + size, theme);
            c.ShowPage("settings"); Pump();
            Shot(w, "settings-" + size, theme);
            w.Close();
        }

        // The open list of "Dosya türü": it lives in its own popup layer, so it is shot on its own.
        static void ShotMenu(Window w, string theme) {
            var box = Find<ComboBox>(w, "FormatBox");
            box.IsDropDownOpen = true; Pump();
            var popup = box.Template.FindName("PART_Popup", box) as System.Windows.Controls.Primitives.Popup;
            var panel = popup == null ? null : popup.Child as FrameworkElement;
            if (panel != null && panel.ActualWidth > 0) {
                foreach (var scale in new[] { 1.0, 1.5 }) {
                    var name = "menu-" + (scale == 1.0 ? "100" : "150") + "-" + theme;
                    var path = RenderElement(panel, w.Background, name, scale);
                    T.Check(File.Exists(path), "tasarım çekimi " + name);
                }
            } else T.Check(false, "açılır liste çekim için açıldı (" + theme + ")");
            box.IsDropDownOpen = false; Pump();
        }

        static void ShotCloseDialog(string theme) {
            var paths = NewPaths("ui-shots-close-" + theme);
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = theme; s.Notify = false;
            Store.SaveSettings(paths, s);
            var c = Open(paths, 1000, 760);
            var dialog = new CloseDialog(c.Window, 2);
            var window = dialog.Window;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000; window.Top = -32000; window.ShowActivated = false;
            window.Show(); Pump();
            Shot(window, "close", theme);
            window.Close();
            c.Window.Close();
        }
    }
}
