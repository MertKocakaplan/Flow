using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Flow {
    // File name template, subfolders and list folders (persisted settings, copied into each job).
    public partial class Controller {
        void InitFileOptions() {
            SelectTag(C<ComboBox>("OrganizeBox"), settings.Organize);
            C<TextBox>("TemplateBox").Text = settings.NameTemplate;
            SyncTemplatePreset();
            C<CheckBox>("CollectionFolderBox").IsChecked = settings.CollectionFolder;
            C<ComboBox>("OrganizeBox").SelectionChanged += (s, e) => { if (loading) return; settings.Organize = TagOf(C<ComboBox>("OrganizeBox")) ?? "none"; SaveSettings(); UpdateSelectionSummary(); };
            C<ComboBox>("TemplatePresetBox").SelectionChanged += (s, e) => {
                if (loading) return;
                var tag = TagOf(C<ComboBox>("TemplatePresetBox"));
                if (tag == null || tag == "custom") { C<TextBox>("TemplateBox").Focus(); return; }
                C<TextBox>("TemplateBox").Text = tag;
            };
            C<TextBox>("TemplateBox").TextChanged += (s, e) => OnTemplateChanged();
            C<CheckBox>("CollectionFolderBox").Click += (s, e) => { settings.CollectionFolder = C<CheckBox>("CollectionFolderBox").IsChecked == true; SaveSettings(); };
            ExtraCombos.AddRange(new[] { "OrganizeBox", "TemplatePresetBox", "HistoryPlatformBox", "HistoryStatusBox", "HistoryDateBox" });
        }
        void SyncTemplatePreset() {
            bool was = loading; loading = true;
            var box = C<ComboBox>("TemplatePresetBox");
            var match = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Convert.ToString(i.Tag) == C<TextBox>("TemplateBox").Text);
            box.SelectedItem = match ?? box.Items.OfType<ComboBoxItem>().Last();
            loading = was;
        }
        void OnTemplateChanged() {
            var text = C<TextBox>("TemplateBox").Text;
            bool valid = Naming.IsValidTemplate(text);
            var hint = C<TextBlock>("TemplateHint");
            if (valid) { hint.SetResourceReference(TextBlock.TextProperty, "s.opt.template.tip"); hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted"); }
            else { hint.Text = Loc.T("opt.template.invalid"); hint.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); }
            SyncTemplatePreset();
            if (loading || !valid) return;
            settings.NameTemplate = text; SaveSettings(); UpdateSelectionSummary();
        }
    }
}
