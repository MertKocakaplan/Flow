using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

[assembly: AssemblyTitle("Flow")]
[assembly: AssemblyDescription("Video and audio downloader")]
[assembly: AssemblyProduct("Flow")]
[assembly: AssemblyVersion("2.2.0.0")]
[assembly: AssemblyFileVersion("2.2.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
namespace Flow {
    public static class Program {
        public const string Version = "2.2";
        public static bool ScheduledLaunch;
        [STAThread] public static int Main(string[] args) {
            ScheduledLaunch = args != null && args.Contains(ScheduledStart.Argument);
            bool browserLaunch = args != null && args.Contains("--browser");
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var paths = AppPaths.Resolve();
            var instance = InstanceLock.TryAcquire(paths.DataDir);
            if (instance == null) { if (!browserLaunch) InstanceLock.SignalExisting(paths.DataDir); return 2; }
            using (instance) {
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (s, e) => { MessageBox.Show(Loc.Error(e.Exception), "Flow", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true; };
                try {
                    var state = Store.Load(paths);
                    var controller = new Controller(paths, state);
                    controller.StartBrowserBridge();
                    instance.OnActivate(() => controller.Window.Dispatcher.BeginInvoke(new Action(controller.BringToFront)));
                    // Opened by the Windows task: start quietly, minimized (or in the tray if enabled).
                    if (ScheduledLaunch || browserLaunch) { controller.Window.WindowState = WindowState.Minimized; controller.Window.ShowActivated = false; }
                    app.Run(controller.Window);
                } catch (Exception ex) { MessageBox.Show("Flow: " + ex.Message, "Flow", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
            }
            return 0;
        }
    }

    public partial class Controller {
        public Window Window;
        readonly AppPaths paths;
        readonly StoreState state;
        readonly FlowSettings settings;
        readonly List<HistoryEntry> history;
        bool loading, compact;
        string page = "download";

        public Controller(AppPaths paths, StoreState state) : this(paths, state, null) {}
        public Controller(AppPaths paths, StoreState state, IJobExecutor executor) {
            this.paths = paths; this.state = state; settings = state.Settings; history = state.History;
            Loc.Set(settings.Language);
            Theme.Set(settings.Theme);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) Window = (Window)XamlReader.Load(stream);
            Theme.Attach(Window);
            Loc.Attach(Window);
            SetWindowIcon();
            loading = true;
            InitNavigation(); InitQueue(executor); InitDownload(); InitHistory(); InitSettings(); InitExtras();
            loading = false;
            Loc.Changed += OnLanguageChanged;
            Window.Closed += (s, e) => Loc.Changed -= OnLanguageChanged;
            Window.SizeChanged += (s, e) => UpdateLayoutMode();
            Window.PreviewKeyDown += OnShortcut;
            Window.Closing += OnClosing;
            Window.Loaded += (s, e) => {
                C<TextBox>("UrlBox").Focus();
                UpdateLayoutMode();
                try { Engine.EnsureTools(); } catch (Exception ex) { ShowMessage(Loc.Error(ex), true); return; }
                ShowStartupNotice();
                queue.Start();
            };
        }
        partial void InitExtras();

        public void BringToFront() {
            if (!Window.IsVisible) Window.Show();
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            Window.Activate(); Window.Topmost = true; Window.Topmost = false; Window.Focus();
        }

        T C<T>(string name) where T : class { return Window.FindName(name) as T; }
        static string TagOf(ComboBox box) { var item = box.SelectedItem as FrameworkElement; return item == null ? null : Convert.ToString(item.Tag); }
        static void SelectTag(ComboBox box, string tag) { foreach (var item in box.Items.OfType<FrameworkElement>()) if (Convert.ToString(item.Tag) == tag) { box.SelectedItem = item; return; } if (box.Items.Count > 0 && box.SelectedIndex < 0) box.SelectedIndex = 0; }

        void SetWindowIcon() {
            try { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico")) if (s != null) Window.Icon = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); } catch (Exception) {}
        }

        void ShowStartupNotice() {
            if (state.Imported && !settings.ImportNoticeShown) { ShowMessage(Loc.F("msg.import.done", settings.ImportedCount), false); settings.ImportNoticeShown = true; SaveSettings(); }
            else if (state.ImportFailed) ShowMessage(Loc.T("msg.import.failed"), true);
            else if (state.SettingsLoad.Recovered || state.HistoryLoad.Recovered) ShowMessage(Loc.F("msg.recovered", state.SettingsLoad.CorruptCopy ?? state.HistoryLoad.CorruptCopy), true);
            else if (state.SettingsLoad.Reset || state.HistoryLoad.Reset) ShowMessage(Loc.F("msg.reset", state.SettingsLoad.CorruptCopy ?? state.HistoryLoad.CorruptCopy), true);
        }

        // ---- Navigation, layout, shortcuts ----
        void InitNavigation() {
            C<RadioButton>("NavDownload").Checked += (s, e) => ShowPage("download");
            C<RadioButton>("NavQueue").Checked += (s, e) => ShowPage("queue");
            C<RadioButton>("NavHistory").Checked += (s, e) => ShowPage("history");
            C<RadioButton>("NavSettings").Checked += (s, e) => ShowPage("settings");
            C<TextBlock>("VersionText").Text = Loc.F("app.version", Program.Version);
        }
        public void ShowPage(string name) {
            page = name;
            var map = new Dictionary<string, string> { { "download", "DownloadPage" }, { "queue", "QueuePage" }, { "history", "HistoryPage" }, { "settings", "SettingsPage" } };
            foreach (var pair in map) { var element = C<UIElement>(pair.Value); if (element != null) element.Visibility = pair.Key == name ? Visibility.Visible : Visibility.Collapsed; }
            var nav = C<RadioButton>("Nav" + char.ToUpperInvariant(name[0]) + name.Substring(1));
            if (nav != null && nav.IsChecked != true) nav.IsChecked = true;
            // The header carries the page title; it follows language changes through the resource reference.
            var title = C<TextBlock>("HeaderEyebrow");
            if (title != null) title.SetResourceReference(TextBlock.TextProperty, name == "download" ? "s.dl.title" : name == "history" ? "s.hist.title" : name == "settings" ? "s.set.title" : "s.queue.title");
            if (name == "history") RefreshHistory();
            if (name == "settings") RefreshSettingsInfo();
        }
        public string CurrentPage { get { return page; } }

        void UpdateLayoutMode() {
            compact = Window.ActualWidth > 0 && Window.ActualWidth < 980;
            C<Border>("Sidebar").Width = compact ? 70 : 212;
            foreach (var name in new[] { "BrandText", "NavDownloadText", "NavHistoryText", "NavSettingsText", "NavQueueText", "VersionText" }) {
                var element = C<UIElement>(name);
                if (element != null) element.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            }
            UpdateQueueIndicators();
        }

        void OnShortcut(object sender, KeyEventArgs e) {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (ctrl && (key == Key.D1 || key == Key.NumPad1)) { ShowPage("download"); e.Handled = true; }
            else if (ctrl && (key == Key.D2 || key == Key.NumPad2)) { ShowPage("queue"); e.Handled = true; }
            else if (ctrl && (key == Key.D3 || key == Key.NumPad3)) { ShowPage("history"); e.Handled = true; }
            else if (ctrl && (key == Key.D4 || key == Key.NumPad4)) { ShowPage("settings"); e.Handled = true; }
            else if (ctrl && key == Key.L) { ShowPage("download"); C<TextBox>("UrlBox").Focus(); C<TextBox>("UrlBox").SelectAll(); e.Handled = true; }
            else if (ctrl && key == Key.Enter && page == "download") { e.Handled = true; StartDownload(); }
            else if (key == Key.F5 && page == "download") { e.Handled = true; InspectNow(); }
            else if (key == Key.Escape && previewCts != null) { e.Handled = true; CancelPreview(); }
            else OnExtraShortcut(e, ctrl, key);
        }
        partial void OnExtraShortcut(KeyEventArgs e, bool ctrl, Key key);

        void OnLanguageChanged() {
            // ComboBox selection boxes keep a snapshot of their item text; reselect to refresh it.
            foreach (var box in new[] { "ProfileBox", "FormatBox", "CodecBox", "FpsBox", "LanguageBox", "ThemeBox", "DuplicateBox", "QueueFilterBox", "ParallelBox", "RetriesBox" }.Concat(ExtraCombos).Select(n => C<ComboBox>(n)).Where(b => b != null)) {
                var index = box.SelectedIndex; loading = true; box.SelectedIndex = -1; box.SelectedIndex = index; loading = false;
            }
            C<TextBlock>("VersionText").Text = Loc.F("app.version", Program.Version);
            RefreshDownloadTexts();
            RefreshHistory();
            RefreshSettingsInfo();
            foreach (var item in queue.Items) item.Refresh();
            UpdateQueueIndicators();
            OnLanguageChangedExtra();
        }
        readonly System.Collections.Generic.List<string> ExtraCombos = new System.Collections.Generic.List<string>();
        partial void OnLanguageChangedExtra();

        // ---- Status / messages ----
        void Announce(FrameworkElement element) {
            var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
            if (peer != null) peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        void SetChip(string key, string brushKey, string textKey, params object[] args) {
            C<TextBlock>("StatusChipText").Text = args.Length > 0 ? Loc.F(key, args) : Loc.T(key);
            C<Border>("StatusChip").SetResourceReference(Border.BackgroundProperty, brushKey);
            C<TextBlock>("StatusChipText").SetResourceReference(TextBlock.ForegroundProperty, textKey);
        }

        // The close prompt chooses between staying in the tray and an orderly shutdown.
        internal bool ConfirmClose = true;
        void OnClosing(object sender, System.ComponentModel.CancelEventArgs e) {
            if (OnCloseRequested(e)) return;
            queue.Shutdown();
            SaveSettings();
            OnClosed();
        }
        bool OnCloseRequested(System.ComponentModel.CancelEventArgs e) { bool handled = false; CloseHook(e, ref handled); return handled; }
        partial void CloseHook(System.ComponentModel.CancelEventArgs e, ref bool handled);
        partial void OnClosed();

        // ---- Files and folders ----
        void ChooseFolder(Action<string> chosen, string current) {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = Loc.T("msg.folder.pick"), SelectedPath = current, ShowNewFolderButton = true }) {
                if (dialog.ShowDialog(new NativeOwner(new System.Windows.Interop.WindowInteropHelper(Window).Handle)) == System.Windows.Forms.DialogResult.OK) chosen(dialog.SelectedPath);
            }
        }
        void OpenFolder(string path) {
            try {
                if (!Directory.Exists(path)) { ShowMessage(Loc.T("msg.folder.later"), false); return; }
                Process.Start(new ProcessStartInfo("explorer.exe", Engine.Quote(path)) { UseShellExecute = false });
            } catch (Exception ex) { ShowMessage(ex.Message, true); }
        }
        void Reveal(string path) {
            try {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { ShowMessage(Loc.T("msg.file.missing"), true); return; }
                Process.Start(new ProcessStartInfo("explorer.exe", "/select," + Engine.Quote(path)) { UseShellExecute = false });
            } catch (Exception ex) { ShowMessage(ex.Message, true); }
        }

        // Application-level diagnostics (job logs live on each job).
        readonly System.Text.StringBuilder appLog = new System.Text.StringBuilder();
        void Log(string line) { if (appLog.Length > 100000) appLog.Remove(0, 50000); appLog.AppendLine(Engine.Mask(line)); }
        void SaveSettings() { if (loading) return; try { Store.SaveSettings(paths, settings); } catch (Exception ex) { Log("Settings could not be saved: " + ex.Message); } }
        void SaveHistory() { try { Store.SaveHistory(paths, history); } catch (Exception ex) { Log("History could not be saved: " + ex.Message); } }
        class NativeOwner : System.Windows.Forms.IWin32Window { public IntPtr Handle { get; private set; } public NativeOwner(IntPtr handle) { Handle = handle; } }
    }

    public static class Thumbnails {
        // A thumbnail is optional. Bound its time/size and do not follow redirects.
        public static async Task<byte[]> Fetch(Uri uri) {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.AllowAutoRedirect = false;
            using (var timeout = new CancellationTokenSource(10000))
            using (timeout.Token.Register(() => request.Abort()))
            using (var response = (HttpWebResponse)await request.GetResponseAsync()) {
                if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > 5 * 1024 * 1024) return null;
                using (var input = response.GetResponseStream())
                using (var output = new MemoryStream()) {
                    var buffer = new byte[8192]; int count;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token)) > 0) {
                        if (output.Length + count > 5 * 1024 * 1024) return null;
                        output.Write(buffer, 0, count);
                    }
                    return output.ToArray();
                }
            }
        }
        public static BitmapImage Decode(byte[] bytes, int width) {
            using (var stream = new MemoryStream(bytes)) { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = width; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap; }
        }
    }
}
