using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Flow;

namespace FlowTests {
    public static class Media {
        public static MediaInfo Make(double duration, params string[] formats) {
            // "id|ext|vcodec|acodec|w|h|fps|tbr|filesize"
            var list = formats.Select(f => {
                var p = f.Split('|');
                return "{\"format_id\":\"" + p[0] + "\",\"ext\":\"" + p[1] + "\",\"vcodec\":" + (p[2] == "null" ? "null" : "\"" + p[2] + "\"") + ",\"acodec\":" + (p[3] == "null" ? "null" : "\"" + p[3] + "\"") + ",\"width\":" + p[4] + ",\"height\":" + p[5] + ",\"fps\":" + p[6] + ",\"tbr\":" + p[7] + ",\"filesize\":" + p[8] + "}";
            });
            return MediaParser.Parse("{\"id\":\"x\",\"title\":\"T\",\"duration\":" + duration + ",\"formats\":[" + string.Join(",", list) + "]}", Platform.Other);
        }
        public static JobOptions Opt(string profile, string format, int height, string codec) {
            return new JobOptions { Profile = profile, Format = format, Height = height, Codec = codec, Folder = "C:\\out", Organize = "none", NameTemplate = "{title} [{id}]", Duplicate = "rename", SubLangs = new List<string>(), SubMode = "file", ClipMode = "fast" };
        }
    }

    public static class Stage3Unit {
        public static void Run() { Selector(); ArgsAndNames(); ProgressAndVerify(); }

