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

        static readonly string[] Windows = { "MainWindow.xaml", "CloseDialog.xaml" };

        public static void Run() {
            XamlNames();
            XamlColorLiterals();
            FocusStates();
            MotionDurations();
        }

        // Storyboards read their durations from resources; with Windows animations off they are zero.
        // The UI tests run with motion off so screenshots show settled states.
        static void MotionDurations() {
            var d = new System.Windows.ResourceDictionary();
            Motion.Enabled = true; Motion.Install(d);
            T.Check(((System.Windows.Duration)d["MotionFast"]).TimeSpan.TotalMilliseconds == 120 && ((System.Windows.Duration)d["MotionMedium"]).TimeSpan.TotalMilliseconds == 200, "hareket süreleri 120/200 ms");
            Motion.Enabled = false; Motion.Install(d);
            T.Check(((System.Windows.Duration)d["MotionFast"]).TimeSpan == TimeSpan.Zero && ((System.Windows.Duration)d["MotionMedium"]).TimeSpan == TimeSpan.Zero, "animasyonlar kapalıyken hareket süresi sıfır");
        }

        static string Detail(List<string> items) { return items.Count == 0 ? "" : ": " + string.Join(", ", items.Take(12)); }

        // High-contrast mode maps every Theme key to system colours; a literal colour in XAML would escape it.
        static void XamlColorLiterals() {
            var named = new HashSet<string>(typeof(System.Windows.Media.Colors).GetProperties().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            named.Remove("Transparent");
            var colourAttributes = new[] { "Brush", "Background", "Foreground", "Fill", "Stroke", "Color" };
            var literals = new List<string>();
            foreach (var file in Windows)
                foreach (XmlElement element in Load(file).GetElementsByTagName("*")) {
                    foreach (XmlAttribute attribute in element.Attributes) {
                        if (!colourAttributes.Any(a => attribute.LocalName.EndsWith(a))) continue;
                        var value = attribute.Value.Trim();
                        if (value.StartsWith("#") || named.Contains(value)) literals.Add(file + ":" + element.LocalName + "." + attribute.LocalName + "=" + value);
                    }
                    if ((element.LocalName == "Color" || element.LocalName == "SolidColorBrush") && element.InnerText.Trim().Length > 0) literals.Add(file + ":" + element.LocalName + "=" + element.InnerText.Trim());
                }
            T.Check(literals.Count == 0, "XAML'de sabit renk yok" + Detail(literals));
        }

        // Every interactive template shows keyboard focus. A template owned by a control that never takes
        // focus is exempt; combo box items show keyboard position through IsHighlighted.
        static void FocusStates() {
            var interactive = new HashSet<string> { "Button", "ToggleButton", "RadioButton", "CheckBox", "TextBox", "ComboBox", "ComboBoxItem", "ListBoxItem" };
            var missing = new List<string>();
            foreach (var file in Windows)
                foreach (XmlElement template in Load(file).GetElementsByTagName("ControlTemplate")) {
                    var target = template.GetAttribute("TargetType");
                    if (!interactive.Contains(target)) continue;
                    var owner = template.ParentNode == null ? null : template.ParentNode.ParentNode as XmlElement;
                    if (owner != null && owner.GetAttribute("Focusable") == "False") continue;
                    var properties = template.GetElementsByTagName("Trigger").Cast<XmlElement>().Select(t => t.GetAttribute("Property")).ToList();
                    bool shows = properties.Contains("IsKeyboardFocused") || properties.Contains("IsKeyboardFocusWithin") || (target == "ComboBoxItem" && properties.Contains("IsHighlighted"));
                    if (!shows) missing.Add(file + ":" + target + Owner(template));
                }
            T.Check(missing.Count == 0, "her etkileşimli şablonda klavye odağı görünür" + Detail(missing));
        }
        static string Owner(XmlElement template) {
            for (XmlNode node = template.ParentNode; node != null; node = node.ParentNode) {
                var element = node as XmlElement;
                if (element != null && element.LocalName == "Style" && element.GetAttribute("Key", XamlNs).Length > 0) return " (" + element.GetAttribute("Key", XamlNs) + ")";
            }
            return "";
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
