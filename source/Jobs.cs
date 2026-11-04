using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Flow {
    // The options of a job are a fixed copy taken when it is added; later setting changes do not alter it.
    public class JobOptions {
        public string Profile { get; set; }
        public string Format { get; set; }
        public int Height { get; set; }
        public bool Strict { get; set; }
        public string Codec { get; set; }
        public int FpsLimit { get; set; }
        public string Folder { get; set; }
        public string Organize { get; set; }
        public string NameTemplate { get; set; }
        public string Duplicate { get; set; }
        public long SpeedLimit { get; set; }
        public List<string> SubLangs { get; set; }
        public bool SubAuto { get; set; }
        public string SubMode { get; set; }       // file | embed
        public double ClipStart { get; set; }
        public double ClipEnd { get; set; }       // 0 = no clip
        public string ClipMode { get; set; }      // fast | precise
        public bool CollectionFolder { get; set; }
        public bool IsAudio { get { return Format == "mp3" || Format == "m4a"; } }
        public bool HasClip { get { return ClipEnd > 0 && ClipEnd > ClipStart; } }
        public static JobOptions From(FlowSettings s) {
            return new JobOptions { Profile = s.Profile, Format = s.Format, Height = s.Height, Strict = s.StrictQuality, Codec = s.Codec, FpsLimit = s.FpsLimit, Folder = s.Folder, Organize = s.Organize, NameTemplate = s.NameTemplate, Duplicate = s.Duplicate, SpeedLimit = s.SpeedLimit, SubLangs = new List<string>(), SubMode = "file", ClipMode = "fast", CollectionFolder = s.CollectionFolder };
        }
        public JobOptions Copy() { var c = (JobOptions)MemberwiseClone(); c.SubLangs = SubLangs == null ? new List<string>() : new List<string>(SubLangs); return c; }
    }

    public static class JobState {
        public const string Queued = "queued", Scheduled = "scheduled", Inspecting = "inspecting", Downloading = "downloading", Processing = "processing", Verifying = "verifying",
            Completed = "completed", Paused = "paused", Retry = "retry", NeedsAction = "needs-action", Failed = "failed", Canceled = "canceled";
        public static readonly string[] Active = { Inspecting, Downloading, Processing, Verifying };
        public static readonly string[] All = { Queued, Scheduled, Inspecting, Downloading, Processing, Verifying, Completed, Paused, Retry, NeedsAction, Failed, Canceled };
        public static bool IsActive(string s) { return Active.Contains(s); }
        public static bool IsFinished(string s) { return s == Completed || s == Failed || s == Canceled; }
    }

    public class DownloadJob {
        public string Id { get; set; }
        public string BrowserRequestId { get; set; } // Idempotency key for a browser submission; old jobs have null.
        public string MediaKind { get; set; } // null = existing video/audio path; image = original raster image
        public string Referrer { get; set; }
        public string Url { get; set; }
        public string Platform { get; set; }
        public int ItemIndex { get; set; }            // entry of a multi-item post, 0 = single
        public string Title { get; set; }
        public string Channel { get; set; }
        public string ContentId { get; set; }
        public string Thumbnail { get; set; }
        public double Duration { get; set; }
        public string CollectionTitle { get; set; }
        public int CollectionIndex { get; set; }
        public JobOptions Options { get; set; }
        public string State { get; set; }
        public string Error { get; set; }
        public string ErrorDetail { get; set; }
        public List<string> ErrorArgs { get; set; }
        public int Attempts { get; set; }
        public string CreatedAt { get; set; }
        public string StartedAt { get; set; }
        public string FinishedAt { get; set; }
        public string ScheduledAt { get; set; }
        public string NextRetryAt { get; set; }
        public string TempDir { get; set; }
        public string ResolvedFormat { get; set; }
        public string OutputPath { get; set; }
        public long ExpectedBytes { get; set; }
        public bool SizeExact { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public string VCodec { get; set; }
        public string ACodec { get; set; }
        public bool Interrupted { get; set; }
        public string Note { get; set; }              // text key of an informational note
        public bool Skipped { get; set; }             // an identical output already existed
        public string ClipMethod { get; set; }        // null/direct = engine range download, local = full download + local cut
    }

    public class QueueFile {
        public const int CurrentSchema = 1;
        public int SchemaVersion { get; set; }
        public List<DownloadJob> Jobs { get; set; }
        public List<BrowserReceipt> BrowserReceipts { get; set; }
    }

    public class BrowserReceipt {
        public string RequestId { get; set; }
        public string JobId { get; set; }
        public string CreatedAt { get; set; }
    }

    public class Selection {
        public FormatInfo Video, Audio;
        public string Spec;
        public bool AudioOnly, Silent, VideoConversion, AudioConversion, AboveLimit, AudioUncertain;
        public bool ConvertIfNeeded;   // codec unknown: checked with ffprobe after download, converted only if needed
        public long Bytes = -1;
        public bool SizeExact;
        public int Width, Height, ShortSide;
        public double Fps;
        public string VCodec, ACodec, Container;
        public List<string> Notes = new List<string>();   // text keys
        public bool NeedsConversion { get { return VideoConversion || AudioConversion; } }
    }

    // Picks real formats from the item's metadata. Everything is decided here, in Flow,
    // so the choice can be shown before the download and checked afterwards.
    public static class FormatSelector {
        static readonly string[] Mp4Video = { "avc1", "h264", "av01", "hev1", "hvc1", "h265", "vp09", "vp9" };
        static readonly string[] Mp4Audio = { "mp4a", "aac", "opus", "mp3", "ac-3", "ec-3" };
        static bool Is(string codec, params string[] prefixes) { if (codec == null) return false; var c = codec.ToLowerInvariant(); return prefixes.Any(p => c.StartsWith(p, StringComparison.Ordinal)); }
        public static bool IsH264(string codec) { return Is(codec, "avc1", "avc3", "h264"); }
        public static bool IsAac(string codec) { return Is(codec, "mp4a", "aac"); }

        static bool Drc(FormatInfo f) { return (f.Id ?? "").EndsWith("-drc", StringComparison.OrdinalIgnoreCase) || (f.Note ?? "").IndexOf("DRC", StringComparison.Ordinal) >= 0; }
        static int AudioRank(FormatInfo f) { return (f.LanguagePreference >= 10 || (f.Note ?? "").ToLowerInvariant().Contains("original") ? 2 : 0) + (Drc(f) ? 0 : 1); }
        static double AudioRate(FormatInfo f) { return f.Abr > 0 ? f.Abr : f.Tbr; }
        // Opus sounds at least as good as AAC at a lower bitrate; used where the container allows it.
        static double AudioScore(FormatInfo f) { return AudioRate(f) * (Is(f.ACodec, "opus") ? 1.3 : 1); }
        static int ProtocolRank(FormatInfo f) { var p = (f.Protocol ?? "https").ToLowerInvariant(); return p.StartsWith("http") && !p.Contains("m3u8") && !p.Contains("dash") ? 1 : 0; }

        public static Selection Select(MediaInfo media, JobOptions options) {
            var usable = media.Usable.ToList();
            if (usable.Count == 0) throw new FlowException(ErrorCode.NoVideo);
            return options.IsAudio ? SelectAudio(media, usable, options) : SelectVideo(media, usable, options);
        }

        static Selection SelectAudio(MediaInfo media, List<FormatInfo> usable, JobOptions options) {
            if (media.HasAudio == Tri.No) throw new FlowException(ErrorCode.NoAudio);
            var result = new Selection { AudioOnly = true, Container = options.Format };
            var audioOnly = usable.Where(f => (f.HasAudio || f.AudioUnknown) && !f.HasVideo && !f.VideoUnknown || (f.VCodec == "none" && !f.AudioUnknown && f.HasAudio)).Distinct().ToList();
            var known = audioOnly.Where(f => f.HasAudio).ToList();
            FormatInfo pick = null;
            if (options.Format == "m4a") pick = known.Where(f => IsAac(f.ACodec)).OrderByDescending(AudioRank).ThenByDescending(AudioRate).FirstOrDefault();
            if (pick == null) pick = known.OrderByDescending(AudioRank).ThenByDescending(AudioRate).FirstOrDefault();
            if (pick == null) {
                // No separate audio stream (common on TikTok/Instagram): take the audio from a muxed file.
                var muxed = usable.Where(f => f.HasAudio && (f.HasVideo || f.VideoUnknown)).OrderBy(f => f.ShortSide > 0 ? f.ShortSide : int.MaxValue).ThenByDescending(AudioRate).ToList();
                pick = muxed.FirstOrDefault();
            }
            if (pick == null) {
                pick = audioOnly.Concat(usable.Where(f => f.AudioUnknown)).FirstOrDefault();
                if (pick == null) throw new FlowException(ErrorCode.NoAudio);
                result.AudioUncertain = true; result.Notes.Add("sel.note.audioUncertain");
            }
            result.Audio = pick; result.Spec = pick.Id; result.ACodec = pick.ACodec;
            result.AudioConversion = options.Format == "mp3" ? !Is(pick.ACodec, "mp3") : !IsAac(pick.ACodec);
            if (result.AudioConversion) result.Notes.Add(options.Format == "mp3" ? "sel.note.toMp3" : "sel.note.toM4a");
            SetSize(result, media, options, pick, null);
            return result;
        }

        static Selection SelectVideo(MediaInfo media, List<FormatInfo> usable, JobOptions options) {
            if (media.HasVideo == Tri.No) throw new FlowException(ErrorCode.NoVideo);
            var result = new Selection { Container = options.Format };
            bool mp4 = options.Format == "mp4", h264 = options.Codec == "h264";
            var videos = usable.Where(f => f.HasVideo || (f.VideoUnknown && !Is(f.Ext, "mp3", "m4a", "opus", "ogg", "wav", "flac", "aac"))).ToList();
            if (videos.Count == 0) throw new FlowException(ErrorCode.NoVideo);
            // Frame-rate limit, when something satisfies it.
            if (options.FpsLimit > 0) { var fit = videos.Where(f => f.Fps <= 0 || f.Fps <= options.FpsLimit + 0.5).ToList(); if (fit.Count > 0) videos = fit; }
            // Codec: H.264 when asked (or conversion is needed); MP4 needs MP4-compatible streams.
            var pool = videos;
            if (h264) {
                var avc = videos.Where(f => IsH264(f.VCodec)).ToList();
                var unknown = videos.Where(f => f.VideoUnknown).ToList();
                if (avc.Count > 0) pool = avc;
                else if (unknown.Count > 0) { pool = unknown; result.ConvertIfNeeded = true; result.Notes.Add("sel.note.codecUnknown"); }
                else { result.VideoConversion = true; result.Notes.Add("sel.note.convertVideo"); }
            } else if (mp4) {
                var compatible = videos.Where(f => f.VideoUnknown ? f.Ext == "mp4" : Is(f.VCodec, Mp4Video)).ToList();
                if (compatible.Count > 0) pool = compatible;
            }
            // Quality limit on the short side.
            var known = pool.Where(f => f.ShortSide > 0).ToList();
            List<FormatInfo> candidates;
            if (options.Height <= 0) candidates = known.Count > 0 ? known : pool;
            else {
                candidates = known.Where(f => f.ShortSide <= options.Height).ToList();
                if (candidates.Count == 0 && known.Count == 0) candidates = pool;   // sizes unknown: cannot apply the limit
                if (candidates.Count == 0) {
                    var lowest = known.OrderBy(f => f.ShortSide).First();
                    if (options.Strict) throw new FlowException(ErrorCode.QualityUnavailable, null, new[] { lowest.ShortSide + "p" });
                    candidates = known.Where(f => f.ShortSide == lowest.ShortSide).ToList();
                    result.AboveLimit = true; result.Notes.Add("sel.note.aboveLimit");
                }
            }
            bool small = options.Profile == "small";
            // MP4 favours compatibility; MKV follows quality-per-bit (AV1 > VP9 > H.265 > H.264).
            Func<FormatInfo, int> codecRank = f => mp4 || h264 ? (IsH264(f.VCodec) ? 3 : Is(f.VCodec, "av01") ? 2 : Is(f.VCodec, "vp09", "vp9") ? 1 : 0)
                : (Is(f.VCodec, "av01") ? 4 : Is(f.VCodec, "vp09", "vp9") ? 3 : Is(f.VCodec, "hev1", "hvc1", "h265") ? 2 : IsH264(f.VCodec) ? 1 : 0);
            var ordered = candidates.OrderByDescending(f => f.ShortSide).ThenByDescending(f => Math.Round(f.Fps)).ThenByDescending(codecRank).ThenByDescending(f => f.HasAudio ? 0 : 1).ThenByDescending(ProtocolRank);
            var video = (small ? ordered.ThenBy(f => f.Tbr <= 0 ? double.MaxValue : f.Tbr) : ordered.ThenByDescending(f => f.Tbr)).First();
            result.Video = video;
            // Audio: a muxed format already has it; otherwise the best compatible audio stream.
            FormatInfo audio = null;
            if (!video.HasAudio) {
                var audios = usable.Where(f => f.HasAudio && !f.HasVideo && !f.VideoUnknown).ToList();
                if (mp4) {
                    var aac = audios.Where(f => IsAac(f.ACodec)).ToList();
                    var mp4ok = audios.Where(f => Is(f.ACodec, Mp4Audio)).ToList();
                    if (aac.Count > 0) audios = aac;
                    else if (h264 && audios.Count > 0) { result.VideoConversion = true; if (!result.Notes.Contains("sel.note.convertVideo")) result.Notes.Add("sel.note.convertAudio"); }
                    else if (mp4ok.Count > 0) audios = mp4ok;
                }
                Func<FormatInfo, double> rate = small ? new Func<FormatInfo, double>(f => -Math.Abs(AudioRate(f) - 128)) : mp4 ? new Func<FormatInfo, double>(AudioRate) : new Func<FormatInfo, double>(AudioScore);
                audio = audios.OrderByDescending(AudioRank).ThenByDescending(rate).FirstOrDefault();
                if (audio == null) {
                    if (media.HasAudio == Tri.Yes && usable.Any(f => f.HasAudio && f.HasVideo)) {
                        // Only muxed files carry sound: prefer one of those over a silent stream.
                        var muxed = usable.Where(f => f.HasAudio && f.HasVideo && candidates.Contains(f)).OrderByDescending(f => f.ShortSide).ThenByDescending(f => f.Tbr).FirstOrDefault();
                        if (muxed != null) { video = muxed; result.Video = muxed; }
                        else { result.Silent = true; result.Notes.Add("sel.note.silentAtLimit"); }
                    } else if (media.HasAudio == Tri.Unknown) { result.AudioUncertain = true; result.Notes.Add("sel.note.audioUncertain"); }
                    else { result.Silent = true; result.Notes.Add("sel.note.silent"); }
                }
            }
            // A muxed file keeps its own sound; for H.264 MP4 it must be AAC, or the final check would fail.
            if (h264 && mp4 && audio == null && video.HasAudio && !result.VideoConversion && !result.ConvertIfNeeded) {
                if (string.IsNullOrEmpty(video.ACodec)) result.ConvertIfNeeded = true;
                else if (!IsAac(video.ACodec)) { result.VideoConversion = true; result.Notes.Add("sel.note.convertAudio"); }
            }
            result.Audio = audio;
            result.Spec = video.Id + (audio != null ? "+" + audio.Id : "");
            result.Width = video.Width; result.Height = video.Height; result.ShortSide = video.ShortSide; result.Fps = video.Fps;
            result.VCodec = video.VCodec; result.ACodec = audio != null ? audio.ACodec : video.HasAudio ? video.ACodec : null;
            if (options.Height > 0 && video.ShortSide <= 0) result.Notes.Add("sel.note.sizeUnknownLimit");
            SetSize(result, media, options, video, audio);
            return result;
        }

        static void SetSize(Selection result, MediaInfo media, JobOptions options, FormatInfo a, FormatInfo b) {
            double duration = media.Duration;
            long sa = a.Size(duration), sb = b == null ? 0 : b.Size(duration);
            if (sa < 0 || sb < 0) { result.Bytes = -1; result.SizeExact = false; }
            else {
                result.Bytes = sa + sb; result.SizeExact = a.SizeExact && (b == null || b.SizeExact);
                if (options.HasClip && duration > 0) { result.Bytes = (long)(result.Bytes * Math.Min(1, (options.ClipEnd - options.ClipStart) / duration)); result.SizeExact = false; }
            }
        }

        // Engine fallback used only if the chosen ids disappear between inspection and download.
        public static string Fallback(JobOptions options) {
            if (options.IsAudio) return "ba/b";
            if (options.Format == "mp4") return options.Codec == "h264" ? "bv*[vcodec^=avc1]+ba[acodec^=mp4a]/b[vcodec^=avc1]/bv*+ba/b" : "bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b";
            return "bv*+ba/b";
        }
        public static string SortFor(JobOptions options) {
            if (options.IsAudio) return null;
            var parts = new List<string>();
            if (options.Codec == "h264") parts.Add("vcodec:h264");
            if (options.Height > 0) parts.Add("res:" + options.Height.ToString(CultureInfo.InvariantCulture));
            if (options.FpsLimit > 0) parts.Add("fps:" + options.FpsLimit.ToString(CultureInfo.InvariantCulture));
            return parts.Count == 0 ? null : string.Join(",", parts);
        }
    }

    // Output folder and file name, decided before the download so collisions and path length can be checked.
    public static class OutputPlanner {
        public static string Folder(JobOptions options, DownloadJob job) {
            var folder = options.Folder;
            var platform = string.IsNullOrEmpty(job.Platform) ? "Other" : job.Platform;
            if (options.Organize == "platform" || options.Organize == "platform-channel") folder = Path.Combine(folder, Naming.Sanitize(platform == "Other" ? "Other sites" : platform));
            if (options.Organize == "platform-channel" && !string.IsNullOrWhiteSpace(job.Channel)) folder = Path.Combine(folder, Naming.Sanitize(job.Channel));
            if (options.CollectionFolder && !string.IsNullOrWhiteSpace(job.CollectionTitle)) folder = Path.Combine(folder, Naming.Sanitize(job.CollectionTitle));
            return folder;
        }
        public static string Extension(JobOptions options) { return options.Format; }
        public static string Name(JobOptions options, DownloadJob job, Selection selection, DateTime now) {
            var quality = options.IsAudio ? "" : Naming.QualityTag(selection != null ? selection.ShortSide : 0, selection != null ? selection.Fps : 0);
            var values = new Dictionary<string, string> {
                { "title", job.Title }, { "id", job.ContentId }, { "channel", job.Channel }, { "platform", job.Platform == "Other" ? "Web" : job.Platform },
                { "quality", quality }, { "date", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                { "index", job.CollectionIndex > 0 ? job.CollectionIndex.ToString("000", CultureInfo.InvariantCulture) : "" }, { "playlist", job.CollectionTitle }
            };
            var template = Naming.IsValidTemplate(options.NameTemplate) ? options.NameTemplate : SettingsRules.DefaultTemplate;
            var name = Naming.Expand(template, values);
            // Different qualities, profiles and ranges of the same content never share a name.
            if (!template.Contains("{quality}") && quality.Length > 0) name += " " + quality;
            if (options.Profile == "small" && !options.IsAudio) name += " small";
            if (options.HasClip) name += " " + Naming.ClipTag(options.ClipStart, options.ClipEnd);
            return Naming.Sanitize(name);
        }
        // Keeps the full path under the classic Windows limit by shortening the name, not the folder.
        public static string Fit(string folder, string name, string ext) {
            const int limit = 240;
            // 8 = " (999)" collision suffix. While downloading the name sits in .flow-parts\<id>\ with a format and
            // .part suffix: about 56 characters more, of which the 20 between this limit and Windows' 260 are spare.
            const int partsOverhead = 36;
            int room = limit - folder.Length - ext.Length - 2 - 8 - partsOverhead;
            if (room < 24) throw new FlowException(ErrorCode.PathTooLong);
            return name.Length > room ? Naming.Truncate(name, room - 1) : name;
        }
    }
}