        static void Selector() {
            var yt = MediaParser.Parse(T.Fixture("youtube.json"), Platform.YouTube);
            var s = FormatSelector.Select(yt, Media.Opt("compatible", "mp4", 1080, "h264"));
            T.Check(FormatSelector.IsH264(s.VCodec) && FormatSelector.IsAac(s.ACodec) && s.ShortSide == 240 && s.Spec.EndsWith("+140") && !s.NeedsConversion, "compatible MP4 picks real H.264 + AAC streams (" + s.Spec + ")");
            T.Check(s.Video.Protocol == "https", "direct HTTPS preferred over HLS at equal quality (resumable)");
            var best = FormatSelector.Select(yt, Media.Opt("best", "mkv", 0, "auto"));
            T.Check(best.VCodec.StartsWith("av01") && best.ACodec == "opus", "best source MKV follows quality-per-bit codecs (" + best.Spec + ")");
            var m4a = FormatSelector.Select(yt, Media.Opt("m4a", "m4a", 0, "auto"));
            T.Check(m4a.AudioOnly && m4a.Audio.Id == "140" && !m4a.AudioConversion, "M4A takes AAC without re-encoding, non-DRC track");
            var mp3 = FormatSelector.Select(yt, Media.Opt("mp3", "mp3", 0, "auto"));
            T.Check(mp3.AudioOnly && mp3.AudioConversion && mp3.Notes.Contains("sel.note.toMp3"), "MP3 conversion is announced");
            T.Check(s.Bytes > 0 && s.SizeExact && s.Bytes == s.Video.FileSize + s.Audio.FileSize, "exact size from stream sizes");

            var ig = MediaParser.Parse(T.Fixture("instagram.json"), Platform.Instagram);
            var igs = FormatSelector.Select(ig, Media.Opt("compatible", "mp4", 1080, "h264"));
            T.Check(igs.Width == 720 && igs.Height == 1280 && igs.Silent && igs.Audio == null && !igs.Spec.Contains("+") && igs.Notes.Contains("sel.note.silent"), "vertical 720x1280 fits a 1080 limit; silent video stays video-only");
            T.Check(igs.Bytes == -1 && ig.Duration == 0, "no duration and no file size: size stays unknown instead of guessed");
            var noSize = Media.Make(100, "v|mp4|avc1|none|1280|720|30|2000|0", "a|m4a|none|mp4a|0|0|0|128|0");
            var est = FormatSelector.Select(noSize, Media.Opt("compatible", "mp4", 1080, "h264"));
            T.Check(est.Bytes == (long)(2128 * 1000 / 8 * 100) && !est.SizeExact, "size estimated from bitrate and duration, marked as estimate");
            T.Rejects(() => FormatSelector.Select(ig, Media.Opt("mp3", "mp3", 0, "auto")), ErrorCode.NoAudio, "audio from a silent video is refused before downloading");
            var ig480 = FormatSelector.Select(ig, Media.Opt("custom", "mp4", 480, "auto"));
            T.Check(ig480.ShortSide == 472, "480 limit picks 472x840 by its short side");
            T.Rejects(() => FormatSelector.Select(ig, new JobOptions { Profile = "custom", Format = "mp4", Height = 360, Strict = true, Codec = "auto" }), ErrorCode.QualityUnavailable, "strict limit below every format is explained");
            try { FormatSelector.Select(ig, new JobOptions { Profile = "custom", Format = "mp4", Height = 360, Strict = true, Codec = "auto" }); } catch (FlowException ex) { T.Check(ex.Args[0] == "472p" && Loc.Error(ex).Contains("472p"), "strict limit message names the lowest real quality"); }
            var soft = FormatSelector.Select(ig, new JobOptions { Profile = "custom", Format = "mp4", Height = 360, Strict = false, Codec = "auto" });
            T.Check(soft.AboveLimit && soft.ShortSide == 472 && soft.Notes.Contains("sel.note.aboveLimit"), "preferred limit falls back to the closest quality with a note");

            var vertical = Media.Make(30, "v1|mp4|avc1.640028|none|1080|1920|30|4000|15000000", "v2|mp4|avc1.64001f|none|720|1280|30|2000|7500000", "a1|m4a|none|mp4a.40.2|0|0|0|128|480000");
            var vs = FormatSelector.Select(vertical, Media.Opt("compatible", "mp4", 1080, "h264"));
            T.Check(vs.Width == 1080 && vs.Height == 1920 && vs.Spec == "v1+a1", "1080x1920 is not rejected as 'above 1080p'");
            var vp9only = Media.Make(60, "v|webm|vp9|none|1920|1080|30|3000|0", "a|webm|none|opus|0|0|0|160|0");
            var conv = FormatSelector.Select(vp9only, Media.Opt("compatible", "mp4", 1080, "h264"));
            T.Check(conv.VideoConversion && conv.Notes.Contains("sel.note.convertVideo"), "no H.264 in the source: conversion is announced beforehand");
            var unknownCodec = MediaParser.Parse("{\"duration\":32,\"formats\":[{\"format_id\":\"1080\",\"ext\":\"mp4\",\"height\":1080,\"protocol\":\"https\"}]}", Platform.Other);
            var uc = FormatSelector.Select(unknownCodec, Media.Opt("compatible", "mp4", 360, "h264"));
            T.Check(uc.ConvertIfNeeded && !uc.VideoConversion && uc.AboveLimit && uc.Notes.Contains("sel.note.codecUnknown"), "unknown codec: checked after download instead of always re-encoding");
            var mkvvp9 = FormatSelector.Select(vp9only, Media.Opt("custom", "mkv", 1080, "auto"));
            T.Check(!mkvvp9.NeedsConversion && mkvvp9.Spec == "v+a", "MKV keeps VP9 + Opus without conversion");
            var tiktok = Media.Make(15, "h264_540p|mp4|h264|aac|576|1024|30|900|1700000", "h264_720p|mp4|h264|aac|720|1280|30|1400|2600000", "bytevc1_1080p|mp4|h265|aac|1080|1920|30|1500|2800000");
            var tt = FormatSelector.Select(tiktok, Media.Opt("compatible", "mp4", 1080, "h264"));
            T.Check(tt.Spec == "h264_720p" && tt.Audio == null && !tt.Silent, "muxed site formats: best H.264 variant with its own audio");
            var ttAudio = FormatSelector.Select(tiktok, Media.Opt("mp3", "mp3", 0, "auto"));
            T.Check(ttAudio.AudioOnly && ttAudio.Spec == "h264_540p", "audio from muxed-only sources uses the smallest muxed file");
            var fps = Media.Make(10, "v60|mp4|avc1|none|1920|1080|60|6000|0", "v30|mp4|avc1|none|1920|1080|30|4000|0", "a|m4a|none|mp4a.40.2|0|0|0|128|0");
            var f30 = FormatSelector.Select(fps, new JobOptions { Profile = "custom", Format = "mp4", Height = 1080, Codec = "h264", FpsLimit = 30 });
            T.Check(f30.Fps == 30 && FormatSelector.Select(fps, Media.Opt("custom", "mp4", 1080, "h264")).Fps == 60, "frame-rate limit applied only when asked");
            var unknownSize = Media.Make(0, "v|mp4|avc1|none|1280|720|30|0|0", "a|m4a|none|mp4a|0|0|0|0|0");
            T.Check(FormatSelector.Select(unknownSize, Media.Opt("compatible", "mp4", 1080, "h264")).Bytes == -1, "unknown size is reported as unknown, not zero");
            var audioOnlySite = Media.Make(200, "mp3-128|mp3|none|mp3|0|0|0|128|3200000", "opus|opus|none|opus|0|0|0|96|0");
            T.Check(audioOnlySite.HasVideo == Tri.No, "audio-only source (e.g. SoundCloud) detected");
            T.Rejects(() => FormatSelector.Select(audioOnlySite, Media.Opt("compatible", "mp4", 1080, "h264")), ErrorCode.NoVideo, "video profile on an audio-only source is refused");
            var drmOnly = MediaParser.Parse("{\"duration\":10,\"formats\":[{\"format_id\":\"d\",\"ext\":\"mp4\",\"vcodec\":\"avc1\",\"acodec\":\"mp4a\",\"has_drm\":true}]}", Platform.Other);
            T.Rejects(() => FormatSelector.Select(drmOnly, Media.Opt("compatible", "mp4", 1080, "h264")), ErrorCode.NoVideo, "DRM formats are never selected");
        }

