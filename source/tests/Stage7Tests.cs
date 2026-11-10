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
            VersionContract();
            Updater();
        }

        sealed class FakeSource : IEngineSource {
            public string Tag, Exe, Sums; public int Downloads;
            public Task<EngineRelease> Latest(CancellationToken token) { return Task.FromResult(new EngineRelease { Tag = Tag, ExeUrl = "fake://exe", SumsUrl = "fake://sums" }); }
            public Task Download(string url, string target, long maxBytes, CancellationToken token) {
                Downloads++;
                if (url == "fake://sums") File.WriteAllText(target, Sums ?? ""); else File.Copy(Exe, target, true);
                return Task.FromResult(0);
            }
        }

        // The engine update is a download checked against the release's SHA-256 list, never the old file's self-updater.
        static void Updater() {
            T.Check(Components.IsNewer("2026.10.26", "2026.08.19") && !Components.IsNewer("2026.08.19", "2026.10.26") && !Components.IsNewer("2026.10.26", "2026.10.26") && Components.IsNewer("2026.10.26.123456", "2026.10.26") && !Components.IsNewer("latest", "2026.10.26") && Components.IsNewer("2026.10.26", null), "engine release numbers compared as dates");
            var a = new string('a', 64); var b = new string('B', 64);
            T.Check(Components.ExpectedHash(a + "  yt-dlp_x86.exe\n" + b + "  yt-dlp.exe\r\n", "yt-dlp.exe") == b.ToLowerInvariant() && Components.ExpectedHash(a + "  yt-dlp.exe.zip\n", "yt-dlp.exe") == null && Components.ExpectedHash("nonsense", "yt-dlp.exe") == null, "the release checksum list is read for the exact file name");

            T.Check(Components.ToolKind("C:\\t\\backup\\yt-dlp-2026.08.19.exe") == "yt-dlp.exe" && Components.ToolKind("yt-dlp.exe") == "yt-dlp.exe" && Components.ToolKind("ffprobe.exe") == "ffprobe.exe" && Components.ToolKind("other.exe") == "other.exe", "a kept engine copy is still recognised as the engine");
            var keptCopy = Path.Combine(T.Scratch("kept-engine"), "yt-dlp-2026.01.01.exe");
            File.Copy(Path.Combine(Engine.ToolsDir, "yt-dlp.exe"), keptCopy);
            T.Check(Task.Run(() => Components.RunVersion(keptCopy, CancellationToken.None)).GetAwaiter().GetResult() != null, "the version of a kept engine copy can be read, so it can be restored");
            var nov9 = new DateTime(2026, 11, 9, 23, 0, 0, DateTimeKind.Utc);
            T.Check(Components.EngineAgeDays("2026.10.26", nov9) == 14 && Components.EngineAgeDays("2026.08.19.123456", nov9) == 82 && Components.EngineAgeDays(null, nov9) == -1 && Components.EngineAgeDays("garbage", nov9) == -1, "engine age read from its release date");
            T.Check(Components.SuggestUpdate(ErrorCode.Unknown, "2026.08.19", nov9) && Components.SuggestUpdate(ErrorCode.Forbidden, "2026.08.19", nov9), "an engine-shaped error with an old engine suggests updating");
            T.Check(!Components.SuggestUpdate(ErrorCode.Unknown, "2026.10.26", nov9) && !Components.SuggestUpdate(ErrorCode.Unavailable, "2026.08.19", nov9) && !Components.SuggestUpdate(ErrorCode.DiskFull, "2026.08.19", nov9) && !Components.SuggestUpdate(ErrorCode.Unknown, null, nov9), "a recent engine, an unrelated error or an unknown version suggests nothing");

            var current = Path.Combine(Engine.ToolsDir, "yt-dlp.exe");
            var real = Task.Run(() => Components.RunVersion(current, CancellationToken.None)).GetAwaiter().GetResult();
            var tools = T.Scratch("tools-update");
            var exe = Path.Combine(tools, "yt-dlp.exe");
            File.Copy(current, exe);
            var original = Components.Hash(exe);
            Func<string, CancellationToken, Task<bool>> ok = (e, t) => Task.FromResult(true);
            Func<IEngineSource, UpdateResult> update = s => Task.Run(() => Components.UpdateEngine(tools, () => 0, ok, null, s, CancellationToken.None)).GetAwaiter().GetResult();

            var same = new FakeSource { Tag = real };
            var r = update(same);
            T.Check(!r.Changed && !r.RolledBack && r.NewVersion == real && same.Downloads == 0 && Components.Hash(exe) == original, "an engine already on the latest release is not downloaded again");

            var bad = new FakeSource { Tag = "2099.01.01", Exe = current, Sums = new string('0', 64) + "  yt-dlp.exe\n" };
            T.Rejects(() => update(bad), ErrorCode.EngineUpdateFailed, "a download that does not match the published checksum is refused");
            T.Check(Components.Hash(exe) == original && !Directory.Exists(Path.Combine(tools, "update")) && Components.Installed(tools) == null, "after a checksum mismatch nothing is replaced and the staged file is gone");

            var noLine = new FakeSource { Tag = "2099.01.01", Exe = current, Sums = original + "  yt-dlp_linux\n" };
            T.Rejects(() => update(noLine), ErrorCode.EngineUpdateFailed, "a checksum list without the Windows engine is refused");
            T.Check(noLine.Downloads == 1, "the engine itself is not downloaded when its checksum is not published");

            var wrong = new FakeSource { Tag = "2099.01.01", Exe = current, Sums = original + "  yt-dlp.exe\n" };
            var w = update(wrong);
            T.Check(w.RolledBack && w.NewVersion == real && Components.Hash(exe) == original && Components.Installed(tools) == null, "a verified file that is not the announced release is rolled back");
            T.Rejects(() => Task.Run(() => Components.UpdateEngine(tools, () => 1, ok, null, same, CancellationToken.None)).GetAwaiter().GetResult(), ErrorCode.UpdateBusy, "no engine update while downloads are active");
        }

        // One release number: the file the browser host starts, the Windows manifest, the component list and the setup page agree.
        static void VersionContract() {
            var version = Flow.Program.Version;
            var source = Path.Combine(Engine.BaseDir, "source");
            T.Check(Flow.BrowserIntegration.BrowserProtocol.ApplicationFile == "Flow-" + version + ".exe", "the browser host starts Flow-" + version + ".exe");
            T.Check(File.ReadAllText(Path.Combine(source, "app.manifest")).Contains("version=\"" + version + ".0.0\""), "app.manifest carries version " + version);
            T.Check(Components.Load(Engine.ToolsDir).Package == "Flow " + version, "components.json names package Flow " + version);
            var setup = Path.Combine(Engine.BaseDir, "chrome-extension", "setup.html");
            T.Check(!File.Exists(setup) || File.ReadAllText(setup).Contains(Flow.BrowserIntegration.BrowserProtocol.ApplicationFile), "the extension setup page names the versioned file");
            var stamp = typeof(Flow.Program).Assembly.GetName().Version;
            T.Check(stamp.ToString() == version + ".0.0", "assembly version follows the release number (" + stamp + ")");
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

            // Engine update on a copy of the tools folder: never on the real package. An older engine kept as a backup
            // makes the copy out of date, so the real release download and its checksum are exercised.
            var tools = Path.Combine(root, "tools-copy");
            Directory.CreateDirectory(tools);
            var backups = Components.BackupDir(Engine.ToolsDir);
            var older = Directory.Exists(backups) ? Directory.GetFiles(backups, "yt-dlp-*.exe").OrderBy(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault() : null;
            var copied = Path.Combine(tools, "yt-dlp.exe");
            File.Copy(older ?? Path.Combine(Engine.ToolsDir, "yt-dlp.exe"), copied);
            var original = Components.Hash(copied);
            Func<string, CancellationToken, Task<bool>> ok = (exe, t) => Task.FromResult(true);
            var result = Task.Run(() => Components.UpdateEngine(tools, () => 0, ok, l => {}, CancellationToken.None)).GetAwaiter().GetResult();
            Note("Engine update on copy: " + result.OldVersion + " -> " + result.NewVersion + " changed=" + result.Changed + " rolledBack=" + result.RolledBack + (older == null ? " (no older engine to start from)" : ""));
            T.Check(!result.RolledBack && result.NewVersion != null && (older == null || result.Changed), "live: engine update downloads the published release and checks its SHA-256");
            if (result.Changed) {
                var installed = Components.Installed(tools);
                T.Check(Components.HasBackup(tools) && installed != null && installed.Version == result.NewVersion && installed.Sha256 == Components.Hash(copied), "live: the installed engine is recorded with its checksum and the previous one is kept");
                var restored = Task.Run(() => Components.Rollback(tools, CancellationToken.None)).GetAwaiter().GetResult();
                T.Check(restored == result.OldVersion && Components.Hash(copied) == original, "live: previous engine restored byte for byte");
                // A new engine that breaks a previously working reference check is rolled back.
                int calls = 0;
                Func<string, CancellationToken, Task<bool>> breaks = (exe, t) => Task.FromResult(calls++ == 0);
                var broken = Task.Run(() => Components.UpdateEngine(tools, () => 0, breaks, l => {}, CancellationToken.None)).GetAwaiter().GetResult();
                T.Check(broken.RolledBack && Components.Hash(copied) == original && broken.NewVersion == broken.OldVersion, "live: failed post-update check restores the previous engine");
            }
            T.Rejects(() => Task.Run(() => Components.UpdateEngine(tools, () => 1, ok, l => {}, CancellationToken.None)).GetAwaiter().GetResult(), ErrorCode.UpdateBusy, "live: no engine update while downloads are active");
            T.Check(Components.Hash(Path.Combine(Engine.ToolsDir, "yt-dlp.exe")) == Components.Load(Engine.ToolsDir).Components[0].Sha256, "live: the packaged engine was not modified by the tests");
        }
    }
}
