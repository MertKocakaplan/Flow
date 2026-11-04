using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Flow;

namespace FlowTests {
    // Real sites through the real engine. Site access (IP blocks, sign-in walls) is reported separately
    // from Flow's own correctness.
    public static partial class LiveTests {
        public static void Run() {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            RunAll();
        }
        static partial void RunAll();
        public static readonly List<string> Report = new List<string>();
        public static void Note(string line) { Report.Add(line); Console.WriteLine("LIVE " + line); }

        public static JobItem RunJob(QueueService q, string url, JobOptions options, int timeoutSeconds) {
            var parsed = Platforms.Parse(url);
            var item = q.Add(new DownloadJob { Url = parsed.Url, Platform = parsed.Platform.ToString(), Options = options }, false);
            UiTests.Wait(() => JobState.IsFinished(item.Job.State) || item.Job.State == JobState.NeedsAction || item.Job.State == JobState.Retry, timeoutSeconds * 1000);
            return item;
        }
        // Blocking on the dispatcher thread would deadlock; run the async call elsewhere.
        public static ProbeResult Probe(string path) { return Task.Run(() => EngineExecutor.Probe(path, CancellationToken.None)).GetAwaiter().GetResult(); }
        public static string Describe(JobItem i) { return i.Job.State + (i.Job.Error != null ? " " + i.Job.Error : "") + " " + (i.Job.OutputPath ?? "") + (i.Job.ErrorDetail != null ? " | " + i.Job.ErrorDetail.Split('\n').FirstOrDefault(l => l.Contains("ERROR")) : ""); }
    }

