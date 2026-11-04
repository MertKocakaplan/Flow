using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Flow;

namespace FlowTests {
    public static partial class UiTests {
        static AppPaths NewPaths(string name) {
            var root = T.Scratch(name);
            return new AppPaths { DataDir = Path.Combine(root, "Flow"), LegacyDir = Path.Combine(root, "Akis"), DefaultDownloads = Path.Combine(root, "Downloads") };
        }
        static Controller Open(AppPaths paths, double width, double height) { return Open(paths, width, height, new FakeExecutor()); }
        static Controller Open(AppPaths paths, double width, double height, IJobExecutor executor) {
            var controller = new Controller(paths, Store.Load(paths), executor);
            controller.ConfirmClose = false;
            ShowOffscreen(controller.Window, width, height);
            return controller;
        }
        public static IEnumerable<DependencyObject> Visuals(DependencyObject root) {
            var queue = new Queue<DependencyObject>(); queue.Enqueue(root);
            while (queue.Count > 0) {
                var node = queue.Dequeue(); yield return node;
                int n = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < n; i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
        static bool Shown(UIElement e) { return e.IsVisible; }

        static partial void RunAll() {
            Stage1Import();
            Stage2();
            RunLaterStages();
            CloseChoiceUi();
            BrowserTests.Api();
            RedesignShots();
        }
        static partial void RunLaterStages();

        static void Stage1Import() {
            var paths = NewPaths("ui-import");
            Directory.CreateDirectory(paths.LegacyDir);
            File.WriteAllText(paths.LegacySettings, "{\"Folder\":" + Engine.Json().Serialize(Path.Combine(paths.DataDir, "..", "Old")) + ",\"Format\":\"mp3\",\"Height\":720,\"History\":[{\"Title\":\"Eski video\",\"FilePath\":\"C:\\\\x\\\\Eski video [abc].mp3\",\"Summary\":\"22.09.2026  20:11  ·  MP3  ·  13,9 MB\"}]}", new UTF8Encoding(false));
            Loc.Set("tr");
            var c = Open(paths, 1180, 840);
            T.Check(c.Window.Title == "Flow", "window uses the Flow name");
            T.Check(Find<TextBox>(c.Window, "FolderBox").Text.EndsWith("Old"), "imported folder shown");
            T.Check(c.MessageTextValue.Contains("aktarıldı"), "one-time import notice shown");
            T.Check(TagOf(Find<ComboBox>(c.Window, "FormatBox")) == "mp3" && TagOf(Find<ComboBox>(c.Window, "ProfileBox")) == "mp3", "imported format selects the matching profile");
            c.Window.Close();
            var again = Open(paths, 1180, 840);
            T.Check(again.MessageTextValue == "", "import notice is shown only once");
            again.Window.Close();
        }
        static string TagOf(ComboBox box) { var item = box.SelectedItem as FrameworkElement; return item == null ? null : Convert.ToString(item.Tag); }

        static void Stage2() {
            // Every text key used in XAML exists in the table.
            string xaml;
            using (var s = typeof(Controller).Assembly.GetManifestResourceStream("MainWindow.xaml")) using (var r = new StreamReader(s)) xaml = r.ReadToEnd();
            var keys = Regex.Matches(xaml, @"DynamicResource s\.([A-Za-z0-9.]+)").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
            var missing = keys.Where(k => !Loc.Has(k)).ToList();
            T.Check(keys.Count > 40 && missing.Count == 0, "all XAML text keys translated" + (missing.Count > 0 ? " (missing: " + string.Join(", ", missing) + ")" : ""));
            var brushes = Regex.Matches(xaml, @"DynamicResource ([A-Z][A-Za-z0-9]+)\}").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
            T.Check(brushes.All(b => Theme.Keys.Contains(b) || (b.EndsWith("Color") && Theme.Keys.Contains(b.Substring(0, b.Length - 5)))), "all XAML color keys exist in both themes");
            T.Check(!Regex.IsMatch(xaml, @"(Background|Foreground|BorderBrush|Fill|Stroke)=""#"), "no fixed colors in the window markup");

            // Contrast in both palettes
            foreach (var theme in new[] { "light", "dark" }) {
                var p = Theme.Palette(theme);
                Func<string, string, double> cr = (a, b) => Theme.Contrast(p[a], p[b]);
                T.Check(cr("Text", "Surface") >= 7 && cr("Text", "Bg") >= 7 && cr("TextMuted", "Surface") >= 4.5 && cr("TextMuted", "Surface2") >= 4.5 && cr("TextFaint", "InputBg") >= 4.5, theme + " theme: body text contrast");
                T.Check(cr("OnAccent", "Accent") >= 4.5 && cr("AccentText", "Surface") >= 4.5 && cr("AccentText", "AccentSoft") >= 4.5, theme + " theme: accent contrast");
                T.Check(cr("Danger", "Surface") >= 4.5 && cr("Danger", "DangerSoft") >= 4.5 && cr("Warning", "Surface2") >= 4.5 && cr("Success", "Surface") >= 4.5 && cr("Info", "InfoSoft") >= 4.5, theme + " theme: status colors contrast");
                T.Check(cr("SidebarText", "Sidebar") >= 7 && cr("SidebarText", "SidebarActive") >= 4.5 && cr("SidebarMuted", "Sidebar") >= 4.5, theme + " theme: sidebar contrast");
                T.Check(cr("Focus", "Surface") >= 3 && cr("BorderStrong", "Surface") >= 2 && cr("InputBorder", "InputBg") >= 2, theme + " theme: focus ring and control borders visible");
            }

            var paths = NewPaths("ui-stage2");
            Loc.Set("tr");
            var c = Open(paths, 1180, 840);
            var w = c.Window;
            // Tabs and redirection
            T.Check(new[] { "TabYouTube", "TabInstagram", "TabTikTok", "TabOther" }.All(n => Find<RadioButton>(w, n) != null) && c.CurrentTab == Platform.YouTube, "four platform tabs, YouTube first");
            var url = Find<TextBox>(w, "UrlBox");
            url.Text = "https://www.instagram.com/reel/Chunk8-jurw/"; Pump();
            T.Check(c.CurrentTab == Platform.Instagram && Find<RadioButton>(w, "TabInstagram").IsChecked == true && url.Text == "https://www.instagram.com/reel/Chunk8-jurw/", "link pasted in the wrong tab moves to Instagram and is kept");
            T.Check(Find<Border>(w, "RedirectNote").IsVisible && Find<TextBlock>(w, "RedirectText").Text.Contains("Instagram"), "redirect is explained");
            url.Text = "https://vm.tiktok.com/ZMabc/"; Pump();
            T.Check(c.CurrentTab == Platform.TikTok, "short TikTok link opens the TikTok tab");
            url.Text = "vimeo.com/76979871"; Pump();
            T.Check(c.CurrentTab == Platform.Other && Find<TextBlock>(w, "TabHint").Text.Contains("Dailymotion"), "other sites tab with its own hint");
            Find<RadioButton>(w, "TabYouTube").IsChecked = true; Pump();
            T.Check(url.Text == "vimeo.com/76979871", "switching tabs manually never clears the link");
            // Per-tab profiles
            url.Text = ""; Pump();
            Find<RadioButton>(w, "TabInstagram").IsChecked = true; Pump();
            SelectByTag(Find<ComboBox>(w, "ProfileBox"), "best"); Pump();
            Find<RadioButton>(w, "TabYouTube").IsChecked = true; Pump();
            SelectByTag(Find<ComboBox>(w, "ProfileBox"), "mp3"); Pump();
            Find<RadioButton>(w, "TabInstagram").IsChecked = true; Pump();
            T.Check(TagOf(Find<ComboBox>(w, "ProfileBox")) == "best" && TagOf(Find<ComboBox>(w, "FormatBox")) == "mkv", "each tab remembers its profile");
            Find<RadioButton>(w, "TabYouTube").IsChecked = true; Pump();
            T.Check(TagOf(Find<ComboBox>(w, "ProfileBox")) == "mp3" && !Find<ComboBox>(w, "QualityBox").IsEnabled, "audio profile disables video quality");
            SelectByTag(Find<ComboBox>(w, "ProfileBox"), "compatible"); Pump();
            SelectByTag(Find<ComboBox>(w, "FormatBox"), "mkv"); Pump();
            T.Check(TagOf(Find<ComboBox>(w, "ProfileBox")) == "custom", "manual format change turns the preset into Custom");

            // Accessible names on inputs and icon-only controls
            var unnamed = Visuals(w).OfType<Control>().Where(x => x.IsVisible && (x is TextBox || x is ComboBox)).Where(x => string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(x))).Select(x => x.Name).ToList();
            T.Check(unnamed.Count == 0, "inputs have accessible names" + (unnamed.Count > 0 ? " (" + string.Join(",", unnamed) + ")" : ""));
            var buttons = Visuals(w).OfType<ButtonBase>().Where(b => b.IsVisible && b.Focusable).ToList();
            T.Check(buttons.All(b => b.IsTabStop) && buttons.Count > 8, "buttons reachable with the keyboard");

            SelectByTag(Find<ComboBox>(w, "ThemeBox"), "light"); Pump();
            Console.WriteLine("SCREEN " + Render(w, "stage2-tr-light-download"));
            c.ShowPage("settings"); Pump();
            Console.WriteLine("SCREEN " + Render(w, "stage2-tr-light-settings"));
            c.ShowPage("download"); Pump();
            // Language switch keeps state, updates texts
            url.Text = "https://www.youtube.com/watch?v=jNQXAC9IVRw"; Pump();
            c.ShowPage("settings"); Pump();
            SelectByTag(Find<ComboBox>(w, "LanguageBox"), "en"); Pump();
            T.Check(Loc.Lang == "en" && Find<TextBlock>(w, "NavDownloadText").Text == "Download" && url.Text.Contains("jNQXAC9IVRw"), "language switch applies at once and keeps the link");
            var selectionText = Find<ComboBox>(w, "ThemeBox").SelectionBoxItem as string;
            T.Check(selectionText == "Light", "open selection boxes follow the language");
            c.ShowPage("download"); Pump();
            var turkish = new Regex("[ğüşıöçĞÜŞİÖÇ]");
            var leftovers = Visuals(w).OfType<TextBlock>().Where(t => t.IsVisible && turkish.IsMatch(t.Text ?? "") && t.Text != "Türkçe").Select(t => t.Text).ToList();
            T.Check(leftovers.Count == 0, "no Turkish leftovers in English UI" + (leftovers.Count > 0 ? " (" + string.Join(" | ", leftovers.Take(3)) + ")" : ""));
            // Theme switch
            SelectByTag(Find<ComboBox>(w, "ThemeBox"), "dark"); Pump();
            var bg = ((SolidColorBrush)w.Resources["Bg"]).Color;
            T.Check(Theme.Current == "dark" && bg == Theme.Palette("dark")["Bg"] && url.Text.Contains("jNQXAC9IVRw"), "dark theme applies without reloading");
            Console.WriteLine("SCREEN " + Render(w, "stage2-en-dark-download"));
            c.ShowPage("settings"); Pump();
            Console.WriteLine("SCREEN " + Render(w, "stage2-en-dark-settings"));
            SelectByTag(Find<ComboBox>(w, "LanguageBox"), "tr"); Pump();
            Console.WriteLine("SCREEN " + Render(w, "stage2-tr-dark-settings"));
            SelectByTag(Find<ComboBox>(w, "ThemeBox"), "light"); Pump();
            T.Check(Theme.Current == "light" && Loc.Lang == "tr", "back to Turkish and light");
            var saved = Store.Load(paths).Settings;
            T.Check(saved.Language == "tr" && saved.Theme == "light", "language and theme preferences persisted");
            SelectByTag(Find<ComboBox>(w, "ThemeBox"), "system"); Pump();
            T.Check(Theme.Current == (Theme.SystemUsesLight() ? "light" : "dark"), "system theme follows Windows setting");
            w.Close();

            // Small window / high scaling: 1366x768 at 150% leaves about 910x512 DIPs.
            foreach (var size in new[] { new Size(910, 512), new Size(760, 560) }) {
                var small = Open(NewPaths("ui-small"), size.Width, size.Height);
                Pump();
                var page = Find<ScrollViewer>(small.Window, "DownloadPage");
                var content = (FrameworkElement)page.Content;
                T.Check(Find<Border>(small.Window, "Sidebar").ActualWidth <= 72 && content.ActualWidth <= page.ViewportWidth + 1 && page.ComputedHorizontalScrollBarVisibility != Visibility.Visible, "compact layout at " + size.Width + "x" + size.Height + " without horizontal overflow");
                var tabs = Visuals(small.Window).OfType<RadioButton>().Where(r => r.GroupName == "platform").ToList();
                T.Check(tabs.All(t => t.ActualWidth > 60), "platform tabs stay usable at " + size.Width);
                Console.WriteLine("SCREEN " + Render(small.Window, "stage2-small-" + size.Width));
                small.Window.Close();
            }
            // Long English texts
            var enPaths = NewPaths("ui-en");
            var enSettings = SettingsRules.Defaults(enPaths); enSettings.Language = "en"; enSettings.Theme = "light";
            Store.SaveSettings(enPaths, enSettings);
            var en = Open(enPaths, 910, 700);
            T.Check(Loc.Lang == "en" && Theme.Current == "light", "saved language and theme are applied at start");
            Find<RadioButton>(en.Window, "TabOther").IsChecked = true; Pump();
            var hint = Find<TextBlock>(en.Window, "TabHint");
            T.Check(hint.ActualWidth <= ((FrameworkElement)Find<ScrollViewer>(en.Window, "DownloadPage").Content).ActualWidth && hint.ActualHeight > 16, "long English hint wraps instead of overflowing");
            Console.WriteLine("SCREEN " + Render(en.Window, "stage2-en-light-other"));
            en.Window.Close();
            Loc.Set("tr");

            // Auto preview: only the latest link may fill the preview; older requests are cancelled.
            var ap = Open(NewPaths("ui-autopreview"), 1180, 840);
            var calls = new List<string>(); var canceled = new List<string>();
            ap.InfoSource = async (u, token) => {
                calls.Add(u.Url);
                try { await Task.Delay(u.Url.Contains("slow") ? 1500 : 100, token); }
                catch (OperationCanceledException) { canceled.Add(u.Url); throw; }
                return MediaParser.Parse("{\"title\":\"" + (u.Url.Contains("slow") ? "Slow" : "Fast") + "\",\"duration\":5}", u.Platform);
            };
            var box = Find<TextBox>(ap.Window, "UrlBox");
            box.Text = "https://vimeo.com/slow1"; Wait(() => calls.Count == 1, 3000);
            box.Text = "https://vimeo.com/fast2"; Wait(() => Find<TextBlock>(ap.Window, "VideoTitle").Text == "Fast", 4000);
            Wait(() => false, 1800);
            T.Check(calls.Count == 2 && canceled.Contains("https://vimeo.com/slow1") && Find<TextBlock>(ap.Window, "VideoTitle").Text == "Fast", "stale preview request cancelled, newest link wins");
            box.Text = "https://www.youtube.com/watch?v=jNQXAC9IVR"; Wait(() => false, 1200);
            T.Check(calls.Count == 2 && Find<TextBlock>(ap.Window, "VideoMeta").Text.Length > 0, "invalid link is not sent to the engine");
            ap.InfoSource = (u, token) => Task.FromResult(MediaParser.Parse(T.Fixture("instagram.json"), Platform.Instagram));
            box.Text = "https://www.instagram.com/reel/Chunk8-jurw/"; Wait(() => Find<TextBlock>(ap.Window, "VideoTitle").Text != Loc.T("preview.empty.title") && Find<WrapPanel>(ap.Window, "Chips").Children.Count > 0, 3000);
            var chipTexts = Find<WrapPanel>(ap.Window, "Chips").Children.OfType<Border>().Select(b => ((TextBlock)b.Child).Text).ToList();
            T.Check(chipTexts.Any(t => t.Contains("720×1280") && t.Contains("dikey")) && chipTexts.Contains(Loc.T("chip.silent")), "preview shows real size, orientation and missing audio");
            var qualities = Find<ComboBox>(ap.Window, "QualityBox").Items.OfType<ComboBoxItem>().Select(i => Convert.ToString(i.Content)).ToList();
            T.Check(qualities.Any(q => q.StartsWith("720p") && q.Contains("720×1280")) && !qualities.Any(q => q.StartsWith("1080p")), "quality list comes from the video's real formats");
            SelectByTag(Find<ComboBox>(ap.Window, "ProfileBox"), "mp3"); Pump();
            T.Check(Find<TextBlock>(ap.Window, "SelectionWarning").Text == Loc.Error(ErrorCode.NoAudio) && ap.Enqueue(false) == null, "audio choice on a silent video is explained and not queued");
            Console.WriteLine("SCREEN " + Render(ap.Window, "stage2-preview-instagram"));
            ap.Window.Close();
        }
        static void SelectByTag(ComboBox box, string tag) { foreach (var item in box.Items.OfType<FrameworkElement>()) if (Convert.ToString(item.Tag) == tag) { box.SelectedItem = item; return; } }
    }
}