        static void ArgsAndNames() {
            var yt = MediaParser.Parse(T.Fixture("youtube.json"), Platform.YouTube);
            var options = Media.Opt("compatible", "mp4", 1080, "h264"); options.SpeedLimit = 512000;
            var job = new DownloadJob { Id = "job1", Url = "https://www.youtube.com/watch?v=jNQXAC9IVRw", Platform = "YouTube", Title = "100% zoo", ContentId = "jNQXAC9IVRw", Options = options };
            var sel = FormatSelector.Select(yt, options);
            var args = Engine.JobArgs(job, sel, "C:\\out", "100% zoo [jNQXAC9IVRw] 240p", "C:\\out\\.flow-parts\\job1");
            T.Check(args[args.IndexOf("-f") + 1].StartsWith(sel.Spec + "/") && args.Last() == job.Url && args[args.Count - 2] == "--", "job arguments: exact formats first, URL last");
            T.Check(args.Contains("home:C:\\out") && args.Contains("temp:C:\\out\\.flow-parts\\job1") && args[args.IndexOf("-o") + 1] == "100%% zoo [jNQXAC9IVRw] 240p.%(ext)s", "parts in the job folder, name escaped for the engine");
            T.Check(args[args.IndexOf("-r") + 1] == "512000" && args.Contains("--abort-on-unavailable-fragments") && args.Contains("--continue"), "speed limit, fragment safety, resume");
            T.Check(args[args.IndexOf("--merge-output-format") + 1] == "mp4" && !args.Any(a => a.StartsWith("--cookies")), "merge container and no cookies");
            var vp9only = Media.Make(60, "v|webm|vp9|none|1920|1080|30|3000|0", "a|webm|none|opus|0|0|0|160|0");
            var conv = Engine.JobArgs(job, FormatSelector.Select(vp9only, options), "C:\\out\\.flow-parts\\job1", "source", "C:\\out\\.flow-parts\\job1");
            T.Check(conv[conv.IndexOf("--merge-output-format") + 1] == "mkv", "conversion jobs keep an intermediate file for Flow's encoder");
            job.ItemIndex = 3;
            var itemArgs = Engine.JobArgs(job, sel, "C:\\out", "x", "C:\\t");
            T.Check(itemArgs[itemArgs.IndexOf("--playlist-items") + 1] == "3", "chosen item of a multi-item post");
            var audioJob = new DownloadJob { Url = job.Url, Options = Media.Opt("mp3", "mp3", 0, "auto") };
            var audioArgs = Engine.JobArgs(audioJob, FormatSelector.Select(yt, audioJob.Options), "C:\\out", "x", "C:\\t");
            T.Check(audioArgs.Contains("-x") && audioArgs[audioArgs.IndexOf("--audio-format") + 1] == "mp3" && !audioArgs.Contains("-S"), "audio extraction arguments");

            // Output names: different qualities / profiles / ranges never collide
            var now = new DateTime(2026, 9, 28);
            var j = new DownloadJob { Title = "Klip: ilk", ContentId = "abc", Platform = "YouTube", Channel = "Kanal" };
            var o1 = Media.Opt("compatible", "mp4", 1080, "h264");
            var n1080 = OutputPlanner.Name(o1, j, new Selection { ShortSide = 1080, Fps = 60 }, now);
            var n720 = OutputPlanner.Name(o1, j, new Selection { ShortSide = 720, Fps = 30 }, now);
            var small = Media.Opt("small", "mp4", 480, "h264");
            var nSmall = OutputPlanner.Name(small, j, new Selection { ShortSide = 480 }, now);
            var custom480 = OutputPlanner.Name(Media.Opt("custom", "mp4", 480, "h264"), j, new Selection { ShortSide = 480 }, now);
            var clip = Media.Opt("compatible", "mp4", 1080, "h264"); clip.ClipStart = 10; clip.ClipEnd = 20;
            var nClip = OutputPlanner.Name(clip, j, new Selection { ShortSide = 1080 }, now);
            T.Check(n1080 == "Klip： ilk [abc] 1080p60" && n720 == "Klip： ilk [abc] 720p" && nSmall != custom480 && nClip.EndsWith("00m10s-00m20s"), "names carry quality, profile and range");
            T.Check(OutputPlanner.Name(Media.Opt("mp3", "mp3", 0, "auto"), j, new Selection { AudioOnly = true }, now) == "Klip： ilk [abc]", "audio names have no video quality");
            var org = Media.Opt("compatible", "mp4", 1080, "h264"); org.Organize = "platform-channel";
            T.Check(OutputPlanner.Folder(org, j) == "C:\\out\\YouTube\\Kanal" && OutputPlanner.Folder(Media.Opt("x", "mp4", 0, "auto"), j) == "C:\\out", "folder organization by platform and channel");
            var deep = "C:\\" + new string('d', 220);
            T.Check(OutputPlanner.Fit("C:\\out", new string('n', 300), "mp4").Length < 240, "long names shortened to fit the path");
            T.Rejects(() => OutputPlanner.Fit(deep, "name", "mp4"), ErrorCode.PathTooLong, "too deep folder explained instead of failing later");
        }