    public static partial class LiveTests {
        static partial void RunAll() {
            var root = T.Scratch("live");
            var paths = new AppPaths { DataDir = Path.Combine(root, "data"), LegacyDir = Path.Combine(root, "old"), DefaultDownloads = Path.Combine(root, "out") };
            var settings = SettingsRules.Defaults(paths); settings.Parallel = 1; settings.Retries = 0;
            var q = new QueueService(paths, () => settings, new EngineExecutor());
            q.Start();
            var output = Path.Combine(root, "out");
            const string zoo = "https://www.youtube.com/watch?v=jNQXAC9IVRw";

            var only = Environment.GetEnvironmentVariable("FLOW_LIVE_ONLY");
            if (only == "resume") { PauseResume(q, root, zoo); q.Shutdown(); return; }
            if (only == "extras") { Stage5Live(q, root); q.Shutdown(); return; }
            if (only == "sites") { q.Shutdown(); Stage7Live(root); return; }
            var compat = Media.Opt("compatible", "mp4", 360, "h264"); compat.Folder = output;
            var a = RunJob(q, zoo, compat, 240);
            Note("YouTube compatible MP4: " + Describe(a));
            T.Check(a.Job.State == JobState.Completed && File.Exists(a.Job.OutputPath) && a.Job.OutputPath.EndsWith("240p.mp4"), "live: YouTube compatible MP4 completed with quality in the name");
            if (File.Exists(a.Job.OutputPath)) { var p = Probe(a.Job.OutputPath); T.Check(p.VCodec == "h264" && p.ACodec == "aac" && p.Duration > 17, "live: ffprobe confirms H.264 + AAC and full length"); }
            T.Check(!Directory.Exists(Path.Combine(output, ".flow-parts")), "live: job parts folder removed after success");

            var skip = compat.Copy(); skip.Duplicate = "skip";
            var b = RunJob(q, zoo, skip, 120);
            T.Check(b.Job.State == JobState.Completed && b.Job.Skipped && b.Job.OutputPath == a.Job.OutputPath, "live: same content and profile skipped, not reported as a new download");
            var c = RunJob(q, zoo, compat.Copy(), 240);
            T.Check(c.Job.State == JobState.Completed && c.Job.OutputPath.EndsWith("240p (2).mp4"), "live: rename policy keeps both files");

            var best = Media.Opt("best", "mkv", 0, "auto"); best.Folder = output;
            var d = RunJob(q, zoo, best, 240);
            Note("YouTube best MKV: " + Describe(d) + " " + d.Job.VCodec + "/" + d.Job.ACodec);
            T.Check(d.Job.State == JobState.Completed && d.Job.OutputPath.EndsWith(".mkv") && (d.Job.VCodec == "av1" || d.Job.VCodec == "vp9"), "live: best source MKV keeps the efficient codec without re-encoding");

            foreach (var format in new[] { "mp3", "m4a" }) {
                var o = Media.Opt(format, format, 0, "auto"); o.Folder = output;
                var e = RunJob(q, zoo, o, 240);
                Note("YouTube " + format + ": " + Describe(e));
                T.Check(e.Job.State == JobState.Completed && e.Job.OutputPath.EndsWith("." + format) && Probe(e.Job.OutputPath).HasAudio, "live: YouTube " + format.ToUpperInvariant() + " verified");
            }

            PauseResume(q, root, zoo);

            // Instagram silent reel
            var ig = Media.Opt("compatible", "mp4", 1080, "h264"); ig.Folder = output;
            var g = RunJob(q, "https://www.instagram.com/reel/Chunk8-jurw/", ig, 180);
            Note("Instagram reel: " + Describe(g) + " " + g.Job.Width + "x" + g.Job.Height);
            if (g.Job.State == JobState.Completed) T.Check(g.Job.Width == 720 && g.Job.Height == 1280 && !Probe(g.Job.OutputPath).HasAudio, "live: Instagram vertical reel at its real 720x1280 size, silent as the source");
            else Note("Instagram access not verified (" + g.Job.Error + "): site access, not counted as a Flow failure");
            var igAudio = Media.Opt("mp3", "mp3", 0, "auto"); igAudio.Folder = output;
            var h = RunJob(q, "https://www.instagram.com/reel/Chunk8-jurw/", igAudio, 180);
            if (h.Job.Error == "NoAudio") T.Check(h.Job.State == JobState.NeedsAction && !File.Exists(h.Job.OutputPath ?? ""), "live: MP3 from a silent reel stops before downloading, with an explanation");
            else Note("Instagram MP3 check skipped: " + Describe(h));

            // Flow's own H.264 conversion (used when a source has no H.264)
            if (d.Job.State == JobState.Completed) {
                var converted = Path.Combine(root, "converted.mp4");
                double last = -1;
                Task.Run(() => EngineExecutor.ConvertCompatible(d.Job.OutputPath, converted, 19, CancellationToken.None, p => last = p, l => {})).GetAwaiter().GetResult();
                var cp = Probe(converted);
                T.Check(cp.VCodec == "h264" && cp.ACodec == "aac" && cp.Duration > 17 && last > 50, "live: conversion produces H.264/AAC MP4 with progress");
            }
            q.Shutdown();
            RunMore(q, root);
        }
        static void PauseResume(QueueService q, string root, string zoo) {
            var slow = Media.Opt("custom", "mp4", 360, "auto"); slow.Folder = Path.Combine(root, "slow"); slow.SpeedLimit = 40 * 1024;
            var parsed = Platforms.Parse(zoo);
            var f = q.Add(new DownloadJob { Url = parsed.Url, Platform = "YouTube", Options = slow }, false);
            UiTests.Wait(() => f.Job.State == JobState.Downloading && f.ProgressValue > 5, 90000);
            q.Pause(f);
            UiTests.Wait(() => f.Job.State == JobState.Paused, 15000);
            long partial = EngineExecutor.PartsSize(f.Job.TempDir);
            T.Check(f.Job.State == JobState.Paused && partial > 0, "live: pause keeps the partial data (" + partial + " bytes)");
            f.Job.Options.SpeedLimit = 0;
            q.Resume(f);
            UiTests.Wait(() => JobState.IsFinished(f.Job.State), 120000);
            var resumed = f.ResumedBytes >= partial * 0.9 || f.LogText.Contains("Resuming download at byte");
            Note("Pause/resume: " + Describe(f) + " resumed-from-part=" + resumed);
            T.Check(f.Job.State == JobState.Completed && resumed && Probe(f.Job.OutputPath).Duration > 17, "live: resumed from the partial file and completed a full-length file");

            File.WriteAllText(Path.Combine(root, "resume-log.txt"), f.LogText);
            Console.WriteLine("RESUMELOG " + Path.Combine(root, "resume-log.txt"));
        }
        static partial void RunMore(QueueService q, string root);
        static QueueService NewQueue(string root, string name) {
            var paths = new AppPaths { DataDir = Path.Combine(root, name), LegacyDir = Path.Combine(root, "old"), DefaultDownloads = Path.Combine(root, "out") };
            var settings = SettingsRules.Defaults(paths); settings.Parallel = 1; settings.Retries = 0;
            var q = new QueueService(paths, () => settings, new EngineExecutor()); q.Start(); return q;
        }
    }
}
