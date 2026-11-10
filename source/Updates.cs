using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Flow {
    public sealed class AppRelease { public string Version, Url; }
    public interface IAppReleaseFeed { Task<AppRelease> Latest(CancellationToken token); }

    // Flow's own releases on GitHub. Only the newest published release is read; nothing about the user is sent.
    public sealed class GitHubAppFeed : IAppReleaseFeed {
        const string Api = "https://api.github.com/repos/MertKocakaplan/Flow/releases/latest";
        public async Task<AppRelease> Latest(CancellationToken token) {
            var request = (HttpWebRequest)WebRequest.Create(Api);
            request.UserAgent = "Flow/" + Program.Version; request.Accept = "application/vnd.github+json";
            request.Timeout = 20000; request.ReadWriteTimeout = 20000; request.Credentials = null; request.UseDefaultCredentials = false;
            string body;
            using (token.Register(request.Abort)) {
                try {
                    using (var response = (HttpWebResponse)await request.GetResponseAsync())
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) {
                        var buffer = new char[256 * 1024];
                        int read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                        body = new string(buffer, 0, read);
                    }
                } catch (WebException ex) {
                    var http = ex.Response as HttpWebResponse;
                    // No release published yet.
                    if (http != null && http.StatusCode == HttpStatusCode.NotFound) return null;
                    throw;
                }
            }
            return AppUpdates.ParseRelease(body);
        }
    }

    public static class AppUpdates {
        public const string ReleasesPage = "https://github.com/MertKocakaplan/Flow/releases";
        public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

        // "v2.3", "2.3" and "2.3.0" are the same release; anything else is not a version.
        public static Version Parse(string value) {
            var text = (value ?? "").Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            if (!Regex.IsMatch(text, @"^\d{1,5}(\.\d{1,5}){0,3}$")) return null;
            var parts = text.Split('.');
            int[] n = new int[4];
            for (int i = 0; i < parts.Length; i++) n[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
            return new Version(n[0], n[1], n[2], n[3]);
        }
        public static bool IsNewer(string candidate, string current) {
            var a = Parse(candidate); var b = Parse(current);
            return a != null && b != null && a > b;
        }
        public static string Clean(string value) { var v = Parse(value); return v == null ? null : (value ?? "").Trim().TrimStart('v', 'V'); }
        // The page that is opened must be one of Flow's own release pages.
        public static bool IsReleasePage(string url) {
            Uri uri;
            return url != null && Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/MertKocakaplan/Flow/releases", StringComparison.Ordinal);
        }
        public static AppRelease ParseRelease(string json) {
            Dictionary<string, object> data;
            try { data = Engine.Json().DeserializeObject(json ?? "") as Dictionary<string, object>; } catch (ArgumentException) { return null; } catch (InvalidOperationException) { return null; }
            if (data == null || Engine.Flag(data, "draft") || Engine.Flag(data, "prerelease")) return null;
            var version = Clean(Engine.Text(data, "tag_name", null));
            if (version == null) return null;
            var url = Engine.Text(data, "html_url", null);
            return new AppRelease { Version = version, Url = IsReleasePage(url) ? url : ReleasesPage };
        }
        public static bool Due(string checkedAt, DateTime nowUtc) {
            var last = Iso.Parse(checkedAt);
            return !last.HasValue || nowUtc - last.Value >= Interval || last.Value > nowUtc.AddMinutes(5);
        }
    }

    // The rail's "new version" notice and the check behind it.
    public partial class Controller {
        internal IAppReleaseFeed ReleaseFeed = ShellIntegration ? new GitHubAppFeed() : null;
        DispatcherTimer updateTimer;
        bool updateChecking;
        internal string UpdateStatus { get { return C<TextBlock>("UpdateStatusText").Text; } }

        void InitUpdates() {
            C<Button>("UpdateNotice").Click += (s, e) => OpenRelease();
            C<CheckBox>("CheckUpdatesBox").IsChecked = settings.CheckUpdates;
            C<CheckBox>("CheckUpdatesBox").Click += (s, e) => { settings.CheckUpdates = C<CheckBox>("CheckUpdatesBox").IsChecked == true; SaveSettings(); };
            C<Button>("CheckUpdatesButton").Click += async (s, e) => await CheckForUpdateAsync(true);
            ShowUpdateNotice();
            // The first check waits until Flow has settled; afterwards it runs at most once a day.
            updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            updateTimer.Tick += async (s, e) => {
                updateTimer.Interval = TimeSpan.FromHours(1);
                if (settings.CheckUpdates && AppUpdates.Due(settings.UpdateCheckedAt, UtcToday())) await CheckForUpdateAsync(false);
            };
            Window.Loaded += (s, e) => { if (ReleaseFeed != null) updateTimer.Start(); };
            Window.Closed += (s, e) => updateTimer.Stop();
        }

        internal async Task CheckForUpdateAsync(bool manual) {
            if (ReleaseFeed == null || updateChecking) return;
            updateChecking = true;
            var status = C<TextBlock>("UpdateStatusText");
            C<Button>("CheckUpdatesButton").IsEnabled = false;
            if (manual) status.Text = Loc.T("update.checking");
            try {
                var release = await ReleaseFeed.Latest(CancellationToken.None);
                settings.UpdateCheckedAt = Iso.From(UtcToday());
                bool newer = release != null && AppUpdates.IsNewer(release.Version, Program.Version);
                settings.UpdateVersion = newer ? release.Version : null;
                settings.UpdateUrl = newer ? release.Url : null;
                SaveSettings();
                status.Text = newer ? Loc.F("update.available", release.Version) : release == null ? Loc.F("update.noRelease", Program.Version) : Loc.F("update.current", Program.Version);
            } catch (Exception ex) {
                Log("Update check failed: " + ex.Message);
                if (manual) status.Text = Loc.T("update.failed");
            } finally {
                updateChecking = false;
                C<Button>("CheckUpdatesButton").IsEnabled = true;
                ShowUpdateNotice();
            }
        }

        void ShowUpdateNotice() {
            bool show = settings.UpdateVersion != null && AppUpdates.IsNewer(settings.UpdateVersion, Program.Version);
            C<Button>("UpdateNotice").Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) {
                C<TextBlock>("UpdateNoticeTitle").Text = Loc.F("update.rail", settings.UpdateVersion);
                System.Windows.Automation.AutomationProperties.SetName(C<Button>("UpdateNotice"), Loc.F("update.rail", settings.UpdateVersion) + ". " + Loc.T("update.rail.action"));
            }
        }
        public bool UpdateNoticeShown { get { return C<Button>("UpdateNotice").Visibility == Visibility.Visible; } }

        // Installing the new version from inside Flow comes later; for now the release page is opened.
        void OpenRelease() {
            var url = AppUpdates.IsReleasePage(settings.UpdateUrl) ? settings.UpdateUrl : AppUpdates.ReleasesPage;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { ShowMessage(ex.Message, true); }
        }
    }
}
