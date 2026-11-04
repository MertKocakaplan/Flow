using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace Flow {
    // The text face. Sentient (Fontshare, ITF Free Font License: embedding in a desktop app is allowed,
    // redistributing the file is not) is embedded by build.ps1 when source\fonts holds it; the file is kept
    // out of git. WPF opens fonts only from a URI, so the embedded copy is written once to a cache folder.
    // Without it the text face falls back to Segoe UI. XAML reads it as {StaticResource TextFont}.
    public static class Typefaces {
        public const string Fallback = "Segoe UI Variable Text, Segoe UI";
        static FontFamily text;
        public static FontFamily Text { get { if (text == null) text = Load(); return text; } }

        public static void Install(ResourceDictionary target) {
            if (target != null) target["TextFont"] = Text;
        }

        static FontFamily Load() {
            try {
                var assembly = Assembly.GetExecutingAssembly();
                var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.StartsWith("font.", StringComparison.Ordinal) && n.IndexOf("Sentient", StringComparison.OrdinalIgnoreCase) >= 0);
                if (name == null) return new FontFamily(Fallback);
                var dir = Path.Combine(Path.GetTempPath(), "Flow-fonts");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, name.Substring("font.".Length));
                using (var stream = assembly.GetManifestResourceStream(name)) {
                    if (!File.Exists(file) || new FileInfo(file).Length != stream.Length)
                        using (var output = File.Create(file)) stream.CopyTo(output);
                }
                return new FontFamily(new Uri(dir + Path.DirectorySeparatorChar), "./#Sentient, " + Fallback);
            } catch (Exception) {
                return new FontFamily(Fallback);
            }
        }
    }
}