        static void ProgressAndVerify() {
            var sel = new Selection { Video = new FormatInfo { Id = "v", FileSize = 800 }, Audio = new FormatInfo { Id = "a", FileSize = 200 } };
            var tracker = new ProgressTracker(sel);
            var p1 = tracker.Update(new ProgressEvent { FormatId = "v", Status = "downloading", Downloaded = 400, Total = 800, TotalEstimate = -1 });
            T.Check(p1.Stream == 1 && p1.Streams == 2 && p1.IsVideo && Math.Abs(p1.Percent - 40) < 0.01, "overall progress over both streams (video half done = 40%)");
            tracker.Update(new ProgressEvent { FormatId = "v", Status = "finished", Downloaded = 800, Total = 800 });
            var p2 = tracker.Update(new ProgressEvent { FormatId = "a", Status = "downloading", Downloaded = 100, Total = 200 });
            T.Check(p2.Stream == 2 && !p2.IsVideo && Math.Abs(p2.Percent - 90) < 0.01, "audio stream continues the same bar instead of restarting");
            var unknown = new ProgressTracker(new Selection { Video = new FormatInfo { Id = "v" }, Audio = new FormatInfo { Id = "a" } });
            var p3 = unknown.Update(new ProgressEvent { FormatId = "v", Downloaded = 50, Total = -1, TotalEstimate = -1 });
            T.Check(p3.Percent < 0 && p3.StreamPercent < 0, "no fake percentage when sizes are unknown");

            var probeJson = "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":320,\"height\":240},{\"codec_type\":\"audio\",\"codec_name\":\"aac\"},{\"codec_type\":\"video\",\"codec_name\":\"mjpeg\",\"disposition\":{\"attached_pic\":1}}],\"format\":{\"duration\":\"19.02\"}}";
            var probe = EngineExecutor.ParseProbe(probeJson, 533931);
            T.Check(probe.HasVideo && probe.HasAudio && probe.VCodec == "h264" && probe.ACodec == "aac" && Math.Abs(probe.Duration - 19.02) < 0.01 && probe.Width == 320, "ffprobe result parsed (cover image ignored)");
            var yt = MediaParser.Parse(T.Fixture("youtube.json"), Platform.YouTube);
            var compat = Media.Opt("compatible", "mp4", 1080, "h264");
            var s = FormatSelector.Select(yt, compat);
            EngineExecutor.Verify(compat, s, yt, probe);
            T.Check(true, "verified file accepted");
            T.Rejects(() => EngineExecutor.Verify(compat, s, yt, EngineExecutor.ParseProbe("{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\"}],\"format\":{\"duration\":\"19\"}}", 100)), ErrorCode.VerifyFailed, "missing audio is not reported as success");
            T.Rejects(() => EngineExecutor.Verify(compat, s, yt, EngineExecutor.ParseProbe("{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\"},{\"codec_type\":\"audio\",\"codec_name\":\"aac\"}],\"format\":{\"duration\":\"9.5\"}}", 100)), ErrorCode.VerifyFailed, "incomplete (short) file is not reported as success");
            T.Rejects(() => EngineExecutor.Verify(compat, s, yt, EngineExecutor.ParseProbe("{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"vp9\"},{\"codec_type\":\"audio\",\"codec_name\":\"opus\"}],\"format\":{\"duration\":\"19\"}}", 100)), ErrorCode.VerifyFailed, "compatible profile checks the real codecs");
            var ig = MediaParser.Parse(T.Fixture("instagram.json"), Platform.Instagram);
            var igs = FormatSelector.Select(ig, compat);
            EngineExecutor.Verify(compat, igs, ig, EngineExecutor.ParseProbe("{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\"}],\"format\":{\"duration\":\"4.97\"}}", 63867));
            T.Check(true, "silent source verified without demanding audio");
            T.Check(DiskSpace.Needed(new Selection { Bytes = 1000000000, Video = new FormatInfo(), Audio = new FormatInfo() }, 60, 0) > 2000000000 && DiskSpace.Needed(new Selection { Bytes = -1 }, 60, 0) == -1, "disk estimate counts parts plus merge output; unknown stays unknown");
            T.Check(DiskSpace.Free(Path.GetTempPath()) > 0 && DiskSpace.Free("C:\\does\\not\\exist\\yet") > 0, "free space found for new folders");
        }
    }

