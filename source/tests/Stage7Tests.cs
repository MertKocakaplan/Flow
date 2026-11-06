using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Flow;

namespace FlowTests {
    public static class Stage7Unit {
        public static void Run() {
            T.Check(Components.ParseVersion("yt-dlp.exe", "2026.08.19\n") == "2026.08.19" && Components.ParseVersion("yt-dlp.exe", "2026.09.27.232812\n") == "2026.09.27.232812", "engine version read (stable and nightly forms)");
            T.Check(Components.ParseVersion("ffmpeg.exe", "ffmpeg version 9.0.2-essentials_build-www.gyan.dev Copyright (c) 2000-2026") == "9.0.2-essentials_build-www.gyan.dev" && Components.ParseVersion("deno.exe", "deno 2.9.7 (stable, release, x86_64-pc-windows-msvc)\nv8 14") == "2.9.7", "FFmpeg and Deno versions read");
            T.Check(Components.ParseVersion("yt-dlp.exe", "") == null && Components.ParseVersion("yt-dlp.exe", "Traceback (most recent call last)") == null, "broken output is not taken as a version");
            var manifest = Components.Load(Engine.ToolsDir);
            T.Check(manifest.Components.Count == 4 && manifest.Components.All(c => c.Sha256 != null && c.Sha256.Length == 64 && c.License != null && File.Exists(Path.Combine(Engine.ToolsDir, c.License))), "component manifest: hashes and license files for every tool");
            var rows = Task.Run(() => Components.Check(Engine.ToolsDir, CancellationToken.None)).GetAwaiter().GetResult();
            T.Check(rows.Count == 4 && rows.All(r => r.State == "verified" && r.Runs), "every packaged tool runs and matches the manifest (" + string.Join(", ", rows.Select(r => r.Name + "=" + r.State + " " + r.Version)) + ")");
            var empty = T.Scratch("tools-empty");
            var missing = Task.Run(() => Components.Check(empty, CancellationToken.None)).GetAwaiter().GetResult();
            T.Check(missing.All(r => r.State == "missing"), "missing tools reported as missing");
            var list = Sites.ParseExtractors("20min (CURRENTLY BROKEN)\nBandcamp\nBandcamp:album\ntiktok:sound (CURRENTLY BROKEN)\nvimeo\n");
            T.Check(list.Count == 5 && list.Count(x => x.Broken) == 2 && list[0].Name == "20min", "engine extractor list parsed with broken flags");
            T.Check(Sites.Search(list, "band").Count() == 2 && Sites.Search(list, "").Count() == 5, "extractor search");
            T.Check(Sites.Known.Any(s => s.Name == "YouTube" && s.Status == "verified") && Sites.Known.All(s => Loc.Has("site." + s.Status)), "verified-site table has a status text for every entry");
        }
    }

    public static partial class UiTests {
        public static void Stage7Ui() {
            var paths = NewPaths("ui-components");
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = "light";
            Store.SaveSettings(paths, s);
            var c = Open(paths, 1180, 1100, new FakeExecutor());
            var w = c.Window;
            Find<RadioButton>(w, "TabOther").IsChecked = true; Pump();
            var support = Find<Expander>(w, "SupportExpander");
            T.Check(support.IsVisible && Find<ItemsControl>(w, "KnownSites").Items.Count == Sites.Known.Length, "other-sites tab shows the tried-site table");
            support.IsExpanded = true;
            Wait(() => Find<ListBox>(w, "SiteList").Items.Count > 0, 60000);
            T.Check(Find<ListBox>(w, "SiteList").Items.Count > 300 && Find<TextBlock>(w, "SiteCount").Text.Contains("çıkarıcı"), "engine extractor list loaded and counted as extractors, not sites");
            Find<TextBox>(w, "SiteSearch").Text = "soundcloud"; Pump();
            T.Check(Find<ListBox>(w, "SiteList").Items.Count >= 3 && Find<ListBox>(w, "SiteList").Items.OfType<ExtractorRow>().All(r => r.Name.ToLowerInvariant().Contains("soundcloud")), "extractor search narrows the list");
            Find<TextBox>(w, "SiteSearch").Text = "tiktok:sound"; Pump();
            T.Check(Find<ListBox>(w, "SiteList").Items.OfType<ExtractorRow>().Any(r => r.Broken && r.ToString().Contains(Loc.T("site.broken"))), "extractors the engine marks as broken are labelled");
            Console.WriteLine("SCREEN " + Render(w, "stage7-other-sites"));
            Find<RadioButton>(w, "TabYouTube").IsChecked = true; Pump();
            T.Check(!support.IsVisible, "site table only on the other-sites tab");
            c.ShowPage("settings"); Pump();
            Wait(() => c.ComponentRows.Count == 4, 60000);
            T.Check(c.ComponentRows.All(r => r.State == "verified") && c.ComponentRows.First(r => r.Name == "yt-dlp").Detail == Loc.T("comp.updatable"), "components card: versions verified, only the engine is updatable here");
            var diag = c.Diagnostics();
            T.Check(diag.Contains("yt-dlp: 2026") && diag.Contains("Queue:") && !diag.Contains("\\" + Environment.UserName + "\\"), "diagnostics include versions and mask the user name");
            var names = Visuals(Find<ItemsControl>(w, "KnownSites")).OfType<TextBlock>().Select(t => t.Text).ToList();
            T.Check(names.Contains("YouTube") && names.Contains("Dailymotion"), "site names visible in the table");
            Find<ItemsControl>(w, "ComponentList").BringIntoView(); Pump();
            Console.WriteLine("SCREEN " + Render(w, "stage7-settings-components"));
            w.Close();
        }
    }

