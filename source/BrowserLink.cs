using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using Shape = System.Windows.Shapes.Shape;
using System.Windows.Threading;
using Microsoft.Win32;
using Flow.BrowserIntegration;

namespace Flow {
    // Where Chrome (and Opera, which reads the same key) finds Flow's browser host.
    public interface IHostRegistry { string Read(); void Write(string manifestFile); void Delete(); }

    public sealed class ChromeHostRegistry : IHostRegistry {
        const string Key = @"Software\Google\Chrome\NativeMessagingHosts\" + BrowserProtocol.HostName;
        // Chrome checks the 32-bit view first, even on 64-bit Windows (same as register-browser.ps1).
        static RegistryKey Root() { return RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32); }
        public string Read() { using (var root = Root()) using (var key = root.OpenSubKey(Key)) return key == null ? null : key.GetValue("") as string; }
        public void Write(string manifestFile) { using (var root = Root()) using (var key = root.CreateSubKey(Key)) key.SetValue("", manifestFile, RegistryValueKind.String); }
        public void Delete() { using (var root = Root()) root.DeleteSubKey(Key, false); }
    }

    // Tests and anything that must not touch the user's registry.
    public sealed class MemoryHostRegistry : IHostRegistry {
        public string Value;
        public string Read() { return Value; }
        public void Write(string manifestFile) { Value = manifestFile; }
        public void Delete() { Value = null; }
    }

    public enum Registration { None, Ours, OtherPackage, Broken }
    public enum LinkState { NotInstalled, OtherPackage, Broken, Waiting, NeverSeen, Connected, Away, Outdated }

    public static class BrowserLink {
        public static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(3);    // the extension calls once a minute
        public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(90);

        public static string HostFile(string root) { return Path.Combine(root, "Flow.BrowserHost.exe"); }
        public static string ManifestFile(string root) { return Path.Combine(root, "flow.native.json"); }
        public static string ExtensionFolder(string root) { return Path.Combine(root, "chrome-extension"); }
        static string Full(string path) { try { return Path.GetFullPath(path).TrimEnd('\\'); } catch (Exception) { return path ?? ""; } }

        // The version Chrome would load from this package's folder: what "up to date" means.
        public static string PackagedExtensionVersion(string root) {
            try {
                var file = Path.Combine(ExtensionFolder(root), "manifest.json");
                if (!File.Exists(file)) return null;
                var data = Engine.Json().DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                return AppUpdates.Clean(Engine.Text(data, "version", null));
            } catch (Exception) { return null; }
        }

        public static string ManifestJson(string hostFile) {
            return Engine.Json().Serialize(new Dictionary<string, object> {
                { "name", BrowserProtocol.HostName }, { "description", "Flow browser companion" }, { "path", hostFile }, { "type", "stdio" },
                { "allowed_origins", new[] { "chrome-extension://" + BrowserProtocol.ExtensionId + "/" } }
            });
        }

        public static Registration Check(IHostRegistry registry, string root) {
            string value;
            try { value = registry.Read(); } catch (Exception) { return Registration.None; }
            if (string.IsNullOrEmpty(value)) return Registration.None;
            if (!string.Equals(Full(value), Full(ManifestFile(root)), StringComparison.OrdinalIgnoreCase)) return Registration.OtherPackage;
            try {
                if (!File.Exists(value) || !File.Exists(HostFile(root))) return Registration.Broken;
                var data = Engine.Json().DeserializeObject(File.ReadAllText(value, Encoding.UTF8)) as Dictionary<string, object>;
                return string.Equals(Full(Engine.Text(data, "path", "")), Full(HostFile(root)), StringComparison.OrdinalIgnoreCase) ? Registration.Ours : Registration.Broken;
            } catch (Exception) { return Registration.Broken; }
        }

        // Same result as Tarayici-Kur.bat: the host manifest next to the host, and one per-user registry value.
        public static void Install(IHostRegistry registry, string root) {
            var host = HostFile(root);
            if (!File.Exists(host)) throw new FlowException(ErrorCode.ToolsMissing, host, new[] { Path.GetFileName(host) });
            File.WriteAllText(ManifestFile(root), ManifestJson(host), new UTF8Encoding(false));
            registry.Write(ManifestFile(root));
        }
        // Only this package's own registration is removed.
        public static bool Uninstall(IHostRegistry registry, string root) {
            if (Check(registry, root) == Registration.OtherPackage || string.IsNullOrEmpty(registry.Read())) return false;
            registry.Delete();
            return true;
        }

        public static LinkState Evaluate(Registration registration, DateTime? seenAt, string seenVersion, string expected, DateTime nowUtc, DateTime startedUtc) {
            if (registration == Registration.None) return LinkState.NotInstalled;
            if (registration == Registration.OtherPackage) return LinkState.OtherPackage;
            if (registration == Registration.Broken) return LinkState.Broken;
            if (!seenAt.HasValue) return nowUtc - startedUtc < StartupGrace ? LinkState.Waiting : LinkState.NeverSeen;
            // Versions before 1.2.0 do not report themselves; they are older than any packaged one.
            if (expected != null && (seenVersion == null || AppUpdates.IsNewer(expected, seenVersion))) return LinkState.Outdated;
            if (nowUtc - seenAt.Value > AwayAfter) return nowUtc - startedUtc < StartupGrace ? LinkState.Waiting : LinkState.Away;
            return LinkState.Connected;
        }

        // Chrome's own path from Windows' App Paths list; null when Chrome is not installed.
        public static string ChromePath() {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 }) {
                    try {
                        using (var root = RegistryKey.OpenBaseKey(hive, view))
                        using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe")) {
                            var path = key == null ? null : key.GetValue("") as string;
                            if (!string.IsNullOrEmpty(path) && File.Exists(path.Trim('"'))) return path.Trim('"');
                        }
                    } catch (Exception) {}
                }
            return null;
        }
    }

    // The rail's browser line and the Browser page.
    public partial class Controller {
        internal IHostRegistry HostRegistry = ShellIntegration ? (IHostRegistry)new ChromeHostRegistry() : new MemoryHostRegistry();
        internal string PackageRoot = Engine.BaseDir;
        internal Func<string> ChromeLocator = BrowserLink.ChromePath;
        readonly DateTime startedUtc = DateTime.UtcNow;
        DateTime? browserSeenAt; string browserSeenVersion; DateTime savedSeenAt;
        bool reloadPending; string expectedExtension;
        DispatcherTimer linkTimer;
        public LinkState BrowserState { get; private set; }

        void InitBrowserPage() {
            browserSeenAt = Iso.Parse(settings.BrowserSeenAt);
            browserSeenVersion = settings.BrowserSeenVersion;
            C<RadioButton>("NavBrowser").Checked += (s, e) => ShowPage("browser");
            C<Button>("BrowserLink").Click += (s, e) => ShowPage("browser");
            C<Button>("BrowserInstallButton").Click += (s, e) => InstallBrowserLink();
            C<Button>("BrowserRemoveButton").Click += (s, e) => RemoveBrowserLink();
            C<Button>("BrowserOpenChromeButton").Click += (s, e) => OpenChromeExtensions();
            C<Button>("BrowserCopyFolderButton").Click += (s, e) => { try { Clipboard.SetText(BrowserLink.ExtensionFolder(PackageRoot)); BrowserAction(Loc.T("br.copied"), false); } catch (Exception) { BrowserAction(Loc.T("msg.clipboard.error"), true); } };
            C<Button>("BrowserShowFolderButton").Click += (s, e) => OpenFolder(BrowserLink.ExtensionFolder(PackageRoot));
            C<Button>("BrowserReloadButton").Click += (s, e) => ReloadExtension();
            linkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            linkTimer.Tick += (s, e) => RefreshBrowserLink();
            Window.Loaded += (s, e) => { linkTimer.Start(); RefreshBrowserLink(); };
            Window.Closed += (s, e) => linkTimer.Stop();
            RefreshBrowserLink();
        }

        // Called for every request the extension makes. A ping carries the extension's version (null before 1.2.0).
        internal void BrowserSeen(bool ping, string version) {
            var now = UtcToday();
            browserSeenAt = now;
            if (ping && browserSeenVersion != version) { browserSeenVersion = version; savedSeenAt = DateTime.MinValue; }
            if (now - savedSeenAt > TimeSpan.FromMinutes(10)) {
                settings.BrowserSeenAt = Iso.From(now); settings.BrowserSeenVersion = browserSeenVersion; savedSeenAt = now; SaveSettings();
            }
            RefreshBrowserLink();
        }
        internal string ExpectedExtension { get { return expectedExtension; } }
        // A reload is sent once, to an extension that can perform it and only while it is out of date.
        internal bool TakeReload() {
            if (!reloadPending || BrowserState != LinkState.Outdated || browserSeenVersion == null) return false;
            reloadPending = false;
            return true;
        }

        internal void RefreshBrowserLink() {
            expectedExtension = BrowserLink.PackagedExtensionVersion(PackageRoot);
            var registration = BrowserLink.Check(HostRegistry, PackageRoot);
            BrowserState = BrowserLink.Evaluate(registration, browserSeenAt, browserSeenVersion, expectedExtension, UtcToday(), startedUtc);
            var state = BrowserState;
            bool live = state == LinkState.Connected || (state == LinkState.Outdated && browserSeenAt.HasValue && UtcToday() - browserSeenAt.Value <= BrowserLink.AwayAfter);

            // Rail: a filled square while the extension is talking to Flow, an empty one otherwise.
            var dot = C<Rectangle>("BrowserDot");
            if (live) dot.SetResourceReference(Shape.FillProperty, "SidebarText"); else dot.Fill = Brushes.Transparent;
            C<TextBlock>("BrowserLinkText").Text = Loc.T("br.rail." + state);
            System.Windows.Automation.AutomationProperties.SetName(C<Button>("BrowserLink"), Loc.T("br.rail." + state));
            C<Button>("BrowserLink").ToolTip = Loc.T("br.state." + state + ".detail");

            // Page.
            if (C<TextBlock>("BrowserStateTitle") == null) return;
            C<TextBlock>("BrowserStateTitle").Text = Loc.T("br.state." + state);
            C<TextBlock>("BrowserStateText").Text = Loc.T("br.state." + state + ".detail");
            string value; try { value = HostRegistry.Read(); } catch (Exception) { value = null; }
            C<TextBlock>("BrowserRegText").Text = registration == Registration.Ours ? Loc.F("br.reg.ours", BrowserLink.ManifestFile(PackageRoot))
                : registration == Registration.OtherPackage ? Loc.F("br.reg.other", value)
                : registration == Registration.Broken ? Loc.T("br.reg.broken") : Loc.T("br.reg.none");
            C<TextBlock>("BrowserSeenText").Text = !browserSeenAt.HasValue ? Loc.T("br.seen.never")
                : Loc.F("br.seen.at", Loc.Date(browserSeenAt.Value), browserSeenVersion ?? Loc.T("br.seen.legacy"));
            C<TextBlock>("BrowserExpectedText").Text = expectedExtension ?? Loc.T("br.expected.missing");
            C<TextBlock>("BrowserFolderText").Text = BrowserLink.ExtensionFolder(PackageRoot);
            C<TextBlock>("BrowserAppText").Text = Loc.F("br.app", Program.Version, BrowserProtocol.Version, BrowserProtocol.ExtensionId);
            C<Button>("BrowserInstallButton").Content = Loc.T(registration == Registration.Ours ? "br.install.again" : "br.install");
            // The page's one filled button is the step that is actually needed; a working setup gets an ordinary one.
            if (registration == Registration.Ours) C<Button>("BrowserInstallButton").ClearValue(FrameworkElement.StyleProperty);
            else C<Button>("BrowserInstallButton").SetResourceReference(FrameworkElement.StyleProperty, "Primary");
            C<Button>("BrowserRemoveButton").Visibility = registration == Registration.Ours || registration == Registration.Broken ? Visibility.Visible : Visibility.Collapsed;
            C<Button>("BrowserReloadButton").IsEnabled = state == LinkState.Outdated;
            C<TextBlock>("BrowserUpdateText").Text = state == LinkState.Outdated
                ? (browserSeenVersion == null ? Loc.F("br.update.legacy", expectedExtension) : reloadPending ? Loc.T("br.update.sent") : Loc.F("br.update.ready", browserSeenVersion, expectedExtension))
                : browserSeenAt.HasValue ? Loc.F("br.update.current", browserSeenVersion ?? "?") : Loc.T("br.update.unknown");
        }

        void BrowserAction(string text, bool error) {
            var box = C<TextBlock>("BrowserActionText");
            box.Text = text;
            box.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "TextMuted");
        }
        internal string BrowserActionValue { get { return C<TextBlock>("BrowserActionText").Text; } }

        internal void InstallBrowserLink() {
            try {
                if (BrowserLink.Check(HostRegistry, PackageRoot) == Registration.OtherPackage && ConfirmClose && MessageBox.Show(Window, Loc.T("br.install.replace"), "Flow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                BrowserLink.Install(HostRegistry, PackageRoot);
                BrowserAction(Loc.T("br.install.done"), false);
            } catch (Exception ex) { BrowserAction(Loc.F("br.install.failed", Loc.Error(ex)), true); }
            RefreshBrowserLink();
        }
        internal void RemoveBrowserLink() {
            if (ConfirmClose && MessageBox.Show(Window, Loc.T("br.remove.confirm"), "Flow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { BrowserAction(Loc.T(BrowserLink.Uninstall(HostRegistry, PackageRoot) ? "br.remove.done" : "br.remove.none"), false); }
            catch (Exception ex) { BrowserAction(ex.Message, true); }
            RefreshBrowserLink();
        }
        void OpenChromeExtensions() {
            var chrome = ChromeLocator();
            if (chrome == null) { BrowserAction(Loc.T("br.chrome.missing"), true); return; }
            try { Process.Start(new ProcessStartInfo(chrome, "chrome://extensions") { UseShellExecute = false }); BrowserAction(Loc.T("br.chrome.opened"), false); }
            catch (Exception ex) { BrowserAction(ex.Message, true); }
        }
        // An unpacked extension reloads from its folder; the next call from it receives the request.
        internal void ReloadExtension() {
            if (BrowserState != LinkState.Outdated) return;
            if (browserSeenVersion == null) { BrowserAction(Loc.T("br.update.manual"), false); OpenChromeExtensions(); return; }
            reloadPending = true;
            BrowserAction(Loc.T("br.update.sent"), false);
            RefreshBrowserLink();
        }
    }
}
