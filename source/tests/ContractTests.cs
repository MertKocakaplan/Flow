using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Flow;

namespace FlowTests {
    // Design-independent guards for the UI rewrite. The code and the tests look elements up by
    // x:Name, so every window-scope name keeps its element type; parts inside templates may change.
    public static class ContractTests {
        const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

        public static void Run() {
            XamlNames();
        }

        static XmlDocument Load(string resource) {
            using (var stream = typeof(Controller).Assembly.GetManifestResourceStream(resource)) {
                var doc = new XmlDocument();
                doc.Load(stream);
                return doc;
            }
        }

        static bool InTemplate(XmlNode node) {
            for (var parent = node.ParentNode; parent != null; parent = parent.ParentNode)
                if (parent.LocalName.Contains("Template")) return true;
            return false;
        }

        static Dictionary<string, string> WindowNames(XmlDocument doc) {
            var result = new Dictionary<string, string>();
            foreach (XmlElement element in doc.GetElementsByTagName("*")) {
                var name = element.GetAttribute("Name", XamlNs);
                if (name.Length > 0 && !InTemplate(element)) result[name] = element.LocalName;
            }
            return result;
        }

        static void XamlNames() {
            var documents = new Dictionary<string, Dictionary<string, string>>();
            var broken = new List<string>();
            foreach (var row in T.Fixture("xaml-names.txt").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)) {
                var parts = row.Split('|');
                Dictionary<string, string> names;
                if (!documents.TryGetValue(parts[0], out names)) { names = WindowNames(Load(parts[0])); documents[parts[0]] = names; }
                string type;
                if (!names.TryGetValue(parts[1], out type)) broken.Add(parts[0] + ":" + parts[1] + " yok");
                else if (type != parts[2]) broken.Add(parts[0] + ":" + parts[1] + " " + type + " (beklenen " + parts[2] + ")");
            }
            T.Check(broken.Count == 0, "XAML ad sözleşmesi korunuyor" + (broken.Count == 0 ? "" : ": " + string.Join(", ", broken)));
        }
    }
}