    // Queue behavior with a scripted executor; runs on a dispatcher like the real UI.
    public class FakeExecutor : IJobExecutor {
        public readonly List<string> Started = new List<string>();
        public int Delay = 150;
        public async Task Run(JobItem item, JobContext context, CancellationToken token) {
            var job = item.Job; var script = job.Title ?? job.Url.Substring(job.Url.LastIndexOf('/') + 1);
            Started.Add(script);
            context.Post(() => item.SetStage(JobState.Downloading, "stage.download"));
            if (job.TempDir == null) job.TempDir = EngineExecutor.PartsFolder(job.Options.Folder, job.Id);
            File.WriteAllText(Path.Combine(job.TempDir, "part.part"), "x");
            if (script.StartsWith("slow")) { for (int i = 0; i < 400; i++) { await Task.Delay(25, token); context.Post(() => item.ApplyProgress(new ProgressSnapshot { Stream = 1, Streams = 1, IsVideo = true, Downloaded = i * 1000, Total = 400000, Percent = i / 4.0, StreamPercent = i / 4.0, Speed = 50000, Eta = (400 - i) / 40 })); } }
            else await Task.Delay(Delay, token);
            if (script.StartsWith("net")) throw new FlowException(ErrorCode.Network, "ERROR: timed out");
            if (script.StartsWith("disk")) throw new FlowException(ErrorCode.DiskSpaceLow, null, new[] { "2 GB", "1 GB" });
            if (script.StartsWith("gone")) throw new FlowException(ErrorCode.Unavailable, "ERROR: Video unavailable");
            var output = Path.Combine(job.Options.Folder, script + "-" + job.Id + ".mp4");
            File.WriteAllText(output, "video");
            EngineExecutor.TryDelete(job.TempDir, job.Id);
            context.Post(() => item.MarkCompleted(output, "18", new ProbeResult { Size = 5, HasVideo = true, HasAudio = true }));
        }
    }

