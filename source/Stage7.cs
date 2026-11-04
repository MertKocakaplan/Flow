using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Flow {
    // Components (versions, integrity, engine update with rollback), supported sites and diagnostics.
    public partial class Controller {
        internal string ToolsDir = Engine.ToolsDir;
        List<ExtractorRow> extractors;
        bool componentsBusy;
        public List<ComponentStatus> ComponentRows { get { return C<ItemsControl>("ComponentList").ItemsSource as List<ComponentStatus> ?? new List<ComponentStatus>(); } }

        void InitStage7() {
            C<ItemsControl>("KnownSites").ItemsSource = Sites.Known;
            C<Expander>("SupportExpander").Expanded += (s, e) => LoadExtractors();
            C<TextBox>("SiteSearch").TextChanged += (s, e) => FilterExtractors();
            C<Button>("CheckComponentsButton").Click += (s, e) => CheckComponents();
            C<Button>("RollbackButton").Click += (s, e) => RollbackEngine();
            C<Button>("CopyDiagnosticsButton").Click += (s, e) => { try { Clipboard.SetText(Diagnostics()); ShowMessage(Loc.T("set.diagnostics.done"), false); } catch (Exception) { ShowMessage(Loc.T("msg.clipboard.error"), true); } };
            C<RadioButton>("NavSettings").Checked += (s, e) => { if (ComponentRows.Count == 0) CheckComponents(); };
            UpdateSupportPanel();
        }
        partial void OnTabShown() { UpdateSupportPanel(); }
        void UpdateSupportPanel() {
            var panel = C<Expander>("SupportExpander");
            if (panel != null) panel.Visibility = currentTab == Platform.Other ? Visibility.Visible : Visibility.Collapsed;
        }

        async void LoadExtractors() {
            if (extractors != null) { FilterExtractors(); return; }
            C<TextBlock>("SiteCount").Text = Loc.T("sites.loading");
            try {
                extractors = await Sites.Extractors(paths.CacheDir, engineVersion, CancellationToken.None);
                FilterExtractors();
            } catch (Exception ex) { C<TextBlock>("SiteCount").Text = Loc.Error(ex); }
        }
        void FilterExtractors() {
            if (extractors == null) return;
            var matches = Sites.Search(extractors, C<TextBox>("SiteSearch").Text).ToList();
            C<ListBox>("SiteList").ItemsSource = matches.Take(500).ToList();
            C<TextBlock>("SiteCount").Text = matches.Count == extractors.Count ? Loc.F("sites.count", Loc.Count(extractors.Count), Loc.Count(extractors.Count(x => x.Broken))) : Loc.F("sites.count.filtered", Loc.Count(matches.Count), Loc.Count(extractors.Count));
        }

        void SetComponentsBusy(bool busy, string text) {
            componentsBusy = busy;
            C<ProgressBar>("ComponentProgress").Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            foreach (var name in new[] { "CheckComponentsButton", "UpdateButton", "RollbackButton" }) C<Button>(name).IsEnabled = !busy;
            if (text != null) C<TextBlock>("ComponentStatusText").Text = text;
        }

        internal async Task CheckComponentsAsync() {
            if (componentsBusy) return;
            SetComponentsBusy(true, Loc.T("comp.checking"));
            try {
                var rows = await Components.Check(ToolsDir, CancellationToken.None);
                C<ItemsControl>("ComponentList").ItemsSource = rows;
                var engine = rows.FirstOrDefault(r => r.Entry.File == "yt-dlp.exe");
                if (engine != null && engine.Version != null) { engineVersion = engine.Version; RefreshSettingsInfo(); }
                SetComponentsBusy(false, "");
            } catch (Exception ex) { SetComponentsBusy(false, Loc.Error(ex)); }
            C<Button>("RollbackButton").Visibility = Components.HasBackup(ToolsDir) ? Visibility.Visible : Visibility.Collapsed;
        }
        async void CheckComponents() { await CheckComponentsAsync(); }

        // Replaces the stage-2 update: blocked while downloads run, with backup, self-check and rollback.
        internal async Task<UpdateResult> UpdateEngineAsync() {
            if (componentsBusy) return null;
            if (queue.ActiveCount > 0) { C<TextBlock>("ComponentStatusText").Text = Loc.Error(ErrorCode.UpdateBusy); ShowMessage(Loc.Error(ErrorCode.UpdateBusy), true); return null; }
            SetComponentsBusy(true, Loc.T("msg.engine.updating"));
            UpdateResult result = null;
            try {
                result = await Components.UpdateEngine(ToolsDir, () => queue.ActiveCount, ReferenceCheck, Log, CancellationToken.None);
                string text = result.RolledBack ? Loc.F("comp.rolledBack", result.Reason, result.NewVersion) : result.Changed ? Loc.F("comp.updatedTo", result.OldVersion, result.NewVersion) : Loc.F("comp.upToDate", result.NewVersion);
                SetComponentsBusy(false, text);
                ShowMessage(text, result.RolledBack);
                extractors = null;
            } catch (Exception ex) { Log(ex.ToString()); SetComponentsBusy(false, Loc.Error(ex)); ShowMessage(Loc.Error(ex), true); }
            await CheckComponentsAsync();
            return result;
        }
        // A short, known-good metadata read used before and after an update.
        internal Func<string, CancellationToken, Task<bool>> ReferenceCheck = async (exe, token) => {
            try {
                var r = await ProcessRunner.Run(exe, Engine.InspectArgs("https://www.youtube.com/watch?v=jNQXAC9IVRw", 0), null, token, 90);
                return r.ExitCode == 0 && r.Output.Contains("jNQXAC9IVRw");
            } catch (Exception) { return false; }
        };

        async void RollbackEngine() {
            if (componentsBusy) return;
            if (queue.ActiveCount > 0) { C<TextBlock>("ComponentStatusText").Text = Loc.Error(ErrorCode.UpdateBusy); return; }
            SetComponentsBusy(true, null);
            try {
                var version = await Components.Rollback(ToolsDir, CancellationToken.None);
                SetComponentsBusy(false, version == null ? "" : Loc.F("comp.restored", version));
                extractors = null;
            } catch (Exception ex) { SetComponentsBusy(false, Loc.Error(ex)); }
            await CheckComponentsAsync();
        }

        internal string Diagnostics() {
            var sb = new StringBuilder();
            sb.AppendLine("Flow " + Program.Version + " · " + Environment.OSVersion + " · .NET " + Environment.Version);
            foreach (var c in ComponentRows) sb.AppendLine(c.Name + ": " + c.VersionText + " (" + c.State + ")");
            if (ComponentRows.Count == 0) sb.AppendLine("yt-dlp: " + (engineVersion ?? "?"));
            sb.AppendLine("Language=" + settings.Language + " Theme=" + settings.Theme + " Profile=" + settings.Profile + " Parallel=" + settings.Parallel + " Retries=" + settings.Retries + " Speed=" + settings.SpeedLimit + " Tray=" + settings.MinimizeToTray + "/" + settings.CloseToTray + " WindowsTask=" + settings.WindowsTask);
            sb.AppendLine("Queue: " + string.Join(", ", queue.Items.GroupBy(i => i.Job.State).Select(g => g.Key + "=" + g.Count())) + " · History: " + history.Count);
            sb.AppendLine("Data: " + paths.DataDir);
            sb.Append(appLog.ToString());
            return Engine.Mask(sb.ToString());
        }
    }
}
