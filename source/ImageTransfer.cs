using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Flow {
    public sealed class ImageMetadata {
        public string Url, Title, Extension, Referrer, Id;
        public long Bytes;
    }
    // Original raster images use the same durable queue as video, without passing them to yt-dlp.
    public static class ImageTransfer {
        public const long MaxBytes = 100L * 1024 * 1024;
        public static string Referrer(string value) {
            if (string.IsNullOrEmpty(value)) return null;
            Uri uri;
            if (value.Length > 8192 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || !Platforms.TryPublicWebUrl(value, out uri)) throw new FlowException(ErrorCode.UrlNotPublic);
            // Only the page origin is forwarded; query strings can contain private or expiring tokens.
            return uri.GetLeftPart(UriPartial.Authority) + "/";
        }
        public static string Extension(string contentType) {
            switch ((contentType ?? "").Split(';')[0].Trim().ToLowerInvariant()) {
                case "image/jpeg": return "jpg";
                case "image/png": return "png";
                case "image/gif": return "gif";
                case "image/webp": return "webp";
                case "image/avif": return "avif";
                case "image/bmp": case "image/x-ms-bmp": return "bmp";
                default: return null;
            }
        }
        public static string Signature(byte[] bytes, int count) {
            if (count >= 3 && bytes[0]==255 && bytes[1]==216 && bytes[2]==255) return "jpg";
            if (count >= 8 && bytes.Take(8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) return "png";
            var ascii=Encoding.ASCII.GetString(bytes,0,Math.Min(count,64));
            if (ascii.StartsWith("GIF87a") || ascii.StartsWith("GIF89a")) return "gif";
            if (ascii.StartsWith("RIFF") && ascii.Length>=12 && ascii.Substring(8,4)=="WEBP") return "webp";
            if (count>=14 && ascii.StartsWith("BM")) return "bmp";
            if (count>=16 && ascii.Substring(4,4)=="ftyp" && (ascii.Substring(8).Contains("avif") || ascii.Substring(8).Contains("avis"))) return "avif";
            return null;
        }
        public static bool PublicAddress(IPAddress address) {
            if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();
            if(IPAddress.IsLoopback(address))return false;
            var b=address.GetAddressBytes();
            if(address.AddressFamily==AddressFamily.InterNetworkV6)return (b[0]&0xe0)==0x20; // global unicast only
            return b[0]!=0 && b[0]!=10 && b[0]!=127 && b[0]<224 && !(b[0]==169&&b[1]==254) && !(b[0]==172&&b[1]>=16&&b[1]<=31) && !(b[0]==192&&b[1]==168) && !(b[0]==100&&b[1]>=64&&b[1]<=127);
        }
        // Statuses that say what is wrong. Anything else stays a network problem and may be tried again.
        internal static ErrorCode StatusError(HttpStatusCode code) {
            if(code==HttpStatusCode.Forbidden)return ErrorCode.Forbidden;
            if(code==HttpStatusCode.Unauthorized)return ErrorCode.LoginRequired;
            if(code==HttpStatusCode.NotFound||code==HttpStatusCode.Gone)return ErrorCode.Unavailable;
            if((int)code==429)return ErrorCode.RateLimited;
            return ErrorCode.None;
        }
        internal static IPEndPoint GuardConnection(ServicePoint point,IPEndPoint remote,int retry) {
            if(!PublicAddress(remote.Address))throw new FlowException(ErrorCode.UrlNotPublic);
            return null;   // the system chooses the local end
        }
        static async Task<HttpWebResponse> Open(string value, string referer, bool headersOnly, CancellationToken token) {
            for(int redirects=0;redirects<6;redirects++) {
                token.ThrowIfCancellationRequested();
                Uri uri;if(!Platforms.TryPublicWebUrl(value,out uri))throw new FlowException(ErrorCode.UrlNotPublic);
                var addresses=await Dns.GetHostAddressesAsync(uri.DnsSafeHost);
                if(addresses.Length==0 || addresses.Any(a=>!PublicAddress(a)))throw new FlowException(ErrorCode.UrlNotPublic);
                var request=(HttpWebRequest)WebRequest.Create(uri);request.AllowAutoRedirect=false;request.Method=headersOnly?"HEAD":"GET";
                request.UserAgent="Flow/2.2";request.Timeout=30000;request.ReadWriteTimeout=30000;request.Credentials=null;request.UseDefaultCredentials=false;
                request.Referer=Referrer(referer);
                // The check above and the connection resolve the name separately, so the address really dialled is
                // checked too. Through a proxy that address is the proxy's, which may rightly be private.
                if(request.Proxy==null||request.Proxy.IsBypassed(uri))request.ServicePoint.BindIPEndPointDelegate=GuardConnection;
                HttpWebResponse response=null;bool retryGet=false;
                using(token.Register(request.Abort)) {
                    try { response=(HttpWebResponse)await request.GetResponseAsync(); }
                    catch(WebException ex) { token.ThrowIfCancellationRequested();var http=ex.Response as HttpWebResponse;if(http!=null){var code=http.StatusCode;http.Dispose();var failure=StatusError(code);if(failure!=ErrorCode.None)throw new FlowException(failure);retryGet=headersOnly&&(code==HttpStatusCode.MethodNotAllowed||code==HttpStatusCode.NotImplemented);}if(!retryGet)throw new FlowException(ex.Status==WebExceptionStatus.Timeout?ErrorCode.Timeout:ErrorCode.Network); }
                }
                if(retryGet)return await Open(value,referer,false,token);
                var status=(int)response.StatusCode;
                if(status>=300&&status<400){var location=response.Headers["Location"];response.Dispose();Uri next;if(string.IsNullOrEmpty(location)||!Uri.TryCreate(uri,location,out next))throw new FlowException(ErrorCode.Unsupported);value=next.AbsoluteUri;continue;}
                if(response.ContentLength>MaxBytes){response.Dispose();throw new FlowException(ErrorCode.ImageTooLarge);}
                return response;
            }
            throw new FlowException(ErrorCode.Network);
        }
        public static async Task<ImageMetadata> Inspect(string url,string title,string referer,CancellationToken token) {
            using(var response=await Open(url,referer,true,token)) {
                var ext=Extension(response.ContentType);if(ext==null)throw new FlowException(ErrorCode.ImageUnsupported);
                string id;using(var sha=SHA256.Create())id=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-","").Substring(0,12).ToLowerInvariant();
                return new ImageMetadata { Url=url,Title=string.IsNullOrWhiteSpace(title)?Path.GetFileName(new Uri(url).AbsolutePath):title,Extension=ext,Referrer=Referrer(referer),Bytes=response.ContentLength,Id=id };
            }
        }
        public static async Task Run(JobItem item,JobContext context,CancellationToken token) {
            var job=item.Job;
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)) {
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                try { await Transfer(item,context,timeout.Token); }
                catch(OperationCanceledException){if(token.IsCancellationRequested)throw;throw new FlowException(ErrorCode.Timeout);}
            }
        }
        static async Task Transfer(JobItem item,JobContext context,CancellationToken token) {
            var job=item.Job;
            context.Post(()=>item.SetStage(JobState.Inspecting,"stage.inspect"));
            using(var response=await Open(job.Url,job.Referrer,false,token))
            using(token.Register(response.Close)) {
                var ext=Extension(response.ContentType);if(ext==null)throw new FlowException(ErrorCode.ImageUnsupported);
                var folder=OutputPlanner.Folder(job.Options,job);EngineExecutor.Prepare(folder);
                var available=DiskSpace.Free(folder);var required=(response.ContentLength>0?response.ContentLength:MaxBytes)+1024*1024;
                if(available>=0&&available<required)throw new FlowException(ErrorCode.DiskSpaceLow,null,new[]{Loc.Size(required),Loc.Size(available)});
                job.TempDir=EngineExecutor.PartsFolder(folder,job.Id);var part=Path.Combine(job.TempDir,"image.part");
                var selection=new Selection { Spec="image-original",Bytes=response.ContentLength,SizeExact=response.ContentLength>0,Silent=true,Container=ext };
                job.Options.Format=ext;
                var name=OutputPlanner.Fit(folder,OutputPlanner.Name(job.Options,job,null,DateTime.Now),ext);
                var target=Path.Combine(folder,name+"."+ext);
                if(File.Exists(target)&&job.Options.Duplicate=="skip"){context.Post(()=>item.MarkSkipped(target,selection));EngineExecutor.TryDelete(job.TempDir,job.Id);return;}
                target=Naming.Unique(folder,name,ext,p=>File.Exists(p)||context.ReservedByOther(p,job.Id));
                context.Post(()=>{item.ApplySelection(selection,target,null);item.SetStage(JobState.Downloading,"stage.download");});
                long downloaded=0;var watch=Stopwatch.StartNew();var buffer=new byte[65536];var signature=new byte[64];int captured=0;
                try {
                    using(var input=response.GetResponseStream())
                    using(var output=new FileStream(part,FileMode.Create,FileAccess.Write,FileShare.None,65536,true)) {
                        while(true){token.ThrowIfCancellationRequested();var read=await input.ReadAsync(buffer,0,buffer.Length,token);if(read==0)break;
                            if(captured<64){var take=Math.Min(64-captured,read);Array.Copy(buffer,0,signature,captured,take);captured+=take;}
                            downloaded+=read;if(downloaded>MaxBytes)throw new FlowException(ErrorCode.ImageTooLarge);
                            await output.WriteAsync(buffer,0,read,token);
                            if(job.Options.SpeedLimit>0){var delay=downloaded*1000.0/job.Options.SpeedLimit-watch.Elapsed.TotalMilliseconds;if(delay>0)await Task.Delay((int)Math.Min(delay,60000),token);}
                            var snap=new ProgressSnapshot { Streams=1,Stream=1,IsVideo=true,Downloaded=downloaded,Total=response.ContentLength,Percent=response.ContentLength>0?100.0*downloaded/response.ContentLength:-1,Speed=downloaded/Math.Max(.1,watch.Elapsed.TotalSeconds),Eta=-1 };
                            context.Post(()=>item.ApplyProgress(snap));
                        }
                    }
                } catch(Exception){token.ThrowIfCancellationRequested();throw;}
                if(downloaded<=0 || response.ContentLength>=0 && downloaded!=response.ContentLength || Signature(signature,captured)!=ext)throw new FlowException(ErrorCode.VerifyFailed,null,new[]{"verify.unreadable"});
                context.Post(()=>item.SetStage(JobState.Verifying,"stage.verify"));
                var probe=await EngineExecutor.Probe(part,token);
                if(!probe.HasVideo || probe.HasAudio || probe.Width<=0 || probe.Height<=0)throw new FlowException(ErrorCode.ImageUnsupported);
                var decoded=await ProcessRunner.Run(Engine.FfmpegExe,new[]{"-v","error","-xerror","-i",part,"-frames:v","1","-f","null","-"},null,token,120);
                if(decoded.ExitCode!=0)throw new FlowException(ErrorCode.VerifyFailed,null,new[]{"verify.unreadable"});
                token.ThrowIfCancellationRequested();
                // The name was chosen before the transfer; a file may have appeared there since.
                var final=File.Exists(target)?Naming.Unique(folder,name,ext,p=>File.Exists(p)||context.ReservedByOther(p,job.Id)):target;
                File.Move(part,final);EngineExecutor.TryDelete(job.TempDir,job.Id);
                context.Post(()=>item.MarkCompleted(final,"image-original",probe));
            }
        }
    }
}
