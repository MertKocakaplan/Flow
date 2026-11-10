using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;

namespace Flow {
    public enum UrlKind { Single, Collection }

    public sealed class UrlInfo {
        public string Url;
        public string Referrer;
        public Platform Platform;
        public UrlKind Kind;
        public string VideoId;   // YouTube only
        public string ListId;    // YouTube playlist id; on a watch URL the list is offered, not implied
    }

    // Source detection and URL rules per platform. The engine decides real support;
    // these rules only reject what is invalid, local, or explicitly outside Flow's scope.
    public static class Platforms {
        static readonly Regex YouTubeId = new Regex(@"^[A-Za-z0-9_-]{11}$");
        static readonly Regex ListIdPattern = new Regex(@"^[A-Za-z0-9_-]{10,64}$");

        public static Platform FromHost(string host) {
            if (string.IsNullOrEmpty(host)) return Platform.Other;
            host = host.TrimEnd('.').ToLowerInvariant();
            if (host == "youtu.be" || host == "www.youtu.be" || host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal) || host == "youtube-nocookie.com" || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal)) return Platform.YouTube;
            if (host == "instagram.com" || host.EndsWith(".instagram.com", StringComparison.Ordinal) || host == "instagr.am" || host == "www.instagr.am") return Platform.Instagram;
            if (host == "tiktok.com" || host.EndsWith(".tiktok.com", StringComparison.Ordinal)) return Platform.TikTok;
            return Platform.Other;
        }

        // Lenient detection while the user is typing; used only to pick a tab.
        public static Platform? Detect(string text) {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (!text.Contains("://")) text = "https://" + text;
            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri) || uri.HostNameType != UriHostNameType.Dns || !uri.Host.Contains(".")) return null;
            return FromHost(uri.IdnHost);
        }

        public static Platform FromExtractor(string extractorKey, Platform fallback) {
            if (string.IsNullOrEmpty(extractorKey)) return fallback;
            var key = extractorKey.ToLowerInvariant();
            if (key.StartsWith("youtube")) return Platform.YouTube;
            if (key.StartsWith("instagram")) return Platform.Instagram;
            if (key.StartsWith("tiktok")) return Platform.TikTok;
            if (key == "generic") return fallback;
            return Platform.Other;
        }

        public static bool TryPublicWebUrl(string value, out Uri uri) {
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || uri.IsLoopback || uri.HostNameType != UriHostNameType.Dns) return false;
            var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            if (!host.Contains(".") || Uri.CheckHostName(host) != UriHostNameType.Dns) return false;
            return !new[] { "localhost", "local", "internal", "lan", "home", "arpa" }.Any(suffix => host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal));
        }

        // Links inside pasted text: several lines of links, or a link inside a sentence. Order kept, duplicates dropped.
        static readonly Regex LinkPattern = new Regex(@"https?://[^\s<>""'“”‘’]+", RegexOptions.IgnoreCase);
        public const int MaxLinks = 200;
        public static List<string> ExtractUrls(string text) {
            var found = new List<string>();
            foreach (Match m in LinkPattern.Matches(text ?? "")) {
                var link = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', '»', '›');
                // A closing bracket belongs to the link only when the link opened one (Wikipedia-style paths).
                while ((link.EndsWith(")") && link.Count(c => c == '(') < link.Count(c => c == ')')) || (link.EndsWith("]") && !link.Contains("["))) link = link.Substring(0, link.Length - 1).TrimEnd('.', ',', ';', ':', '!', '?');
                if (link.Length > 10 && !found.Contains(link)) found.Add(link);
                if (found.Count >= MaxLinks) break;
            }
            return found;
        }
        // Pasted text that is more than one plain link becomes its links, separated by spaces.
        public static string NormalizePasted(string text) {
            var trimmed = (text ?? "").Trim();
            var links = ExtractUrls(trimmed);
            if (links.Count == 0 || (links.Count == 1 && links[0] == trimmed)) return trimmed;
            return string.Join(" ", links);
        }

        public static UrlInfo Parse(string value) {
            if (string.IsNullOrWhiteSpace(value)) throw new FlowException(ErrorCode.UrlEmpty);
            value = value.Trim();
            if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || value.Contains("\\")) throw new FlowException(ErrorCode.UrlInvalid);
            if (!value.Contains("://")) value = "https://" + value;
            Uri uri;
            if (!TryPublicWebUrl(value, out uri)) throw new FlowException(ErrorCode.UrlNotPublic);
            var platform = FromHost(uri.IdnHost);
            var parts = uri.AbsolutePath.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var info = new UrlInfo { Url = uri.AbsoluteUri, Platform = platform, Kind = UrlKind.Single };
            if (platform == Platform.YouTube) return YouTube(uri, parts);
            // Preserve third-party paths and query strings: short links and signed URLs need them.
            if (platform == Platform.Instagram) {
                var first = parts.Length == 0 ? "" : parts[0].ToLowerInvariant();
                int media = Array.FindIndex(parts, p => new[] { "p", "reel", "reels", "tv" }.Contains(p.ToLowerInvariant()));
                if (first == "stories" || first == "explore" || (media < 0 && parts.Length <= 2)) throw new FlowException(ErrorCode.ProfileUnsupported);
                if (media < 0 || media + 1 >= parts.Length) throw new FlowException(ErrorCode.ProfileUnsupported);
            } else if (platform == Platform.TikTok) {
                var lower = parts.Select(p => p.ToLowerInvariant()).ToArray();
                if (lower.Length >= 2 && lower[0].StartsWith("@") && lower[1] == "photo") throw new FlowException(ErrorCode.PhotoUnsupported);
                if (lower.Length >= 1 && new[] { "tag", "music", "effect", "discover", "search" }.Contains(lower[0])) throw new FlowException(ErrorCode.TikTokPageUnsupported);
                if (lower.Length == 1 && lower[0].StartsWith("@")) throw new FlowException(ErrorCode.ProfileUnsupported);
            }
            return info;
        }

        static UrlInfo YouTube(Uri uri, string[] parts) {
            var host = uri.IdnHost.ToLowerInvariant();
            var query = HttpUtility.ParseQueryString(uri.Query);
            string id = null, list = query["list"];
            if (host == "youtu.be" || host == "www.youtu.be") {
                if (parts.Length == 1) id = parts[0];
            } else if (parts.Length == 1 && parts[0] == "watch") {
                id = query["v"];
            } else if (parts.Length == 1 && parts[0] == "playlist") {
                if (list == null || !ListIdPattern.IsMatch(list) || list == "WL" || list == "LL" || list == "LM") throw new FlowException(ErrorCode.PlaylistInvalid);
                return new UrlInfo { Url = "https://www.youtube.com/playlist?list=" + list, Platform = Platform.YouTube, Kind = UrlKind.Collection, ListId = list };
            } else if (parts.Length == 2 && new[] { "shorts", "live", "embed", "v" }.Contains(parts[0])) {
                id = parts[1];
            } else if (parts.Length >= 1 && (parts[0].StartsWith("@") || new[] { "channel", "c", "user" }.Contains(parts[0]))) {
                throw new FlowException(ErrorCode.ChannelUnsupported);
            }
            if (id == null || !YouTubeId.IsMatch(id)) throw new FlowException(ErrorCode.YouTubeBadLink);
            return new UrlInfo { Url = "https://www.youtube.com/watch?v=" + id, Platform = Platform.YouTube, Kind = UrlKind.Single, VideoId = id, ListId = list != null && ListIdPattern.IsMatch(list) && !list.StartsWith("RD") ? list : null };
        }
    }
}
