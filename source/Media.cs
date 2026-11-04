using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Flow {
    // Yes / No / Unknown: "unknown" must never be shown as "not available".
    public enum Tri { Unknown, Yes, No }

    public class FormatInfo {
        public string Id, Ext, VCodec, ACodec, Protocol, Note, Language;
        public int Width, Height;
        public double Fps, Tbr, Abr, Vbr;
        public long FileSize, FileSizeApprox;
        public bool Drm;
        public int LanguagePreference;
        public bool HasVideo { get { return VCodec != null && VCodec != "none"; } }
        public bool VideoUnknown { get { return VCodec == null; } }
        public bool HasAudio { get { return ACodec != null && ACodec != "none"; } }
        public bool AudioUnknown { get { return ACodec == null; } }
        public bool IsStoryboard { get { return Ext == "mhtml" || (Note ?? "").ToLowerInvariant().Contains("storyboard"); } }
        // Quality class uses the short side, so 1080x1920 is "1080p", not "1920p".
        public int ShortSide { get { return Width > 0 && Height > 0 ? Math.Min(Width, Height) : Height; } }
        public long Size(double duration) {
            if (FileSize > 0) return FileSize;
            if (FileSizeApprox > 0) return FileSizeApprox;
            if (Tbr > 0 && duration > 0) return (long)(Tbr * 1000 / 8 * duration);
            return -1;
        }
        public bool SizeExact { get { return FileSize > 0; } }
    }

    public class SubtitleTrack {
        public string Lang, Name;
        public bool Automatic;
        public bool IsOriginal { get { return Lang != null && Lang.EndsWith("-orig", StringComparison.Ordinal); } }
    }

    public class Chapter { public double Start, End; public string Title; }

    public class MediaInfo {
        public string Id, Title, Channel, Thumbnail, ExtractorKey, WebpageUrl, Availability, LiveStatus;
        public Platform Platform;
        public double Duration;
        public int Width, Height;
        public double Fps;
        public bool IsLive, IsCollection, Drm;
        public int ItemIndex;          // position inside a multi-item post (1-based), 0 = not a collection item
        public List<FormatInfo> Formats = new List<FormatInfo>();
        public List<SubtitleTrack> Subtitles = new List<SubtitleTrack>();
        public List<Chapter> Chapters = new List<Chapter>();

        public IEnumerable<FormatInfo> Usable { get { return Formats.Where(f => !f.IsStoryboard && !f.Drm && !(f.VCodec == "none" && f.ACodec == "none")); } }
        public Tri HasVideo {
            get {
                if (Usable.Any(f => f.HasVideo)) return Tri.Yes;
                if (Usable.Any(f => f.VideoUnknown && (f.Ext == null || !new[] { "mp3", "m4a", "opus", "ogg", "wav", "flac", "aac" }.Contains(f.Ext)))) return Tri.Unknown;
                if (Formats.Count == 0) return Width > 0 || Height > 0 ? Tri.Yes : Tri.Unknown;
                return Tri.No;
            }
        }
        public Tri HasAudio {
            get {
                if (Usable.Any(f => f.HasAudio)) return Tri.Yes;
                if (Usable.Any(f => f.AudioUnknown)) return Tri.Unknown;
                return Formats.Count == 0 ? Tri.Unknown : Tri.No;
            }
        }
        // Largest known picture; used for "1080×1920, vertical" labels.
        public FormatInfo BestVideo { get { return Usable.Where(f => f.HasVideo && f.Width > 0 && f.Height > 0).OrderByDescending(f => f.ShortSide).ThenByDescending(f => f.Fps).ThenByDescending(f => f.Tbr).FirstOrDefault(); } }
        public string Orientation {
            get {
                int w = Width, h = Height; var best = BestVideo;
                if (best != null) { w = best.Width; h = best.Height; }
                if (w <= 0 || h <= 0) return null;
                return h > w ? "vertical" : w > h ? "horizontal" : "square";
            }
        }
    }

    public class CollectionEntry {
        public int Index;
        public string Id, Title, Url, Thumbnail, Availability;
        public double Duration;
        public Tri IsVideo;
        public bool Available { get { return !(Availability == "private" || Availability == "needs_auth" || Availability == "premium_only" || Availability == "subscriber_only") && Title != "[Private video]" && Title != "[Deleted video]"; } }
    }

    public class CollectionInfo {
        public string Id, Title, Channel, ExtractorKey;
        public Platform Platform;
        public int TotalCount;        // -1 when the site does not report it
        public List<CollectionEntry> Entries = new List<CollectionEntry>();
    }

    public static class MediaParser {
        static Dictionary<string, object> Obj(object value) { return value as Dictionary<string, object>; }
        static IEnumerable<object> List(object value) { var list = value as IEnumerable; return list == null || value is string ? Enumerable.Empty<object>() : list.Cast<object>(); }

        public static Dictionary<string, object> Read(string json) {
            var data = Engine.Json().DeserializeObject(json) as Dictionary<string, object>;
            if (data == null) throw new FlowException(ErrorCode.NoVideo);
            return data;
        }

        // A single media item. For a multi-item result the first entry is used unless an index is given.
        public static MediaInfo Parse(string json, Platform fallback) { return Parse(Read(json), fallback); }
        public static MediaInfo Parse(Dictionary<string, object> data, Platform fallback) {
            bool collection = false;
            int index = 0;
            for (int depth = 0; depth < 5; depth++) {
                if (data == null) throw new FlowException(ErrorCode.NoVideo);
                object entries;
                if (!data.TryGetValue("entries", out entries)) break;
                collection = true;
                var first = List(entries).FirstOrDefault();
                if (first == null) throw new FlowException(List(entries).Any() ? ErrorCode.FirstItemUnavailable : ErrorCode.CollectionEmpty);
                data = Obj(first);
            }
            if (data == null || data.ContainsKey("entries")) throw new FlowException(ErrorCode.FirstItemUnavailable);
            if (collection) index = Math.Max(1, (int)Engine.Number(data, "playlist_index", 1));
            var info = new MediaInfo {
                Id = Engine.Text(data, "id", ""),
                Title = Engine.Text(data, "title", "Video"),
                Channel = Engine.Text(data, "channel", Engine.Text(data, "uploader", Engine.Text(data, "creator", ""))),
                Duration = Engine.Number(data, "duration"),
                Thumbnail = Engine.Text(data, "thumbnail", ""),
                ExtractorKey = Engine.Text(data, "extractor_key", ""),
                WebpageUrl = Engine.Text(data, "webpage_url", ""),
                Availability = Engine.Text(data, "availability", null),
                LiveStatus = Engine.Text(data, "live_status", null),
                Width = (int)Engine.Number(data, "width"),
                Height = (int)Engine.Number(data, "height"),
                Fps = Engine.Number(data, "fps"),
                Drm = Engine.Flag(data, "_has_drm"),
                IsCollection = collection,
                ItemIndex = index
            };
            info.IsLive = Engine.Flag(data, "is_live") || info.LiveStatus == "is_live" || info.LiveStatus == "is_upcoming";
            info.Platform = Platforms.FromExtractor(info.ExtractorKey, fallback);
            foreach (var raw in List(data.ContainsKey("formats") ? data["formats"] : null)) {
                var f = Obj(raw); if (f == null) continue;
                info.Formats.Add(new FormatInfo {
                    Id = Engine.Text(f, "format_id", null), Ext = Engine.Text(f, "ext", null),
                    VCodec = Engine.Text(f, "vcodec", null), ACodec = Engine.Text(f, "acodec", null),
                    Protocol = Engine.Text(f, "protocol", null), Note = Engine.Text(f, "format_note", null), Language = Engine.Text(f, "language", null),
                    Width = (int)Engine.Number(f, "width"), Height = (int)Engine.Number(f, "height"),
                    Fps = Engine.Number(f, "fps"), Tbr = Engine.Number(f, "tbr"), Abr = Engine.Number(f, "abr"), Vbr = Engine.Number(f, "vbr"),
                    FileSize = (long)Engine.Number(f, "filesize"), FileSizeApprox = (long)Engine.Number(f, "filesize_approx"),
                    Drm = Engine.Flag(f, "has_drm"), LanguagePreference = (int)Engine.Number(f, "language_preference", -1)
                });
            }
            info.Formats.RemoveAll(f => string.IsNullOrEmpty(f.Id));
            // A result without a format list is a single progressive file.
            if (info.Formats.Count == 0 && data.ContainsKey("url")) {
                info.Formats.Add(new FormatInfo { Id = Engine.Text(data, "format_id", "0"), Ext = Engine.Text(data, "ext", null), VCodec = Engine.Text(data, "vcodec", null), ACodec = Engine.Text(data, "acodec", null), Width = info.Width, Height = info.Height, Fps = info.Fps, Tbr = Engine.Number(data, "tbr"), FileSize = (long)Engine.Number(data, "filesize"), FileSizeApprox = (long)Engine.Number(data, "filesize_approx") });
            }
            AddSubtitles(info, Obj(data.ContainsKey("subtitles") ? data["subtitles"] : null), false);
            AddSubtitles(info, Obj(data.ContainsKey("automatic_captions") ? data["automatic_captions"] : null), true);
            foreach (var raw in List(data.ContainsKey("chapters") ? data["chapters"] : null)) {
                var c = Obj(raw); if (c == null) continue;
                var chapter = new Chapter { Start = Engine.Number(c, "start_time"), End = Engine.Number(c, "end_time"), Title = Engine.Text(c, "title", "") };
                if (chapter.End > chapter.Start) info.Chapters.Add(chapter);
            }
            return info;
        }

        static void AddSubtitles(MediaInfo info, Dictionary<string, object> tracks, bool automatic) {
            if (tracks == null) return;
            foreach (var pair in tracks) {
                if (pair.Key == "live_chat" || pair.Key == "danmaku") continue;
                var variants = List(pair.Value).Select(Obj).Where(v => v != null).ToList();
                if (variants.Count == 0) continue;
                info.Subtitles.Add(new SubtitleTrack { Lang = pair.Key, Name = Engine.Text(variants[0], "name", pair.Key), Automatic = automatic });
            }
        }

        public static CollectionInfo ParseCollection(string json, Platform fallback) {
            var data = Read(json);
            var result = new CollectionInfo {
                Id = Engine.Text(data, "id", ""), Title = Engine.Text(data, "title", ""),
                Channel = Engine.Text(data, "channel", Engine.Text(data, "uploader", "")),
                ExtractorKey = Engine.Text(data, "extractor_key", ""),
                TotalCount = (int)Engine.Number(data, "playlist_count", -1)
            };
            result.Platform = Platforms.FromExtractor(result.ExtractorKey, fallback);
            object entries;
            if (!data.TryGetValue("entries", out entries)) return result;
            int position = 0;
            foreach (var raw in List(entries)) {
                position++;
                var e = Obj(raw);
                if (e == null) { result.Entries.Add(new CollectionEntry { Index = position, Title = null, Availability = "unavailable", IsVideo = Tri.Unknown }); continue; }
                var entry = new CollectionEntry {
                    Index = (int)Engine.Number(e, "playlist_index", position),
                    Id = Engine.Text(e, "id", ""), Title = Engine.Text(e, "title", null),
                    Url = Engine.Text(e, "webpage_url", Engine.Text(e, "url", null)),
                    Duration = Engine.Number(e, "duration"),
                    Availability = Engine.Text(e, "availability", null),
                    Thumbnail = Engine.Text(e, "thumbnail", null)
                };
                // Full entries (e.g. Instagram posts) carry formats; flat entries (YouTube lists) do not.
                if (e.ContainsKey("formats") || e.ContainsKey("vcodec")) {
                    var media = Parse(e, result.Platform);
                    entry.IsVideo = media.HasVideo == Tri.No ? Tri.No : media.HasVideo;
                    if (entry.Duration <= 0) entry.Duration = media.Duration;
                } else entry.IsVideo = Tri.Unknown;
                result.Entries.Add(entry);
            }
            return result;
        }
    }
}
