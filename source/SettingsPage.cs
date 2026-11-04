using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Flow {
    public partial class Controller {
        string engineVersion;

        void InitSettings() {
            SelectTag(C<ComboBox>("LanguageBox"), settings.Language);
            SelectTag(C<ComboBox>("ThemeBox"), settings.Theme);
            C<ComboBox>("LanguageBox").SelectionChanged += (s, e) => { if (loading) return; var v = TagOf(C<ComboBox>("LanguageBox")); if (v == null || v == settings.Language) return; settings.Language = v; SaveSettings(); Loc.Set(v); };
            C<ComboBox>("ThemeBox").SelectionChanged += (s, e) => { if (loading) return; var v = TagOf(C<ComboBox>("ThemeBox")); if (v == null || v == settings.Theme) return; settings.Theme = v; SaveSettings(); Theme.Set(v); };
            C<TextBox>("DefaultFolderBox").Text = settings.Folder;
            C<Button>("DefaultFolderButton").Click += (s, e) => ChooseFolder(path => { settings.Folder = path; C<TextBox>("DefaultFolderBox").Text = path; C<TextBox>("FolderBox").Text = path; SaveSettings(); }, settings.Folder);
            C<Button>("OpenDefaultFolderButton").Click += (s, e) => OpenFolder(settings.Folder);
            C<CheckBox>("AutoPreviewBox").IsChecked = settings.AutoPreview;
            C<CheckBox>("AutoPreviewBox").Click += (s, e) => { settings.AutoPreview = C<CheckBox>("AutoPreviewBox").IsChecked == true; SaveSettings(); if (video == null) RefreshDownloadTexts(); };
            C<Button>("OpenDataButton").Click += (s, e) => { Directory.CreateDirectory(paths.DataDir); OpenFolder(paths.DataDir); };
            C<Button>("UpdateButton").Click += (s, e) => UpdateEngine();
            SelectTag(C<ComboBox>("ParallelBox"), settings.Parallel.ToString());
            SelectTag(C<ComboBox>("RetriesBox"), settings.Retries.ToString());
            if (C<ComboBox>("RetriesBox").SelectedItem == null) SelectTag(C<ComboBox>("RetriesBox"), "2");
            C<ComboBox>("ParallelBox").SelectionChanged += (s, e) => { if (loading) return; int v; if (int.TryParse(TagOf(C<ComboBox>("ParallelBox")), out v)) { settings.Parallel = v; SaveSettings(); queue.Pump(); } };
            C<ComboBox>("RetriesBox").SelectionChanged += (s, e) => { if (loading) return; int v; if (int.TryParse(TagOf(C<ComboBox>("RetriesBox")), out v)) { settings.Retries = v; SaveSettings(); } };
            C<CheckBox>("ResumeBox").IsChecked = settings.ResumeInterrupted;
            C<CheckBox>("ResumeBox").Click += (s, e) => { settings.ResumeInterrupted = C<CheckBox>("ResumeBox").IsChecked == true; SaveSettings(); };
            RefreshSettingsInfo();
            LoadEngineVersion();
        }

        void RefreshSettingsInfo() {
            var data = C<TextBlock>("DataFolderText");
            if (data == null) return;
            data.Text = Loc.F("set.data.folder", paths.DataDir);
            var when = Iso.Parse(settings.ImportedAt);
            C<TextBlock>("ImportText").Text = settings.ImportedFrom != null ? Loc.F("set.import.done", settings.ImportedCount, when.HasValue ? Loc.Date(when.Value) : "", settings.ImportedFrom) : Loc.T("set.import.none");
            C<TextBlock>("AboutText").Text = Loc.F("set.about.version", Program.Version, engineVersion ?? Loc.T("common.unknown"));
        }

        async void LoadEngineVersion() {
            if (!File.Exists(Engine.Exe)) return;
            try {
                var result = await ProcessRunner.Run(Engine.Exe, new[] { "--version" }, null, CancellationToken.None, 30);
                if (result.ExitCode == 0) { engineVersion = result.Output.Trim(); RefreshSettingsInfo(); }
            } catch (Exception) {}
        }

        async void UpdateEngine() { await UpdateEngineAsync(); }
    }
}
