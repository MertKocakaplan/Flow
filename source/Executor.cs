using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Flow {
    public sealed class JobContext {
        public Action<string> Log = line => {};
        public Action<Action> Post = action => action();
        public Func<string, string, bool> ReservedByOther = (path, id) => false;
        public Func<string, int, MediaInfo> CachedInfo = (url, index) => null;
    }

    public interface IJobExecutor { Task Run(JobItem item, JobContext context, CancellationToken token); }

    public sealed class ProbeResult {
        public double Duration = -1; public long Size;
        public bool HasVideo, HasAudio;
        public string VCodec, ACodec;
        public int Width, Height, SubtitleStreams;
    }

    public static class DiskSpace {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
        public static long Free(string folder) {
            var existing = folder;
            while (!string.IsNullOrEmpty(existing) && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
            ulong available, total, free;
            if (string.IsNullOrEmpty(existing) || !GetDiskFreeSpaceEx(existing, out available, out total, out free)) return -1;
            return (long)available;
        }
        // Parts, merge output and conversion can exist at the same time.
        public static long Needed(Selection s, double duration, long alreadyDownloaded) {
            if (s.Bytes < 0) return -1;
            double factor = s.Audio != null && s.Video != null ? 2.05 : 1.05;
            double extra = (s.VideoConversion ? s.Bytes : 0) + (s.AudioConversion && duration > 0 ? duration * 40000 : 0);
            return (long)Math.Max(0, s.Bytes * factor + extra - alreadyDownloaded) + 64L * 1024 * 1024;
        }
    }

    public partial class EngineExecutor : IJobExecutor {
        static readonly Dictionary<string, string> PostStages = new Dictionary<string, string> {
            { "Merger", "stage.merge" }, { "FFmpegMerger", "stage.merge" }, { "FFmpegExtractAudio", "stage.extractAudio" }, { "FFmpegVideoConvertor", "stage.convert" },
            { "FFmpegVideoRemuxer", "stage.remux" }, { "FFmpegEmbedSubtitle", "stage.embedSubs" }, { "FFmpegSubtitlesConvertor", "stage.subs" }, { "MoveFiles", "stage.move" }
        };

        public static async Task<MediaInfo> Inspect(string url, Platform fallback, int itemIndex, CancellationToken token) {
            return await Inspect(url,fallback,itemIndex,token,null);
        }
        public static async Task<MediaInfo> Inspect(string url, Platform fallback, int itemIndex, CancellationToken token,string referrer) {
            Engine.EnsureTools();
            ProcessResult result;
            using (await EngineGate.Enter(token)) result = await ProcessRunner.Run(Engine.Exe, Engine.InspectArgs(url, itemIndex,referrer), null, token, 180);
            if (result.ExitCode != 0) throw new FlowException(Engine.Classify(result.Error), result.Error);
            var info = MediaParser.Parse(result.Output, fallback);
            if (info.IsLive) throw new FlowException(ErrorCode.Live);
            return info;
        }

        public async Task Run(JobItem item, JobContext context, CancellationToken token) {
            var job = item.Job;
            if(job.MediaKind=="image"){await ImageTransfer.Run(item,context,token);return;}
            Platform platform; if (!Enum.TryParse(job.Platform, out platform)) platform = Platform.Other;
            context.Post(() => item.SetStage(JobState.Inspecting, "stage.inspect"));
            var info = context.CachedInfo(job.Url, job.ItemIndex) ?? await Inspect(job.Url, platform, job.ItemIndex, token,job.Referrer);
            token.ThrowIfCancellationRequested();
            context.Post(() => item.ApplyMedia(info));

            var selection = FormatSelector.Select(info, job.Options);
            var folder = OutputPlanner.Folder(job.Options, job);
            Prepare(folder);
            var ext = OutputPlanner.Extension(job.Options);
            var name = OutputPlanner.Fit(folder, OutputPlanner.Name(job.Options, job, selection, DateTime.Now), ext);
            var target = Path.Combine(folder, name + "." + ext);
            if (File.Exists(target) && !(job.OutputPath == target && job.ResolvedFormat == selection.Spec && HasParts(job.TempDir))) {
                if (job.Options.Duplicate == "skip") { context.Post(() => item.MarkSkipped(target, selection)); return; }
                target = Naming.Unique(folder, name, ext, p => File.Exists(p) || context.ReservedByOther(p, job.Id));
            } else if (context.ReservedByOther(target, job.Id)) target = Naming.Unique(folder, name, ext, p => File.Exists(p) || context.ReservedByOther(p, job.Id));

            // A changed format cannot reuse old parts; say so instead of silently mixing streams.
            string note = null;
            if (!string.IsNullOrEmpty(job.TempDir) && HasParts(job.TempDir)) {
                if (job.ResolvedFormat != null && job.ResolvedFormat != selection.Spec) { ClearParts(job.TempDir); note = "note.restartFormat"; }
                else note = "note.resumed";
            }
            if (string.IsNullOrEmpty(job.TempDir) || !Directory.Exists(job.TempDir)) job.TempDir = PartsFolder(folder, job.Id);
            var targetPath = target;
            context.Post(() => item.ApplySelection(selection, targetPath, note));

            var free = DiskSpace.Free(folder);
            var needed = DiskSpace.Needed(selection, info.Duration, PartsSize(job.TempDir));
            if (needed > 0 && free >= 0 && free < needed) throw new FlowException(ErrorCode.DiskSpaceLow, null, new[] { Loc.Size(needed), Loc.Size(free) });

            var o = job.Options;
            if (o.HasClip) ClipTime.Validate(o.ClipStart, o.ClipEnd, info.Duration);
            string saved, actualFormat;
            // Flow post-processes itself when converting or cutting locally; the engine output then stays in the job folder.
            bool local = o.HasClip && job.ClipMethod == "local";
            bool convert = selection.VideoConversion;
            bool byFlow = convert || local || selection.ConvertIfNeeded;
            var finalBase = Path.GetFileNameWithoutExtension(target);
            string[] run = null;
            bool fallback = false;
            try {
                run = await Download(item, context, selection, byFlow ? job.TempDir : folder, byFlow ? "source" : finalBase, note, info.Duration, token);
            } catch (FlowException ex) {
                // Some sources cannot serve a range directly: download everything and cut locally, and say so.
                if (!o.HasClip || local || ex.Code != ErrorCode.Unknown) throw;
                fallback = true;
            }
            if (fallback) {
                job.ClipMethod = "local"; local = true; byFlow = true;
                ClearParts(job.TempDir);
                context.Post(() => { job.Note = "note.clipLocal"; item.Refresh(); });
                run = await Download(item, context, selection, job.TempDir, "source", null, info.Duration, token);
            }
            saved = run[0]; actualFormat = run[1];
            if (string.IsNullOrEmpty(saved) || !File.Exists(saved)) throw new FlowException(ErrorCode.OutputMissing);
            if (byFlow) {
                // Unknown source codec: look at the real streams and re-encode only if they are not H.264 / AAC.
                if (selection.ConvertIfNeeded && !convert) {
                    var source = await Probe(saved, token);
                    convert = source.VCodec != "h264" || (source.HasAudio && source.ACodec != "aac");
                }
                string mode = !convert && (!local || o.ClipMode != "precise") ? "copy" : o.IsAudio ? o.Format : "h264";
                context.Post(() => item.SetStage(JobState.Processing, local ? "stage.cut" : "stage.convert"));
                var produced = Path.Combine(job.TempDir, "result." + ext);
                // A clip fetched directly is only that long; the whole video's length would stop the percentage short of 100.
                double length = o.HasClip && !local ? o.ClipEnd - o.ClipStart : info.Duration;
                await Transcode(saved, produced, mode, local ? o.ClipStart : 0, local ? o.ClipEnd : 0, length, token, percent => context.Post(() => item.ApplyStagePercent(percent)), context.Log);
                if (File.Exists(target)) target = Naming.Unique(folder, finalBase, ext, p => File.Exists(p) || context.ReservedByOther(p, job.Id));
                File.Move(produced, target);
                MoveSubtitles(job.TempDir, "source", folder, Path.GetFileNameWithoutExtension(target));
                saved = target;
            }

            context.Post(() => item.SetStage(JobState.Verifying, "stage.verify"));
            var probe = await Probe(saved, token);
            Verify(o, selection, info, probe);
            // Subtitles are extra: a missing track is reported on the job, not turned into a failed video.
            string subsNote = null;
            if (o.SubLangs != null && o.SubLangs.Count > 0 && !o.HasClip) {
                bool embed = o.SubMode == "embed" && !o.IsAudio;
                bool got = embed ? probe.SubtitleStreams > 0 : SubtitleFiles(Path.GetDirectoryName(saved), Path.GetFileNameWithoutExtension(saved)).Count > 0;
                if (!got) subsNote = "note.subsMissing";
            }
            // The chosen streams were gone when the download started and the engine took the nearest ones; say so,
            // since the row then shows a resolution or codec other than the one in the preview.
            string formatNote = FormatChanged(selection, actualFormat) ? "note.formatChanged" : null;
            var shownNote = formatNote ?? subsNote;
            TryDelete(job.TempDir, job.Id);
            var final = saved; var format = actualFormat ?? selection.Spec;
            context.Post(() => { item.MarkCompleted(final, format, probe); if (shownNote != null) { job.Note = shownNote; item.Refresh(); } });
        }

        // One engine run. Returns { saved path, actual format }.
        async Task<string[]> Download(JobItem item, JobContext context, Selection selection, string homeDir, string baseName, string note, double duration, CancellationToken token) {
            var job = item.Job;
            context.Post(() => item.SetStage(JobState.Downloading, "stage.download"));
            string saved = null, actualFormat = null;
            bool firstProgress = true;
            // A bit rate estimates the whole file, so a clip waits for the sizes the engine reports.
            var tracker = new ProgressTracker(selection, job.Options.HasClip ? 0 : duration);
            var args = Engine.JobArgs(job, selection, homeDir, baseName, job.TempDir);
            context.Log("Flow: " + string.Join(" ", args.Where(a => !a.StartsWith("http")).Select(a => a.Contains(" ") ? "\"" + a + "\"" : a)));
            var result = await ProcessRunner.Run(Engine.Exe, args, line => {
                var file = Engine.ParseFile(line);
                if (file != null) { saved = file; context.Log(line); return; }
                var fmt = Engine.ParseValue(line, Engine.FormatPrefix);
                if (fmt != null) { actualFormat = fmt; return; }
                ProgressEvent progress = null;
                try { progress = Engine.ParseProgress(line); } catch (ArgumentException) {} catch (InvalidOperationException) {}
                if (progress != null) {
                    var snapshot = tracker.Update(progress);
                    // The first event after a resume starts at the bytes already on disk.
                    if (firstProgress) { firstProgress = false; var offset = progress.Downloaded; if (note == "note.resumed" && offset > 0 && progress.Status != "finished") context.Post(() => item.ResumedBytes = offset); }
                    context.Post(() => ShowProgress(item, snapshot)); return;
                }
                var post = Engine.ParsePost(line);
                if (post != null) {
                    string stage; if (post[1] == null || !PostStages.TryGetValue(post[1], out stage)) stage = "stage.processing";
                    if (post[0] == "started") context.Post(() => item.SetStage(JobState.Processing, stage));
                    context.Log(line); return;
                }
                context.Log(line);
            }, token, 24 * 60 * 60);
            if (result.ExitCode != 0) throw new FlowException(Engine.Classify(result.Error), Engine.Mask(result.Error));
            return new[] { saved, actualFormat };
        }

        // The engine may run a step before the media download (converting the cover for embedding), which moves the job
        // to "processing". A transfer event means the download is under way again.
        public static void ShowProgress(JobItem item, ProgressSnapshot snapshot) {
            if (item.Job.State != JobState.Downloading) item.SetStage(JobState.Downloading, "stage.download");
            item.ApplyProgress(snapshot);
        }

        internal static void Prepare(string folder) {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder)) throw new FlowException(ErrorCode.FolderInvalid);
            try {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, ".flow-write-" + Guid.NewGuid().ToString("N"));
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) {}
            } catch (UnauthorizedAccessException) { throw new FlowException(ErrorCode.WriteDenied); }
            catch (IOException ex) { var code = Errors.FromException(ex); throw new FlowException(code == ErrorCode.Unknown ? ErrorCode.WriteDenied : code, ex.Message); }
        }

        // Unfinished parts live in a hidden per-job folder next to the output, on the same disk.
        public static string PartsFolder(string folder, string jobId) {
            var root = Path.Combine(folder, ".flow-parts");
            Directory.CreateDirectory(root);
            try { File.SetAttributes(root, File.GetAttributes(root) | FileAttributes.Hidden); } catch (Exception) {}
            var dir = Path.Combine(root, jobId);
            Directory.CreateDirectory(dir);
            return dir;
        }
        public static bool HasParts(string dir) { return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any(); }
        public static long PartsSize(string dir) { try { return HasParts(dir) ? new DirectoryInfo(dir).GetFiles().Sum(f => f.Length) : 0; } catch (Exception) { return 0; } }
        static void ClearParts(string dir) { foreach (var file in Directory.GetFiles(dir)) try { File.Delete(file); } catch (IOException) {} }
        // Only Flow's own per-job folder is ever removed.
        public static void TryDelete(string dir, string jobId) {
            try {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || Path.GetFileName(dir) != jobId || Path.GetFileName(Path.GetDirectoryName(dir)) != ".flow-parts") return;
                Directory.Delete(dir, true);
                var root = Path.GetDirectoryName(dir);
                if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
            } catch (Exception) {}
        }

        // Re-encodes to H.264 / AAC (compatible profile when the source has no H.264).
        public static Task ConvertCompatible(string input, string output, double duration, CancellationToken token, Action<double> progress, Action<string> log) {
            return Transcode(input, output, "h264", 0, 0, duration, token, progress, log);
        }

        public static async Task<ProbeResult> Probe(string path, CancellationToken token) {
            var result = await ProcessRunner.Run(Engine.FfprobeExe, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }, null, token, 120);
            if (result.ExitCode != 0) throw new FlowException(ErrorCode.VerifyFailed, result.Error, new[] { "verify.unreadable" });
            return ParseProbe(result.Output, new FileInfo(path).Length);
        }
        public static ProbeResult ParseProbe(string json, long size) {
            var data = Engine.Json().DeserializeObject(json) as Dictionary<string, object>;
            var probe = new ProbeResult { Size = size };
            if (data == null) return probe;
            var format = data.ContainsKey("format") ? data["format"] as Dictionary<string, object> : null;
            probe.Duration = Engine.Number(format, "duration", -1);
            object streams;
            if (data.TryGetValue("streams", out streams) && streams is System.Collections.IEnumerable) {
                foreach (var raw in ((System.Collections.IEnumerable)streams).Cast<object>()) {
                    var s = raw as Dictionary<string, object>; if (s == null) continue;
                    var type = Engine.Text(s, "codec_type", "");
                    var disposition = s.ContainsKey("disposition") ? s["disposition"] as Dictionary<string, object> : null;
                    bool cover = Engine.Number(disposition, "attached_pic", 0) > 0;
                    if (type == "video" && !cover && !probe.HasVideo) { probe.HasVideo = true; probe.VCodec = Engine.Text(s, "codec_name", null); probe.Width = (int)Engine.Number(s, "width"); probe.Height = (int)Engine.Number(s, "height"); if (probe.Duration < 0) probe.Duration = Engine.Number(s, "duration", -1); }
                    else if (type == "audio" && !probe.HasAudio) { probe.HasAudio = true; probe.ACodec = Engine.Text(s, "codec_name", null); if (probe.Duration < 0) probe.Duration = Engine.Number(s, "duration", -1); }
                    else if (type == "subtitle") probe.SubtitleStreams++;
                }
            }
            return probe;
        }

        public static bool FormatChanged(Selection selection, string actualFormat) {
            return !selection.EngineChooses && !string.IsNullOrEmpty(actualFormat) && actualFormat != selection.Spec;
        }

        // "Completed" only after the file really contains what was chosen.
        public static void Verify(JobOptions options, Selection selection, MediaInfo media, ProbeResult probe) {
            Func<string, FlowException> fail = key => new FlowException(ErrorCode.VerifyFailed, key, new[] { key });
            if (probe.Size <= 0) throw fail("verify.empty");
            if (options.IsAudio) {
                if (!probe.HasAudio) throw fail("verify.noAudio");
                if (options.Format == "mp3" && probe.ACodec != "mp3") throw fail("verify.codec");
                if (options.Format == "m4a" && probe.ACodec != "aac" && probe.ACodec != "alac") throw fail("verify.codec");
            } else {
                if (!probe.HasVideo) throw fail("verify.noVideo");
                if (!selection.Silent && !selection.AudioUncertain && !probe.HasAudio) throw fail("verify.noAudio");
                // "Do not exceed" is checked on the file itself: the engine may have fallen back to another format.
                if (options.Strict && options.Height > 0 && probe.Width > 0 && probe.Height > 0 && Math.Min(probe.Width, probe.Height) > options.Height) throw fail("verify.quality");
                if (options.Codec == "h264" && options.Format == "mp4" && (probe.VCodec != "h264" || (probe.HasAudio && probe.ACodec != "aac"))) throw fail("verify.codec");
            }
            double expected = options.HasClip ? options.ClipEnd - options.ClipStart : media.Duration;
            if (expected > 0 && probe.Duration > 0) {
                double tolerance = options.HasClip ? Math.Max(5, expected * 0.08) : Math.Max(2.5, expected * 0.02);
                if (probe.Duration < expected - tolerance) throw fail("verify.short");
            }
        }
    }

    // Per-stream progress: the percentage belongs to the stream being transferred; the overall value is
    // only computed when every stream size is known.
    public sealed class ProgressTracker {
        readonly List<string> ids = new List<string>();
        readonly List<double> totals = new List<double>(), done = new List<double>();
        readonly List<bool> video = new List<bool>();
        int current = -1;
        public ProgressTracker(Selection s, double duration = 0) {
            if (s.Video != null) Add(s.Video.Id, s.Video.Size(duration) > 0 ? s.Video.Size(duration) : -1, true);
            if (s.Audio != null) Add(s.Audio.Id, s.Audio.Size(duration) > 0 ? s.Audio.Size(duration) : -1, false);
            if (s.Video == null && s.Audio == null) Add("?", -1, true);
        }
        void Add(string id, double total, bool isVideo) { ids.Add(id); totals.Add(total); done.Add(0); video.Add(isVideo); }
        public ProgressSnapshot Update(ProgressEvent e) {
            int index = ids.IndexOf(e.FormatId ?? "");
            if (index < 0) { index = current < 0 ? 0 : current; if (current >= 0 && done[current] > 0 && e.Downloaded < done[current] * 0.5 && current + 1 < ids.Count) index = current + 1; }
            current = index;
            double total = e.Total > 0 ? e.Total : e.TotalEstimate > 0 ? e.TotalEstimate : totals[index];
            if (total > 0) totals[index] = total;
            done[index] = e.Status == "finished" && total > 0 ? total : e.Downloaded;
            var snap = new ProgressSnapshot { Stream = index + 1, Streams = ids.Count, IsVideo = video[index], Speed = e.Speed, Eta = e.Eta, Downloaded = done.Sum(), StreamPercent = e.Percent };
            snap.Total = totals.All(t => t > 0) ? totals.Sum() : -1;
            snap.Percent = snap.Total > 0 ? Math.Min(100, snap.Downloaded * 100 / snap.Total) : -1;
            return snap;
        }
    }
    public sealed class ProgressSnapshot { public int Stream, Streams; public bool IsVideo; public double Speed, Eta, Downloaded, Total, Percent, StreamPercent; }
}
