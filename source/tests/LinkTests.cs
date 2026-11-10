using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using Flow;
using Flow.BrowserIntegration;

namespace FlowTests {
    sealed class FakeFeed : IAppReleaseFeed {
        public AppRelease Release; public bool Fail; public int Calls;
        public Task<AppRelease> Latest(CancellationToken token) {
            Calls++;
            if (Fail) { var t = new TaskCompletionSource<AppRelease>(); t.SetException(new System.Net.WebException("offline")); return t.Task; }
            return Task.FromResult(Release);
        }
    }

    // New Flow releases, the browser extension's registration, its connection state and its version.
    public static class LinkUnit {
        public static void Run() { Versions(); Releases(); Registration(); States(); Bridge(); SettingsKeep(); }

        static void Versions() {
            T.Check(AppUpdates.IsNewer("v2.3", "2.2") && AppUpdates.IsNewer("2.10", "2.9") && AppUpdates.IsNewer("2.2.1", "2.2"), "release numbers compare part by part, with or without a leading v");
            T.Check(!AppUpdates.IsNewer("2.2.0", "2.2") && !AppUpdates.IsNewer("2.1", "2.2") && !AppUpdates.IsNewer("latest", "2.2") && !AppUpdates.IsNewer("2.3", "garbage") && !AppUpdates.IsNewer(null, "2.2"), "equal, older or unreadable numbers are never newer");
            T.Check(AppUpdates.Clean("v2.3") == "2.3" && AppUpdates.Clean(" 1.2.0 ") == "1.2.0" && AppUpdates.Clean("2.3-beta") == null && AppUpdates.Clean("1.2.3.4.5") == null, "release numbers are cleaned or refused");
        }

        static void Releases() {
            var r = AppUpdates.ParseRelease("{\"tag_name\":\"v2.3\",\"html_url\":\"https://github.com/MertKocakaplan/Flow/releases/tag/v2.3\",\"draft\":false,\"prerelease\":false}");
            T.Check(r != null && r.Version == "2.3" && r.Url == "https://github.com/MertKocakaplan/Flow/releases/tag/v2.3", "the newest release is read from GitHub's answer");
            T.Check(AppUpdates.ParseRelease("{\"tag_name\":\"v2.4\",\"prerelease\":true}") == null && AppUpdates.ParseRelease("{\"tag_name\":\"v2.4\",\"draft\":true}") == null && AppUpdates.ParseRelease("{\"tag_name\":\"nightly\"}") == null && AppUpdates.ParseRelease("not json") == null, "drafts, pre-releases and odd tags are ignored");
            var foreign = AppUpdates.ParseRelease("{\"tag_name\":\"2.3\",\"html_url\":\"https://evil.example/flow\"}");
            T.Check(foreign.Url == AppUpdates.ReleasesPage && !AppUpdates.IsReleasePage("http://github.com/MertKocakaplan/Flow/releases") && !AppUpdates.IsReleasePage("https://github.com/someone/Flow/releases") && AppUpdates.IsReleasePage(AppUpdates.ReleasesPage), "only Flow's own release page is ever opened");
            var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
            T.Check(AppUpdates.Due(null, now) && AppUpdates.Due(Iso.From(now.AddHours(-25)), now) && !AppUpdates.Due(Iso.From(now.AddHours(-2)), now) && AppUpdates.Due(Iso.From(now.AddDays(3)), now), "a check runs at most once a day (a clock moved back does not block it)");
        }

        public static string Package(string version) {
            var root = T.Scratch("package");
            File.WriteAllText(BrowserLink.HostFile(root), "host");
            Directory.CreateDirectory(BrowserLink.ExtensionFolder(root));
            if (version != null) File.WriteAllText(Path.Combine(BrowserLink.ExtensionFolder(root), "manifest.json"), "{\"manifest_version\":3,\"version\":\"" + version + "\"}");
            return root;
        }