    public static partial class LiveTests {
        public static void Stage7Live(string root) {
            // Broad site check through Flow's own pipeline (metadata, selection, download, ffprobe).
            var q = NewQueue(root, "data7");
            var sites = new[] {
                new[] { "TikTok (1)", "https://www.tiktok.com/@patroxofficial/video/6742501081818877190", "video" },
                new[] { "TikTok (2)", "https://www.tiktok.com/@leenabhushan/video/6748451240264420610", "video" },
                new[] { "Dailymotion", "https://www.dailymotion.com/video/x5kesuj", "video" },
                new[] { "Reddit", "https://www.reddit.com/r/videos/comments/6rrwyj/that_small_heart_attack/", "video" },
                new[] { "Twitch clip", "https://clips.twitch.tv/FaintLightGullWholeWheat", "video" },
                new[] { "SoundCloud", "https://soundcloud.com/ethmusic/lostin-powers-she-so-heavy", "audio" },
                new[] { "Bandcamp", "https://youtube-dl.bandcamp.com/track/youtube-dl-test-song", "audio" },
                new[] { "Vimeo", "https://vimeo.com/76979871", "video" },
                new[] { "X", "https://x.com/freethenipple/status/643211948184596480", "video" }
            };
            foreach (var site in sites) {
                var o = site[2] == "audio" ? Media.Opt("mp3", "mp3", 0, "auto") : Media.Opt("compatible", "mp4", 360, "h264");
                o.Folder = Path.Combine(root, "sites"); o.Strict = false;
                var item = RunJob(q, site[1], o, 300);
                string status;
                if (item.Job.State == JobState.Completed) {
                    var p = Probe(item.Job.OutputPath);
                    status = "DOWNLOADED " + (p.HasVideo ? p.Width + "x" + p.Height + " " + p.VCodec + "/" : "") + (p.HasAudio ? p.ACodec : "no-audio") + " " + Math.Round(p.Duration, 1) + "s";
                } else status = "NOT-DOWNLOADED " + item.Job.State + " " + item.Job.Error;
                Note("SITE " + site[0] + " | " + status);
            }
            q.Shutdown();

            // Engine update on a copy of the tools folder: never on the real package.
            var tools = Path.Combine(root, "tools-copy");
            Directory.CreateDirectory(tools);
            File.Copy(Path.Combine(Engine.ToolsDir, "yt-dlp.exe"), Path.Combine(tools, "yt-dlp.exe"));
            var original = Components.Hash(Path.Combine(tools, "yt-dlp.exe"));
            Func<string, CancellationToken, Task<bool>> ok = (exe, t) => Task.FromResult(true);
            var result = Task.Run(() => Components.UpdateEngine(tools, () => 0, ok, l => {}, CancellationToken.None)).GetAwaiter().GetResult();
            Note("Engine update on copy: " + result.OldVersion + " -> " + result.NewVersion + " changed=" + result.Changed + " rolledBack=" + result.RolledBack);
            T.Check(!result.RolledBack && result.NewVersion != null && (result.Changed ? Components.HasBackup(tools) : true), "live: engine update on a copy runs the official updater and keeps a backup when changed");
            if (result.Changed) {
                var restored = Task.Run(() => Components.Rollback(tools, CancellationToken.None)).GetAwaiter().GetResult();
                T.Check(restored == result.OldVersion && Components.Hash(Path.Combine(tools, "yt-dlp.exe")) == original, "live: previous engine restored byte for byte");
            }
            // A new engine that breaks a previously working reference check is rolled back.
            int calls = 0;
            Func<string, CancellationToken, Task<bool>> breaks = (exe, t) => Task.FromResult(calls++ == 0);
            var broken = Task.Run(() => Components.UpdateEngine(tools, () => 0, breaks, l => {}, CancellationToken.None)).GetAwaiter().GetResult();
            T.Check(broken.RolledBack && Components.Hash(Path.Combine(tools, "yt-dlp.exe")) == original && broken.NewVersion == broken.OldVersion, "live: failed post-update check restores the previous engine");
            T.Rejects(() => Task.Run(() => Components.UpdateEngine(tools, () => 1, ok, l => {}, CancellationToken.None)).GetAwaiter().GetResult(), ErrorCode.UpdateBusy, "live: no engine update while downloads are active");
            T.Check(Components.Hash(Path.Combine(Engine.ToolsDir, "yt-dlp.exe")) == Components.Load(Engine.ToolsDir).Components[0].Sha256, "live: the packaged engine was not modified by the tests");
        }
    }
}
