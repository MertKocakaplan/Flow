using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Flow.BrowserIntegration {
    // Shared by the WPF receiver and the small Chrome stdio host. No shell commands or media bytes.
    public static class BrowserProtocol {
        public const int Version = 1;
        public const string HostName = "com.flow.downloader";
        public const string ExtensionId = "eidibhbenooohggnhdeblgfdclahhdnd";
        public const string ApplicationFile = "Flow-2.2.exe";
        public const int MaxRequest = 65536, MaxResponse = 524288;
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = MaxResponse, RecursionLimit = 24 }; }
        public static bool AllowedOrigin(string origin) { return origin == "chrome-extension://" + ExtensionId + "/" || origin == "chrome-extension://" + ExtensionId; }
        public static string DataDirectory() {
            return Environment.GetEnvironmentVariable("FLOW_DATA_DIR") ?? Environment.GetEnvironmentVariable("AKIS_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flow");
        }
        public static string PipeName(string directory) {
            var identity = WindowsIdentity.GetCurrent().User.Value + "|" + Path.GetFullPath(directory).TrimEnd('\\').ToUpperInvariant();
            using (var sha = SHA256.Create()) return "Flow.Browser.v1." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").Substring(0, 32);
        }
        public static string Read(Stream input, int limit) {
            var header = new byte[4]; int first = input.ReadByte(); if (first < 0) return null;
            header[0] = (byte)first; Exact(input, header, 1, 3);
            int size = BitConverter.ToInt32(header, 0);
            if (size < 2 || size > limit) throw new InvalidDataException("Invalid frame length");
            var bytes = new byte[size]; Exact(input, bytes, 0, size);
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        static void Exact(Stream stream, byte[] bytes, int offset, int count) {
            while (count > 0) { int n = stream.Read(bytes, offset, count); if (n == 0) throw new EndOfStreamException(); offset += n; count -= n; }
        }
        public static async Task<string> ReadAsync(Stream input, int limit, CancellationToken token) {
            var header = new byte[4]; await ExactAsync(input, header, token).ConfigureAwait(false);
            int size = BitConverter.ToInt32(header, 0);
            if (size < 2 || size > limit) throw new InvalidDataException("Invalid frame length");
            var bytes = new byte[size]; await ExactAsync(input, bytes, token).ConfigureAwait(false);
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        static async Task ExactAsync(Stream stream, byte[] bytes, CancellationToken token) {
            int offset = 0;
            while (offset < bytes.Length) { int n = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false); if (n == 0) throw new EndOfStreamException(); offset += n; }
        }
        public static void Write(Stream output, string json) {
            var bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > MaxResponse) throw new InvalidDataException("Response too large");
            output.Write(BitConverter.GetBytes(bytes.Length), 0, 4); output.Write(bytes, 0, bytes.Length); output.Flush();
        }
        public static string Error(string code) { return Json().Serialize(new { v = Version, ok = false, error = new { code = code } }); }
    }
}