        static void Registration() {
            var root = Package("1.2.0");
            var registry = new MemoryHostRegistry();
            T.Check(BrowserLink.Check(registry, root) == Flow.Registration.None, "no registration is reported as not set up");
            BrowserLink.Install(registry, root);
            var manifest = Engine.Json().DeserializeObject(File.ReadAllText(BrowserLink.ManifestFile(root))) as Dictionary<string, object>;
            T.Check(registry.Value == BrowserLink.ManifestFile(root) && Engine.Text(manifest, "path", null) == BrowserLink.HostFile(root) && Engine.Text(manifest, "name", null) == BrowserProtocol.HostName && Engine.Text(manifest, "type", null) == "stdio", "one-click setup writes the host manifest and registers it");
            var origins = (manifest["allowed_origins"] as System.Collections.IEnumerable).Cast<object>().ToList();
            T.Check(origins.Count == 1 && (string)origins[0] == "chrome-extension://" + BrowserProtocol.ExtensionId + "/", "only Flow's own extension may talk to the host");
            T.Check(BrowserLink.Check(registry, root) == Flow.Registration.Ours, "the registration is recognised as this package's");
            T.Check(BrowserLink.Check(registry, Package("1.2.0")) == Flow.Registration.OtherPackage, "a registration of another folder is recognised");
            File.Delete(BrowserLink.HostFile(root));
            T.Check(BrowserLink.Check(registry, root) == Flow.Registration.Broken, "a missing host makes the registration broken");
            T.Rejects(() => BrowserLink.Install(new MemoryHostRegistry(), root), ErrorCode.ToolsMissing, "setup refuses a package without its host");
            var other = new MemoryHostRegistry { Value = BrowserLink.ManifestFile(Package("1.2.0")) };
            T.Check(!BrowserLink.Uninstall(other, root) && other.Value != null, "removing never touches another folder's registration");
            var mine = new MemoryHostRegistry(); var own = Package("1.2.0"); BrowserLink.Install(mine, own);
            T.Check(BrowserLink.Uninstall(mine, own) && mine.Value == null, "removing deletes this package's registration");
            T.Check(BrowserLink.PackagedExtensionVersion(own) == "1.2.0" && BrowserLink.PackagedExtensionVersion(Package(null)) == null, "the packaged extension version is read from its manifest");
            var repo = Path.Combine(Engine.BaseDir, "chrome-extension", "manifest.json");
            T.Check(!File.Exists(repo) || AppUpdates.Clean(BrowserLink.PackagedExtensionVersion(Engine.BaseDir)) != null, "the shipped extension has a readable version");
        }

        static void States() {
            var start = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
            var late = start.AddMinutes(5);
            Func<Flow.Registration, DateTime?, string, DateTime, LinkState> e = (r, seen, v, now) => BrowserLink.Evaluate(r, seen, v, "1.2.0", now, start);
            T.Check(e(Flow.Registration.None, late, "1.2.0", late) == LinkState.NotInstalled && e(Flow.Registration.OtherPackage, null, null, late) == LinkState.OtherPackage && e(Flow.Registration.Broken, null, null, late) == LinkState.Broken, "registration problems come first");
            T.Check(e(Flow.Registration.Ours, null, null, start.AddSeconds(20)) == LinkState.Waiting && e(Flow.Registration.Ours, null, null, late) == LinkState.NeverSeen, "an extension not heard from yet is awaited, then reported as not connected");
            T.Check(e(Flow.Registration.Ours, late.AddSeconds(-50), "1.2.0", late) == LinkState.Connected && e(Flow.Registration.Ours, late.AddMinutes(-4), "1.2.0", late) == LinkState.Away, "a recent call means connected; silence for a few minutes means away");
            T.Check(e(Flow.Registration.Ours, late, "1.1.0", late) == LinkState.Outdated && e(Flow.Registration.Ours, late, null, late) == LinkState.Outdated && e(Flow.Registration.Ours, late, "1.10.0", late) == LinkState.Connected, "an older or silent extension needs an update; a newer one does not");
            T.Check(BrowserLink.Evaluate(Flow.Registration.Ours, late, null, null, late, start) == LinkState.Connected, "without a packaged extension nothing is called out of date");
            T.Check(Enum.GetNames(typeof(LinkState)).All(s => Loc.Has("br.state." + s) && Loc.Has("br.state." + s + ".detail") && Loc.Has("br.rail." + s)), "every connection state has its texts");
        }

