using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flow.BrowserIntegration;

namespace Flow {
    // All state and queue mutations run on Flow's dispatcher. The browser cannot supply paths,
    // engine arguments, executable names, cookies, or uninspected download options.
    public sealed class BrowserApi {
        readonly QueueService queue;
        readonly Func<FlowSettings> settings;
        readonly Func<UrlInfo, CancellationToken, Task<MediaInfo>> inspect;
        readonly Action show;
        readonly Dictionary<string, Preview> previews = new Dictionary<string, Preview>();
        int inspecting;
        sealed class Preview { public string Url,Referrer; public MediaInfo Media; public ImageMetadata Image; public DateTime Expires; public List<Choice> Choices; }
        internal Func<string,string,string,CancellationToken,Task<ImageMetadata>> InspectImage = ImageTransfer.Inspect;
        // The extension's version as it reports it (1.2.0 and later), the packaged version Flow expects, and a
        // one-time request to reload from disk.
        public Action<bool, string> Seen = (ping, version) => {};
        public Func<string> ExpectedExtension = () => null;
        public Func<bool> TakeReload = () => false;
        public Func<bool> OpenDownloads = () => false;
        public sealed class Choice {
            public string key, format, profile, summary, warning;
            public int height;
            public long bytes;
            public bool sizeExact, conversion;
        }
        public BrowserApi(QueueService queue, Func<FlowSettings> settings, Func<UrlInfo, CancellationToken, Task<MediaInfo>> inspect, Action show) { this.queue = queue; this.settings = settings; this.inspect = inspect; this.show = show; }
        public async Task<string> Handle(string json, CancellationToken token) {
            try {
                var request = BrowserProtocol.Json().DeserializeObject(json) as Dictionary<string, object>;
                if (request == null || !request.ContainsKey("v") || Convert.ToString(request["v"], CultureInfo.InvariantCulture) != "1") return BrowserProtocol.Error("PROTOCOL_VERSION");
                var action = Text(request, "action", 32);
                object data = null;
                if (action != "ping") Seen(false, null);
                if (action == "ping") {
                    var reported = AppUpdates.Clean(OptionalText(request, "ext", 32));
                    Seen(true, reported);
                    data = new { version = Program.Version, protocol = 1, capabilities=new[]{"media-discovery","image-original","open-folder"}, language = Loc.Lang, theme = Theme.Current, folder = settings().Folder, pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                        extension = new { expected = ExpectedExtension(), reload = TakeReload() } };
                }
                else if (action == "inspect") {
                    if (inspecting >= 2) return BrowserProtocol.Error("BUSY");
                    var url = Platforms.Parse(Text(request, "url", 8192));
                    url.Referrer=ImageTransfer.Referrer(OptionalText(request,"referrer",8192));
                    if (url.Kind == UrlKind.Collection) return BrowserProtocol.Error("COLLECTION_DESKTOP");
                    MediaInfo media;ImageMetadata image=null;
                    inspecting++;
                    try {
                        if(OptionalText(request,"kind",16)=="image"){
                            image=await InspectImage(url.Url,OptionalText(request,"title",220),url.Referrer,token);
                            media=new MediaInfo { Title=image.Title,Id=image.Id,Platform=Platform.Other };
                        }else media = await inspect(url, token);
                    } finally { inspecting--; }
                    if (media.IsLive) throw new FlowException(ErrorCode.Live);
                    if (media.Drm) throw new FlowException(ErrorCode.Drm);
                    // Multi-item posts stay explicit in the desktop collection UI.
                    if (media.IsCollection) return BrowserProtocol.Error("COLLECTION_DESKTOP");
                    var choices = image==null?BuildChoices(media, settings()):new List<Choice> { new Choice {key="original:0",format="original",profile="custom",bytes=image.Bytes,sizeExact=image.Bytes>0,summary=image.Extension.ToUpperInvariant(),warning=Loc.T("note.imageOriginal") } };
                    if (choices.Count == 0) throw new FlowException(ErrorCode.NoVideo);
                    foreach (var key in previews.Where(p => p.Value.Expires <= DateTime.UtcNow).Select(p => p.Key).ToArray()) previews.Remove(key);
                    while (previews.Count >= 12) previews.Remove(previews.OrderBy(p => p.Value.Expires).First().Key);
                    var previewId = Guid.NewGuid().ToString("N");
                    previews[previewId] = new Preview { Url = url.Url,Referrer=url.Referrer,Image=image, Media = media, Expires = DateTime.UtcNow.AddMinutes(10), Choices = choices };
                    data = new { token = previewId, title = Trim(media.Title, 500), platform = media.Platform.ToString(), duration = media.Duration, choices = choices, expiresIn = 600, folder = settings().Folder };
                } else if (action == "enqueue") {
                    var requestId = Text(request, "requestId", 36); Guid guid;
                    if (!Guid.TryParseExact(requestId, "D", out guid)) return BrowserProtocol.Error("INVALID_REQUEST");
                    var existingId = queue.FindBrowserJobId(requestId);
                    if (existingId != null) return Success(new { jobId = existingId, duplicate = true });
                    Preview preview;
                    if (!previews.TryGetValue(Text(request, "token", 32), out preview) || preview.Expires <= DateTime.UtcNow) return BrowserProtocol.Error("PREVIEW_EXPIRED");
                    var choice = preview.Choices.FirstOrDefault(c => c.key == Text(request, "choice", 48));
                    if (choice == null) return BrowserProtocol.Error("INVALID_CHOICE");
                    var options = preview.Image==null?Options(settings(), choice.format, choice.height):JobOptions.From(settings());
                    if(preview.Image==null)FormatSelector.Select(preview.Media, options);
                    else {options.Format=preview.Image.Extension;options.Profile="custom";options.Codec="auto";options.Height=0;options.FpsLimit=0;}
                    object held;
                    bool hold = request.TryGetValue("hold", out held) && held is bool && (bool)held;
                    // Persist while held, then start. A lost response can be retried with the same ID.
                    var job = new DownloadJob { BrowserRequestId = requestId, Url = preview.Url, Referrer=preview.Referrer,MediaKind=preview.Image==null?null:"image",Platform = preview.Media.Platform.ToString(), Options = options, Title = preview.Media.Title, ContentId = preview.Media.Id, Channel = preview.Media.Channel, Duration = preview.Media.Duration };
                    var item = queue.Add(job, true, preview.Media);
                    try { queue.SaveRequired(); }
                    catch (Exception) { queue.Remove(item); return BrowserProtocol.Error("QUEUE_SAVE_FAILED"); }
                    if (!hold) queue.Resume(item);
                    data = new { jobId = job.Id, duplicate = false };
                } else if (action == "queue") {
                    var rows = queue.Items.OrderBy(i => JobState.IsFinished(i.Job.State) ? 1 : 0).ThenByDescending(i => i.Job.CreatedAt).Take(100).Select(i => new {
                        id = i.Job.Id, title = Trim(i.Title, 350), platform = i.Job.Platform, state = i.Job.State,
                        stateText = i.StateText, detail = Trim(i.DetailText, 500), error = Trim(i.ErrorText, 800),
                        progress = i.ShowProgress && !i.Indeterminate ? (double?)i.ProgressValue : null,
                        canPause = i.CanPause, canResume = i.CanResume, canRetry = i.CanRetry, canCancel = i.CanCancel
                    }).ToArray();
                    data = new { items = rows, total = queue.Items.Count, active = queue.ActiveCount };
                } else if (new[] { "pause", "resume", "retry", "cancel" }.Contains(action)) {
                    var item = queue.Items.FirstOrDefault(i => i.Job.Id == Text(request, "jobId", 36));
                    if (item == null) return BrowserProtocol.Error("JOB_MISSING");
                    if (action == "pause" && item.CanPause) queue.Pause(item);
                    else if (action == "resume" && item.CanResume) queue.Resume(item);
                    else if (action == "retry" && item.CanRetry) queue.Retry(item);
                    else if (action == "cancel" && item.CanCancel) queue.Cancel(item);
                    else return BrowserProtocol.Error("INVALID_STATE");
                    data = new { jobId = item.Job.Id };
                } else if (action == "show") { show(); data = new { shown = true }; }
                // Opens Flow's own download folder. The browser names no path; the folder comes from Flow's settings.
                else if (action == "folder") { if (!OpenDownloads()) return BrowserProtocol.Error("FOLDER_MISSING"); data = new { opened = true }; }
                else return BrowserProtocol.Error("INVALID_ACTION");
                return Success(data);
            } catch (OperationCanceledException) { return BrowserProtocol.Error("TIMEOUT"); }
            catch (FlowException ex) { return BrowserProtocol.Json().Serialize(new { v = 1, ok = false, error = new { code = ex.Code.ToString(), message = Loc.Error(ex) } }); }
            catch (Exception) { return BrowserProtocol.Error("INVALID_REQUEST"); }
        }
        public static string Success(object data) { return BrowserProtocol.Json().Serialize(new { v = 1, ok = true, data = data }); }
        static string Text(Dictionary<string, object> request, string key, int limit) {
            object value; if (!request.TryGetValue(key, out value) || !(value is string) || ((string)value).Length > limit) throw new ArgumentException(key);
            return (string)value;
        }
        static string OptionalText(Dictionary<string,object> request,string key,int limit){object value;return !request.TryGetValue(key,out value)||value==null?null:Text(request,key,limit);}
        static string Trim(string value, int length) { return value == null ? "" : value.Length > length ? value.Substring(0, length) : value; }
        static JobOptions Options(FlowSettings settings, string format, int height) {
            var o = JobOptions.From(settings);
            o.Format = format; o.Profile = format == "mp4" ? "compatible" : format == "mkv" ? "best" : format;
            o.Codec = format == "mp4" ? "h264" : "auto"; o.Height = height; o.Strict = true; o.FpsLimit = 0;
            return o;
        }
        public static List<Choice> BuildChoices(MediaInfo media, FlowSettings settings) {
            var list = new List<Choice>();
            var heights = media.Usable.Where(f => f.HasVideo && f.ShortSide > 0).Select(f => f.ShortSide).Distinct().OrderByDescending(h => h).Take(20).ToArray();
            foreach (var format in new[] { "mp4", "mkv", "mp3", "m4a" }) {
                if ((format == "mp3" || format == "m4a") && media.HasAudio == Tri.No) continue;
                if ((format == "mp4" || format == "mkv") && media.HasVideo == Tri.No) continue;
                var available = format == "mp3" || format == "m4a" || heights.Length == 0 ? new[] { 0 } : heights;
                foreach (var height in available) {
                    var o = Options(settings, format, height);
                    try {
                        var s = FormatSelector.Select(media, o);
                        // A codec preference can select a lower resolution than the requested limit.
                        // Offer the actual selected short side once, never label 720p as 2160p.
                        var actualHeight = o.IsAudio ? 0 : s.ShortSide;
                        var choiceKey = format + ":" + actualHeight;
                        if (list.Any(c => c.key == choiceKey)) continue;
                        var summary = new List<string>();
                        if (s.Width > 0 && s.Height > 0) summary.Add(s.Width + "×" + s.Height);
                        if (s.Fps > 0) summary.Add(s.Fps.ToString("0.##", CultureInfo.InvariantCulture) + " fps");
                        if (s.VCodec != null) summary.Add(s.VCodec);
                        if (s.ACodec != null) summary.Add(s.ACodec);
                        list.Add(new Choice { key = choiceKey, format = format, profile = o.Profile, height = actualHeight, summary = string.Join(" · ", summary), bytes = s.Bytes, sizeExact = s.SizeExact, conversion = s.NeedsConversion || s.ConvertIfNeeded, warning = string.Join(" ", s.Notes.Select(Loc.T)) });
                    } catch (FlowException) { /* Unavailable combinations never become clickable options. */ }
                }
            }
            return list;
        }
    }
}
