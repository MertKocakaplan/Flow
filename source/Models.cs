using System;
using System.Collections.Generic;

namespace Flow {
    public enum Platform { YouTube, Instagram, TikTok, Other }

    // Language-neutral outcome codes. User-facing text is produced by Loc ("err." + code).
    public enum ErrorCode {
        None,
        UrlEmpty, UrlInvalid, UrlNotPublic, YouTubeBadLink, ChannelUnsupported, ProfileUnsupported, PhotoUnsupported, TikTokPageUnsupported, PlaylistInvalid,
        ToolsMissing, Unsupported, Drm, IpBlocked, LoginRequired, NoVideo, NoAudio, FormatUnavailable, QualityUnavailable, Unavailable,
        Network, RateLimited, Forbidden, WriteDenied, DiskFull, DiskSpaceLow, Timeout, Live, CollectionEmpty, FirstItemUnavailable,
        OutputMissing, VerifyFailed, FragmentsMissing, FolderInvalid, FormatInvalid, ClipInvalid, PathTooLong,
        EngineUpdateFailed, UpdateBusy, Interrupted, Canceled, ScheduleInvalid, SchedulePast, Unknown, ImageUnsupported, ImageTooLarge
    }

    public sealed class FlowException : Exception {
        public readonly ErrorCode Code;
        public readonly string[] Args;
        public readonly string Detail;
        public FlowException(ErrorCode code) : this(code, null, null) {}
        public FlowException(ErrorCode code, string detail) : this(code, detail, null) {}
        public FlowException(ErrorCode code, string detail, string[] args) : base(code.ToString() + (detail == null ? "" : ": " + detail)) { Code = code; Detail = detail; Args = args ?? new string[0]; }
    }

    public static class Errors {
        // Transient codes are retried automatically a limited number of times.
        public static bool IsTransient(ErrorCode code) {
            return code == ErrorCode.Network || code == ErrorCode.Timeout || code == ErrorCode.RateLimited || code == ErrorCode.FragmentsMissing || code == ErrorCode.Interrupted;
        }
        // These need a user decision (another folder, format or free space); retrying alone will not help.
        public static bool NeedsUser(ErrorCode code) {
            return code == ErrorCode.DiskFull || code == ErrorCode.DiskSpaceLow || code == ErrorCode.WriteDenied || code == ErrorCode.FormatUnavailable || code == ErrorCode.QualityUnavailable || code == ErrorCode.NoAudio || code == ErrorCode.FolderInvalid || code == ErrorCode.PathTooLong || code == ErrorCode.ToolsMissing;
        }
        public static ErrorCode FromException(Exception ex) {
            var flow = ex as FlowException;
            if (flow != null) return flow.Code;
            if (ex is OperationCanceledException) return ErrorCode.Canceled;
            if (ex is TimeoutException) return ErrorCode.Timeout;
            if (ex is UnauthorizedAccessException) return ErrorCode.WriteDenied;
            if (ex is System.IO.PathTooLongException) return ErrorCode.PathTooLong;
            var io = ex as System.IO.IOException;
            if (io != null) {
                int hr = System.Runtime.InteropServices.Marshal.GetHRForException(io) & 0xFFFF;
                if (hr == 112 || hr == 39) return ErrorCode.DiskFull;
                if (hr == 5) return ErrorCode.WriteDenied;
            }
            return ErrorCode.Unknown;
        }
    }

    // ---- Persisted data (schema-versioned; dates are ISO 8601 strings, enums are strings) ----

    public class FlowSettings {
        public const int CurrentSchema = 1;
        public int SchemaVersion { get; set; }
        public string Language { get; set; }        // system | tr | en
        public string Theme { get; set; }           // system | light | dark
        public string Folder { get; set; }
        public string Profile { get; set; }         // compatible | best | small | mp3 | m4a | custom
        public string Format { get; set; }          // mp4 | mkv | mp3 | m4a
        public int Height { get; set; }             // 0 = best; otherwise upper bound on the short side
        public bool StrictQuality { get; set; }
        public string Codec { get; set; }           // auto | h264
        public int FpsLimit { get; set; }           // 0 = source
        public string Organize { get; set; }        // none | platform | platform-channel
        public string NameTemplate { get; set; }
        public string Duplicate { get; set; }       // skip | rename
        public int Parallel { get; set; }
        public int Retries { get; set; }
        public long SpeedLimit { get; set; }        // bytes/s, 0 = unlimited
        public bool AutoPreview { get; set; }
        public bool ResumeInterrupted { get; set; }
        public string MissedSchedule { get; set; }  // start | hold
        public bool MinimizeToTray { get; set; }
        public bool CloseToTray { get; set; }
        public bool Notify { get; set; }
        public bool WindowsTask { get; set; }
        public bool TrayHintShown { get; set; }
        public bool CollectionFolder { get; set; }
        public string LastTab { get; set; }
        public Dictionary<string, string> TabProfiles { get; set; }
        public string ImportedFrom { get; set; }
        public string ImportedAt { get; set; }
        public int ImportedCount { get; set; }
        public bool ImportNoticeShown { get; set; }
    }

    public class HistoryEntry {
        public string MediaKind { get; set; }
        public string Referrer { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public string Platform { get; set; }        // YouTube | Instagram | TikTok | Other | null (unknown/legacy)
        public string SourceUrl { get; set; }       // null when unknown; never fabricated
        public string ContentId { get; set; }
        public string Channel { get; set; }
        public string FilePath { get; set; }
        public string CompletedAt { get; set; }     // ISO 8601 UTC
        public long SizeBytes { get; set; }
        public string Format { get; set; }
        public string Profile { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double Duration { get; set; }
        public string Status { get; set; }          // completed | failed | canceled | skipped
        public string Error { get; set; }           // ErrorCode name
        public string Clip { get; set; }
        public int ItemIndex { get; set; }
        public bool Legacy { get; set; }
    }

    public class HistoryFile {
        public const int CurrentSchema = 1;
        public int SchemaVersion { get; set; }
        public List<HistoryEntry> Entries { get; set; }
    }

    public static class Iso {
        public static string Now() { return DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture); }
        public static string From(DateTime value) { return value.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture); }
        public static DateTime? Parse(string value) {
            DateTime result;
            if (!string.IsNullOrEmpty(value) && DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out result)) return result.ToUniversalTime();
            return null;
        }
    }
}