        static void Bridge() {
            var dir = T.Scratch("browser-seen");
            var paths = new AppPaths { DataDir = Path.Combine(dir, "data"), LegacyDir = Path.Combine(dir, "legacy"), DefaultDownloads = Path.Combine(dir, "downloads") };
            var settings = SettingsRules.Defaults(paths);
            var api = new BrowserApi(new QueueService(paths, () => settings, new FakeExecutor()), () => settings, (u, t) => Task.FromResult(new MediaInfo()), () => {});
            var seen = new List<string>(); bool reload = true;
            api.Seen = (ping, version) => seen.Add(ping + ":" + (version ?? "null"));
            api.ExpectedExtension = () => "1.3.0";
            api.TakeReload = () => { var r = reload; reload = false; return r; };
            Func<object, Dictionary<string, object>> reply = request => {
                var task = api.Handle(BrowserProtocol.Json().Serialize(request), CancellationToken.None);
                return (Dictionary<string, object>)BrowserProtocol.Json().DeserializeObject(task.GetAwaiter().GetResult());
            };
            var first = (Dictionary<string, object>)((Dictionary<string, object>)reply(new { v = 1, action = "ping", ext = "1.2.0" })["data"])["extension"];
            var second = (Dictionary<string, object>)((Dictionary<string, object>)reply(new { v = 1, action = "ping", ext = "1.2.0" })["data"])["extension"];
            T.Check((string)first["expected"] == "1.3.0" && (bool)first["reload"] && !(bool)second["reload"], "the ping answers with the packaged version and a reload request only once");
            reply(new { v = 1, action = "ping" }); reply(new { v = 1, action = "ping", ext = "1.2; drop" }); reply(new { v = 1, action = "queue" });
            int opened = 0; bool exists = true;
            api.OpenDownloads = () => { opened++; return exists; };
            var caps = ((System.Collections.IEnumerable)((Dictionary<string, object>)reply(new { v = 1, action = "ping" })["data"])["capabilities"]).Cast<object>().ToList();
            var ok = reply(new { v = 1, action = "folder", path = @"C:\Windows" });
            exists = false;
            var missing = reply(new { v = 1, action = "folder" });
            T.Check(caps.Contains("open-folder") && (bool)ok["ok"] && !(bool)missing["ok"] && (string)((Dictionary<string, object>)missing["error"])["code"] == "FOLDER_MISSING" && opened == 2, "the extension can open Flow's download folder; a path it sends is ignored, a missing folder is explained");
            seen.RemoveRange(seen.Count - 3, 3);
            T.Check(seen.SequenceEqual(new[] { "True:1.2.0", "True:1.2.0", "True:null", "True:null", "False:null" }), "every call counts as seen; only a ping carries a version, and a malformed one is dropped (" + string.Join(" ", seen) + ")");
        }

        static void SettingsKeep() {
            var paths = new AppPaths { DataDir = "C:\\d", LegacyDir = "C:\\l", DefaultDownloads = "C:\\dl" };
            var fresh = SettingsRules.Normalize(new Dictionary<string, object>(), paths);
            T.Check(fresh.CheckUpdates && fresh.UpdateVersion == null, "new installs check for releases by default");
            var raw = new Dictionary<string, object> { { "CheckUpdates", false }, { "UpdateVersion", "2.9" }, { "UpdateUrl", "https://evil.example/" }, { "BrowserSeenVersion", "<x>" }, { "BrowserSeenAt", "yesterday" } };
            var kept = SettingsRules.Normalize(raw, paths);
            T.Check(!kept.CheckUpdates && kept.UpdateVersion == "2.9" && kept.UpdateUrl == null && kept.BrowserSeenVersion == null && kept.BrowserSeenAt == null, "a turned-off check stays off; stored values that are not versions, dates or Flow pages are dropped");
        }
    }