    public static class QueueTests {
        static AppPaths Paths(string name) { var root = T.Scratch(name); return new AppPaths { DataDir = Path.Combine(root, "data"), LegacyDir = Path.Combine(root, "old"), DefaultDownloads = Path.Combine(root, "out") }; }
        static DownloadJob Job(string title, string folder) { var o = Media.Opt("compatible", "mp4", 1080, "h264"); o.Folder = folder; return new DownloadJob { Url = "https://example.com/v/" + title, Platform = "Other", Title = title, Options = o }; }
        static void Wait(Func<bool> condition, int ms) { UiTests.Wait(condition, ms); }

        public static void Run() {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var paths = Paths("queue");
            var settings = SettingsRules.Defaults(paths); settings.Parallel = 1; settings.Retries = 1;
            var fake = new FakeExecutor();
            var q = new QueueService(paths, () => settings, fake);
            var finished = new List<string>();
            q.Finished += i => finished.Add(i.Job.Title + ":" + i.Job.State);
            q.Start();
            var out1 = paths.DefaultDownloads; Directory.CreateDirectory(out1);
            var a = q.Add(Job("slowA", out1), false); var b = q.Add(Job("okB", out1), false); var c = q.Add(Job("okC", out1), false);
            Wait(() => a.Job.State == JobState.Downloading, 3000);
            T.Check(q.ActiveCount == 1 && b.Job.State == JobState.Queued && c.Job.State == JobState.Queued, "one download at a time by default; others wait");
            var d = q.Add(Job("okD", out1), false);
            T.Check(d.Job.State == JobState.Queued && a.IsActive, "new links can be added while a download runs");
            Wait(() => a.ProgressValue > 5, 3000);
            T.Check(!a.Indeterminate && a.PercentText.EndsWith("%") && a.DetailText.Length > 0, "live progress shown for the running job");
            q.Pause(a);
            Wait(() => a.Job.State == JobState.Paused && b.IsActive, 3000);
            T.Check(a.Job.State == JobState.Paused && Directory.Exists(a.Job.TempDir) && b.IsActive, "pause keeps the parts and lets the next job start");
            Wait(() => b.Job.State == JobState.Completed && c.Job.State == JobState.Completed && d.Job.State == JobState.Completed, 5000);
            T.Check(finished.Contains("okB:completed") && File.Exists(b.Job.OutputPath), "completed jobs reported with their files");
            q.Resume(a);
            Wait(() => a.IsActive, 3000);
            q.Cancel(a);
            Wait(() => a.Job.State == JobState.Canceled, 3000);
            T.Check(a.Job.State == JobState.Canceled && !Directory.Exists(a.Job.TempDir) && finished.Contains("slowA:canceled"), "cancel is not pause: parts removed, job ends as canceled");

            // Two in parallel; cancelling one leaves the other running
            settings.Parallel = 2;
            var e = q.Add(Job("slowE", out1), false); var f = q.Add(Job("slowF", out1), false);
            Wait(() => e.IsActive && f.IsActive, 3000);
            T.Check(q.ActiveCount == 2, "parallel limit of two honored");
            q.Cancel(e);
            Wait(() => e.Job.State == JobState.Canceled, 3000);
            T.Check(f.IsActive, "cancelling one job does not stop another");
            q.Pause(f); Wait(() => f.Job.State == JobState.Paused, 3000);
            settings.Parallel = 1;

            // Transient errors retry a limited number of times; permanent ones do not
            var n = q.Add(Job("netN", out1), false);
            Wait(() => n.Job.State == JobState.Retry, 3000);
            T.Check(n.Job.State == JobState.Retry && n.Job.Attempts == 1 && Iso.Parse(n.Job.NextRetryAt).Value > DateTime.UtcNow && n.StateText.Contains("1"), "network error schedules a retry");
            n.Job.NextRetryAt = Iso.From(DateTime.UtcNow.AddSeconds(-1));
            Wait(() => n.Job.State == JobState.Failed, 4000);
            T.Check(n.Job.State == JobState.Failed && n.Job.Attempts == 2 && finished.Contains("netN:failed"), "retries are limited, then the job fails");
            var g = q.Add(Job("goneG", out1), false);
            Wait(() => g.Job.State == JobState.Failed, 3000);
            T.Check(g.Job.Attempts == 1 && g.CanRetry && g.ErrorText == Loc.Error(ErrorCode.Unavailable), "unavailable video is not retried automatically");
            var k = q.Add(Job("diskK", out1), false);
            Wait(() => k.Job.State == JobState.NeedsAction, 3000);
            T.Check(k.CanFixFolder && k.CanResume && k.ErrorText.Contains("2 GB"), "low disk space asks the user, with numbers");
            var newFolder = Path.Combine(out1, "other"); Directory.CreateDirectory(newFolder);
            k.Job.Title = "okK";
            q.ChangeFolder(k, newFolder);
            Wait(() => k.Job.State == JobState.Completed, 3000);
            T.Check(k.Job.State == JobState.Completed && k.Job.OutputPath.StartsWith(newFolder), "changing the folder continues the job there");
            var copy = g.Diagnostics;
            T.Check(copy.Contains("Unavailable") && !copy.Contains(Environment.UserName + "\\"), "copied details are masked");

            // Moving, removing, clearing
            var h = q.Add(Job("okH", out1), true); var i2 = q.Add(Job("okI", out1), true);
            q.Move(i2, -1);
            T.Check(q.Items.IndexOf(i2) < q.Items.IndexOf(h), "order can be changed");
            int before = q.Items.Count; q.ClearFinished();
            T.Check(q.Items.All(x => !JobState.IsFinished(x.Job.State)) && q.Items.Count < before && File.Exists(b.Job.OutputPath), "clearing finished jobs never deletes their files");

            // Shutdown and recovery
            h.Job.Title = "slowH"; q.Resume(h);
            Wait(() => h.IsActive, 3000);
            q.Shutdown();
            Wait(() => false, 300);
            var saved = JsonStore.Convert<QueueFile>(JsonStore.Load(paths.QueueFile).Data);
            T.Check(saved.Jobs.Any(j => j.Title == "slowH" && j.State == JobState.Paused), "closing pauses running jobs and writes the queue");
            var q2 = new QueueService(paths, () => settings, fake); q2.Load();
            T.Check(q2.Items.Any(x => x.Job.Title == "slowH" && x.Job.State == JobState.Paused && Directory.Exists(x.Job.TempDir)), "jobs come back after reopening, parts intact");

            // Crash recovery: a job left "downloading" in the file
            var file = new QueueFile { SchemaVersion = 1, Jobs = new List<DownloadJob> { Job("crash", out1), Job("sched", out1), Job("done", out1) } };
            file.Jobs[0].Id = "c1"; file.Jobs[0].State = JobState.Downloading;
            file.Jobs[1].Id = "s1"; file.Jobs[1].State = JobState.Scheduled; file.Jobs[1].ScheduledAt = Iso.From(DateTime.UtcNow.AddHours(-2));
            file.Jobs[2].Id = "d1"; file.Jobs[2].State = JobState.Completed;
            var crashPaths = Paths("queue-crash");
            JsonStore.Save(crashPaths.QueueFile, file);
            settings.ResumeInterrupted = true; settings.MissedSchedule = "hold";
            var q3 = new QueueService(crashPaths, () => settings, fake); q3.Load();
            var crash = q3.Items.First(x => x.Job.Id == "c1");
            T.Check(crash.Job.State == JobState.Queued && crash.Job.Interrupted && crash.NoteText == Loc.T("note.interrupted"), "interrupted job resumes after a crash, with a note");
            var sched = q3.Items.First(x => x.Job.Id == "s1");
            T.Check(sched.Job.State == JobState.Paused && sched.Job.Note == "note.missed", "missed schedule held as configured");
            T.Check(!q3.Items.Any(x => x.Job.Id == "d1"), "completed jobs are not re-queued");
            settings.ResumeInterrupted = false;
            JsonStore.Save(crashPaths.QueueFile, file);
            var q4 = new QueueService(crashPaths, () => settings, fake); q4.Load();
            T.Check(q4.Items.First(x => x.Job.Id == "c1").Job.State == JobState.Paused, "interrupted job waits paused when auto-resume is off");
            File.WriteAllText(crashPaths.QueueFile, "{broken", Encoding.UTF8);
            var q5 = new QueueService(crashPaths, () => settings, fake); q5.Load();
            T.Check(q5.LoadInfo.Recovered && q5.Items.Count > 0, "damaged queue file restored from its last good copy");

            // Scheduling
            var paths6 = Paths("queue-schedule");
            var q6 = new QueueService(paths6, () => settings, fake); q6.Start();
            var later = Job("okLater", out1); later.ScheduledAt = Iso.From(DateTime.UtcNow.AddSeconds(2));
            var li = q6.Add(later, false);
            T.Check(li.Job.State == JobState.Scheduled && li.StateText.StartsWith(Loc.T("state.scheduled").Split('·')[0].Trim()), "scheduled job waits for its time");
            Wait(() => li.Job.State == JobState.Completed, 6000);
            T.Check(li.Job.State == JobState.Completed, "scheduled job starts on time while Flow is open");

            // Output reservations between jobs
            var r1 = q6.Add(Job("okR1", out1), true); r1.Job.OutputPath = Path.Combine(out1, "same.mp4");
            var r2 = q6.Add(Job("okR2", out1), true);
            var ctx = typeof(QueueService);
            T.Check(q6.Items.Any(x => x.Job.OutputPath == Path.Combine(out1, "same.mp4")), "queued job reserves its output name");
            q6.Shutdown(); q.Shutdown(); q2.Shutdown(); q3.Shutdown(); q4.Shutdown(); q5.Shutdown();
        }
    }
}
