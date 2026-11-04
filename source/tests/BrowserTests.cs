using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flow;
using Flow.BrowserIntegration;

namespace FlowTests {
    public static class BrowserTests {
        public static void Wire() {
            using (var stream = new MemoryStream()) {
                BrowserProtocol.Write(stream, "{\"title\":\"İstanbul 🎬\"}"); stream.Position = 0;
                T.Check(BrowserProtocol.Read(stream, BrowserProtocol.MaxRequest) == "{\"title\":\"İstanbul 🎬\"}", "browser: binary UTF-8 framing preserves Unicode");
                T.Check(BrowserProtocol.Read(stream, BrowserProtocol.MaxRequest) == null, "browser: clean EOF ends the native host");
            }
            foreach (var bytes in new[] { new byte[] { 0,0,1,0 }, new byte[] { 255,255,255,255 }, new byte[] { 20,0,0,0,1,2 } }) {
                bool rejected = false;
                try { BrowserProtocol.Read(new MemoryStream(bytes), 64); } catch (Exception ex) { rejected = ex is IOException || ex is InvalidDataException; }
                T.Check(rejected, "browser: invalid or truncated message rejected without allocation or hang");
            }
            T.Check(BrowserProtocol.AllowedOrigin("chrome-extension://" + BrowserProtocol.ExtensionId + "/") && !BrowserProtocol.AllowedOrigin("https://example.com/") && !BrowserProtocol.AllowedOrigin("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/"), "browser: only the packaged extension origin is accepted");
            var path = T.Scratch("browser-pipe");
            T.Check(BrowserProtocol.PipeName(path) != BrowserProtocol.PipeName(path + "-other"), "browser: isolated data folders use distinct per-user pipes");
            using (var server = new BrowserPipeServer(path, (json, token) => Task.FromResult(BrowserApi.Success(new { received = json })))) {
                server.Start();
                using (var client = new NamedPipeClientStream(".", BrowserProtocol.PipeName(path), PipeDirection.InOut)) {
                    client.Connect(5000); BrowserProtocol.Write(client, "{\"v\":1,\"action\":\"ping\"}");
                    var result = BrowserProtocol.Read(client, BrowserProtocol.MaxResponse);
                    T.Check(result.Contains("received") && result.Contains("ping"), "browser: actual named-pipe request/response round trip");
                }
            }
        }
        static Dictionary<string, object> Reply(BrowserApi api, object request) {
            var task = api.Handle(BrowserProtocol.Json().Serialize(request), CancellationToken.None);
            UiTests.Wait(() => task.IsCompleted, 10000);
            return (Dictionary<string, object>)BrowserProtocol.Json().DeserializeObject(task.GetAwaiter().GetResult());
        }
        static Dictionary<string, object> Data(Dictionary<string, object> reply) { return (Dictionary<string, object>)reply["data"]; }
        static string Error(Dictionary<string, object> reply) { return (string)((Dictionary<string, object>)reply["error"])["code"]; }
        public static void Api() {
            var dir = T.Scratch("browser-api");
            var paths = new AppPaths { DataDir = Path.Combine(dir,"data"), LegacyDir = Path.Combine(dir,"legacy"), DefaultDownloads = Path.Combine(dir,"downloads") };
            var settings = SettingsRules.Defaults(paths);
            var queue = new QueueService(paths, () => settings, new FakeExecutor());
            int calls = 0, shows = 0;
            var media = MediaParser.Parse(T.Fixture("youtube.json"), Platform.YouTube);
            var api = new BrowserApi(queue, () => settings, (url, token) => { calls++; return Task.FromResult(media); }, () => shows++);
            T.Check((bool)Reply(api, new { v=1, action="ping" })["ok"], "browser: versioned handshake succeeds");
            T.Check(Error(Reply(api, new { v=2, action="ping" })) == "PROTOCOL_VERSION", "browser: incompatible protocol explained");
            T.Check(Error(Reply(api, new { v=1, action="exec", command="calc" })) == "INVALID_ACTION", "browser: arbitrary native commands are refused");
            T.Check(Error(Reply(api, new { v=1, action="inspect", url="https://127.0.0.1/video" })) == "UrlNotPublic" && calls == 0, "browser: unsafe URLs rejected before reaching the engine");
            var result = Data(Reply(api, new { v=1, action="inspect", url="https://www.youtube.com/watch?v=jNQXAC9IVRw" }));
            var tokenId = (string)result["token"];
            var choices = BrowserApi.BuildChoices(media, settings);
            T.Check(choices.Any(c=>c.format=="mp3") && choices.Any(c=>c.height==240) && !choices.Any(c=>c.height==1080), "browser: offers real media qualities and supported audio choices");
            var mixed = new MediaInfo { Formats = new List<FormatInfo> {
                new FormatInfo { Id="avc", Ext="mp4", VCodec="h264", ACodec="aac", Width=1280, Height=720 },
                new FormatInfo { Id="av1", Ext="webm", VCodec="av01", ACodec="opus", Width=3840, Height=2160 }
            } };
            var mixedChoices = BrowserApi.BuildChoices(mixed, settings);
            T.Check(mixedChoices.Count(c=>c.format=="mp4")==1 && mixedChoices.Single(c=>c.format=="mp4").height==720 && mixedChoices.Any(c=>c.format=="mkv" && c.height==2160), "browser: codec preference cannot mislabel a lower-resolution stream or duplicate choices");
            var silent = MediaParser.Parse(T.Fixture("instagram.json"), Platform.Instagram);
            T.Check(!BrowserApi.BuildChoices(silent,settings).Any(c=>c.format=="mp3" || c.format=="m4a"), "browser: silent video never offers audio extraction");
            string requestId = Guid.NewGuid().ToString("D");
            var request = new { v=1, action="enqueue", token=tokenId, choice=choices.First().key, requestId=requestId, hold=true, folder="C:\\untrusted", command="ignored" };
            var first = Data(Reply(api,request)); var second = Data(Reply(api,request));
            T.Check(queue.Items.Count==1 && (string)first["jobId"]==(string)second["jobId"] && (bool)second["duplicate"], "browser: repeated submission does not create a duplicate job");
            T.Check(queue.Items[0].Job.Options.Folder==settings.Folder && queue.Items[0].Job.State==JobState.Paused, "browser: uses desktop folder settings and preserves Add for later");
            var saved = JsonStore.Convert<QueueFile>(JsonStore.Load(paths.QueueFile).Data);
            T.Check(saved.Jobs.Count==1 && saved.Jobs[0].BrowserRequestId==requestId, "browser: submission ID and held job are durably saved");
            var reopenedQueue = new QueueService(paths,()=>settings,new FakeExecutor()); reopenedQueue.Load();
            var reopened = new BrowserApi(reopenedQueue,()=>settings,(u,t)=>Task.FromResult(media),()=>{});
            T.Check((bool)Data(Reply(reopened,request))["duplicate"] && reopenedQueue.Items.Count==1, "browser: retry after desktop restart still returns the original job");
            reopenedQueue.Shutdown();
            T.Check(Error(Reply(api,new {v=1,action="enqueue",token="missing",choice="mp4:240",requestId=Guid.NewGuid().ToString("D")}))=="PREVIEW_EXPIRED", "browser: missing preview cannot start a download");
            T.Check(Error(Reply(api,new {v=1,action="enqueue",token=tokenId,choice="mp4:9999",requestId=Guid.NewGuid().ToString("D")}))=="INVALID_CHOICE", "browser: fabricated quality rejected");
            var id=queue.Items[0].Job.Id;
            T.Check((bool)Reply(api,new {v=1,action="cancel",jobId=id})["ok"] && queue.Items[0].Job.State==JobState.Canceled, "browser: queue cancellation uses the existing queue behavior");
            T.Check((int)Data(Reply(api,new {v=1,action="queue"}))["total"]==1, "browser: queue snapshot includes desktop jobs");
            Reply(api,new {v=1,action="show"}); T.Check(shows==1,"browser: open-desktop command is routed explicitly");
            queue.Items[0].Job.State=JobState.Completed; queue.SaveRequired();
            queue.Shutdown();
            var finishedQueue = new QueueService(paths,()=>settings,new FakeExecutor()); finishedQueue.Load(); finishedQueue.SaveRequired(); finishedQueue.Shutdown();
            var finishedAgain = new QueueService(paths,()=>settings,new FakeExecutor()); finishedAgain.Load();
            var finishedApi = new BrowserApi(finishedAgain,()=>settings,(u,t)=>Task.FromResult(media),()=>{});
            T.Check(finishedAgain.Items.Count==0 && (bool)Data(Reply(finishedApi,request))["duplicate"], "browser: completed submissions remain idempotent after repeated restarts");
            finishedAgain.Shutdown();
            var blockedDir=Path.Combine(dir,"blocked"); File.WriteAllText(blockedDir,"Not a directory");
            var blockedPaths=new AppPaths { DataDir=blockedDir, LegacyDir=paths.LegacyDir, DefaultDownloads=paths.DefaultDownloads };
            var blockedQueue=new QueueService(blockedPaths,()=>settings,new FakeExecutor());
            var blockedApi=new BrowserApi(blockedQueue,()=>settings,(u,t)=>Task.FromResult(media),()=>{});
            var blockedPreview=Data(Reply(blockedApi,new {v=1,action="inspect",url="https://www.youtube.com/watch?v=jNQXAC9IVRw"}));
            var failedSave=Reply(blockedApi,new {v=1,action="enqueue",token=(string)blockedPreview["token"],choice=choices.First().key,requestId=Guid.NewGuid().ToString("D"),hold=false});
            T.Check(Error(failedSave)=="QUEUE_SAVE_FAILED" && blockedQueue.Items.Count==0 && blockedQueue.ActiveCount==0, "browser: unwritable storage never acknowledges or starts a download");
            blockedQueue.Shutdown();
            ImageSupport();
        }
        static void ImageSupport() {
            T.Check(ImageTransfer.Extension("image/jpeg; charset=binary")=="jpg" && ImageTransfer.Extension("text/html")==null && ImageTransfer.Extension("image/svg+xml")==null,"browser images: only supported raster content types accepted");
            T.Check(ImageTransfer.Signature(new byte[]{137,80,78,71,13,10,26,10},8)=="png" && ImageTransfer.Signature(Encoding.UTF8.GetBytes("<html>not an image"),18)==null,"browser images: file signature distinguishes image bytes from error pages");
            T.Check(ImageTransfer.Referrer("https://example.com/private/path?token=secret")=="https://example.com/","browser media: only source origin is forwarded, without private path or tokens");
            T.Rejects(()=>ImageTransfer.Referrer("https://localhost/a"),ErrorCode.UrlNotPublic,"browser media: local referrer rejected");
            T.Check(!ImageTransfer.PublicAddress(System.Net.IPAddress.Parse("192.168.1.2")) && !ImageTransfer.PublicAddress(System.Net.IPAddress.Parse("::ffff:127.0.0.1")) && !ImageTransfer.PublicAddress(System.Net.IPAddress.Parse("fe80::1")) && ImageTransfer.PublicAddress(System.Net.IPAddress.Parse("8.8.8.8")),"browser images: private, mapped loopback and link-local DNS targets rejected");
            var args=Engine.InspectArgs("https://cdn.example.com/video.mp4",0,"https://example.com/watch?token=secret");
            T.Check(args.Contains("--referer") && args[args.IndexOf("--referer")+1]=="https://example.com/" && !args.Any(a=>a.Contains("secret")),"browser media: referrer stays a validated argument, not an arbitrary header");
            var dir=T.Scratch("browser-images");var paths=new AppPaths {DataDir=Path.Combine(dir,"data"),DefaultDownloads=Path.Combine(dir,"downloads"),LegacyDir=Path.Combine(dir,"legacy")};
            var settings=SettingsRules.Defaults(paths);var queue=new QueueService(paths,()=>settings,new FakeExecutor());
            var api=new BrowserApi(queue,()=>settings,(u,t)=>{throw new Exception("Images must not use yt-dlp inspection");},()=>{});
            api.InspectImage=(url,title,referer,t)=>Task.FromResult(new ImageMetadata {Url=url,Title=title,Extension="png",Bytes=321,Id="img",Referrer=referer});
            var preview=Data(Reply(api,new {v=1,action="inspect",kind="image",url="https://cdn.example.com/photo.png",title="My image",referrer="https://example.com/gallery?secret=1"}));
            var added=Reply(api,new {v=1,action="enqueue",token=(string)preview["token"],choice="original:0",hold=true,requestId=Guid.NewGuid().ToString("D")});
            T.Check((bool)added["ok"] && queue.Items.Count==1 && queue.Items[0].Job.MediaKind=="image" && queue.Items[0].Job.Options.Format=="png" && queue.Items[0].Job.Referrer=="https://example.com/","browser images: original format and safe referrer enter the same durable queue");
            queue.Shutdown();var reopened=new QueueService(paths,()=>settings,new FakeExecutor());reopened.Load();
            T.Check(reopened.Items.Single().Job.MediaKind=="image" && reopened.Items.Single().Job.Options.Format=="png","browser images: original-image queue survives desktop restart");reopened.Shutdown();
        }
    }
}