    public static partial class UiTests {
        static void LinkUi() {
            var paths = NewPaths("ui-link");
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = "light";
            Store.SaveSettings(paths, s);
            var c = Open(paths, 1180, 900);
            var w = c.Window;
            c.ChromeLocator = () => null;   // never start the user's Chrome
            c.PackageRoot = LinkUnit.Package("1.2.0");
            var registry = (MemoryHostRegistry)c.HostRegistry;
            c.RefreshBrowserLink(); Pump();

            // The extension's folder button opens only the folder in Flow's settings.
            var opened = new List<string>();
            c.FolderOpener = path => { opened.Add(path); return true; };
            var downloads = Path.Combine(paths.DefaultDownloads);
            Directory.CreateDirectory(downloads);
            T.Check(c.OpenDownloadFolder() && opened.SequenceEqual(new[] { downloads }), "the download folder from the settings is opened");
            Directory.Delete(downloads);
            T.Check(!c.OpenDownloadFolder() && opened.Count == 1, "a folder that does not exist yet is not opened");

            // Rail: the extension line is always there; it leads to the Browser page.
            var dot = Find<Rectangle>(w, "BrowserDot");
            T.Check(Find<Button>(w, "BrowserLink").IsVisible && Find<TextBlock>(w, "BrowserLinkText").Text == Loc.T("br.rail.NotInstalled") && ((SolidColorBrush)dot.Fill).Color.A == 0, "the rail says the extension is not set up, with an empty square");
            Find<Button>(w, "BrowserLink").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            T.Check(c.CurrentPage == "browser" && Find<RadioButton>(w, "NavBrowser").IsChecked == true && Find<TextBlock>(w, "HeaderEyebrow").Text == Loc.T("br.title") && Find<ScrollViewer>(w, "BrowserPage").IsVisible, "the rail line and the menu open the Browser page");
            T.Check(Find<TextBlock>(w, "BrowserStateTitle").Text == Loc.T("br.state.NotInstalled") && (string)Find<Button>(w, "BrowserInstallButton").Content == Loc.T("br.install") && !Find<Button>(w, "BrowserReloadButton").IsEnabled, "the page explains what is missing and offers setup");
            T.Check(Find<Button>(w, "BrowserInstallButton").Style == w.FindResource("Primary"), "setup is the page's filled button while it is needed");

            // One click sets up the connection for this package.
            Find<Button>(w, "BrowserInstallButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            T.Check(registry.Value == BrowserLink.ManifestFile(c.PackageRoot) && File.Exists(BrowserLink.ManifestFile(c.PackageRoot)) && c.BrowserState == LinkState.Waiting && c.BrowserActionValue == Loc.T("br.install.done"), "one-click setup registers this folder and waits for the extension");
            T.Check(Find<Button>(w, "BrowserRemoveButton").IsVisible && (string)Find<Button>(w, "BrowserInstallButton").Content == Loc.T("br.install.again"), "once set up, it can be set up again or removed");
            T.Check(Find<Button>(w, "BrowserInstallButton").Style != w.FindResource("Primary"), "a working setup no longer takes the page's filled button");

            // The extension calls in.
            c.BrowserSeen(true, "1.2.0"); Pump();
            T.Check(c.BrowserState == LinkState.Connected && Find<TextBlock>(w, "BrowserLinkText").Text == Loc.T("br.rail.Connected") && ((SolidColorBrush)dot.Fill).Color.A == 255, "a call from the extension fills the square: connected");
            T.Check(Find<TextBlock>(w, "BrowserSeenText").Text.Contains("1.2.0") && Find<TextBlock>(w, "BrowserExpectedText").Text == "1.2.0" && Find<TextBlock>(w, "BrowserUpdateText").Text == Loc.F("br.update.current", "1.2.0"), "the page shows the extension's version and that it is current");
            var stored = Store.Load(paths).Settings;
            T.Check(stored.BrowserSeenVersion == "1.2.0" && Iso.Parse(stored.BrowserSeenAt).HasValue, "the last contact is kept for the next start");

            // Silence for a few minutes.
            var real = c.UtcToday;
            c.UtcToday = () => DateTime.UtcNow.AddMinutes(10); c.RefreshBrowserLink(); Pump();
            T.Check(c.BrowserState == LinkState.Away && ((SolidColorBrush)dot.Fill).Color.A == 0, "after a few minutes without a call it is shown as away");
            c.UtcToday = real;

            // An older extension: one click asks it to reload from the folder, once.
            c.BrowserSeen(true, "1.1.5"); Pump();
            T.Check(c.BrowserState == LinkState.Outdated && Find<TextBlock>(w, "BrowserLinkText").Text == Loc.T("br.rail.Outdated") && Find<Button>(w, "BrowserReloadButton").IsEnabled, "an older extension is called out on the rail and can be updated");
            T.Check(!c.TakeReload(), "nothing is sent before the user asks");
            Find<Button>(w, "BrowserReloadButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            T.Check(c.TakeReload() && !c.TakeReload() && Find<TextBlock>(w, "BrowserUpdateText").Text == Loc.T("br.update.sent"), "the update is handed to the extension's next call, once");
            c.BrowserSeen(true, "1.2.0"); Pump();
            T.Check(c.BrowserState == LinkState.Connected, "after the reload it reports the new version and is current");

            // An extension from before 1.2.0 cannot reload on request: the user is told what to press.
            c.BrowserSeen(true, null); Pump();
            Find<Button>(w, "BrowserReloadButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            T.Check(c.BrowserState == LinkState.Outdated && !c.TakeReload() && Find<TextBlock>(w, "BrowserUpdateText").Text == Loc.F("br.update.legacy", "1.2.0") && c.BrowserActionValue == Loc.T("br.chrome.missing"), "an old extension gets manual steps; with no Chrome found the user is told");
            c.BrowserSeen(true, "1.2.0"); Pump();
            UiShot(w, "browser-page-light");

            // A new Flow release: the rail notice.
            var feed = new FakeFeed { Release = new AppRelease { Version = "9.9", Url = "https://github.com/MertKocakaplan/Flow/releases/tag/v9.9" } };
            c.ReleaseFeed = feed;
            T.Check(!c.UpdateNoticeShown, "no notice before a newer release is known");
            var check = c.CheckForUpdateAsync(true); Wait(() => check.IsCompleted, 5000);
            T.Check(c.UpdateNoticeShown && Find<TextBlock>(w, "UpdateNoticeTitle").Text == Loc.F("update.rail", "9.9") && c.UpdateStatus == Loc.F("update.available", "9.9"), "a newer release shows a notice on the rail");
            T.Check(Store.Load(paths).Settings.UpdateVersion == "9.9", "the known release is kept, so the notice is back after a restart without asking again");
            c.ShowPage("download"); Pump(); UiShot(w, "rail-update-light");
            feed.Release = new AppRelease { Version = Flow.Program.Version, Url = AppUpdates.ReleasesPage };
            check = c.CheckForUpdateAsync(true); Wait(() => check.IsCompleted, 5000);
            T.Check(!c.UpdateNoticeShown && c.UpdateStatus == Loc.F("update.current", Flow.Program.Version), "the same version hides the notice and says Flow is current");
            feed.Release = null;
            check = c.CheckForUpdateAsync(true); Wait(() => check.IsCompleted, 5000);
            T.Check(!c.UpdateNoticeShown && c.UpdateStatus == Loc.F("update.noRelease", Flow.Program.Version), "with no release published yet, it says so");
            feed.Fail = true;
            check = c.CheckForUpdateAsync(true); Wait(() => check.IsCompleted, 5000);
            T.Check(!c.UpdateNoticeShown && c.UpdateStatus == Loc.T("update.failed"), "an offline check fails quietly with a short note");

            // Compact rail: only the square and the icon stay.
            Resize(w, 800, 640);
            T.Check(Find<TextBlock>(w, "BrowserLinkText").Visibility == Visibility.Collapsed && Find<RadioButton>(w, "NavBrowser").IsVisible, "the narrow rail keeps the square and the menu icon");
            Resize(w, 1180, 900);

            // Dark theme and English.
            Theme.Set("dark"); Loc.Set("en"); feed.Fail = false; feed.Release = new AppRelease { Version = "9.9", Url = AppUpdates.ReleasesPage };
            check = c.CheckForUpdateAsync(true); Wait(() => check.IsCompleted, 5000);
            c.ShowPage("browser"); Pump();
            T.Check(Find<TextBlock>(w, "BrowserLinkText").Text == "Extension connected" && Find<TextBlock>(w, "UpdateNoticeTitle").Text == "New version: Flow 9.9", "texts follow the language");
            UiShot(w, "browser-page-dark-en");
            Theme.Set("light"); Loc.Set("tr");
            w.Close(); Pump();
        }
        static void UiShot(Window w, string name) { try { Render(w, name); } catch (Exception) {} }
    }
}
