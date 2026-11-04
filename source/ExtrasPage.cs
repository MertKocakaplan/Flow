using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Flow {
    // Subtitles and clip range: offered only when the inspected item really has them.
    public partial class Controller {
        readonly HashSet<string> chosenSubs = new HashSet<string>();
        bool showAllAuto;

        void InitExtrasUi() {
            SelectTag(C<ComboBox>("SubModeBox"), "file");
            SelectTag(C<ComboBox>("ClipModeBox"), "fast");
            C<CheckBox>("AutoSubsBox").Click += (s, e) => { if (!C<CheckBox>("AutoSubsBox").IsChecked.GetValueOrDefault()) RemoveAutoChoices(); BuildSubtitleChoices(); };
            C<Button>("AllAutoSubsButton").Click += (s, e) => { showAllAuto = true; BuildSubtitleChoices(); };
            C<ComboBox>("SubModeBox").SelectionChanged += (s, e) => UpdateSubtitleHeader();
            C<ComboBox>("ChapterBox").SelectionChanged += (s, e) => {
                var item = C<ComboBox>("ChapterBox").SelectedItem as ComboBoxItem;
                var chapter = item == null ? null : item.Tag as Chapter;
                if (chapter == null) return;
                C<TextBox>("ClipStartBox").Text = ClipTime.Format(chapter.Start);
                C<TextBox>("ClipEndBox").Text = ClipTime.Format(chapter.End);
            };
            C<TextBox>("ClipStartBox").TextChanged += (s, e) => UpdateClipState();
            C<TextBox>("ClipEndBox").TextChanged += (s, e) => UpdateClipState();
            C<ComboBox>("ClipModeBox").SelectionChanged += (s, e) => UpdateClipState();
            C<Button>("ClearClipButton").Click += (s, e) => ClearClip();
            ExtraCombos.AddRange(new[] { "SubModeBox", "ClipModeBox" });
            ResetExtras();
        }

        void ResetExtras() {
            chosenSubs.Clear(); showAllAuto = false;
            C<CheckBox>("AutoSubsBox").IsChecked = false;
            BuildSubtitleChoices();
            var chapters = C<ComboBox>("ChapterBox");
            chapters.Items.Clear();
            chapters.Items.Add(new ComboBoxItem { Content = Loc.T("clip.chapter.none"), Tag = null });
            chapters.SelectedIndex = 0; chapters.IsEnabled = false;
            ClearClip();
        }
        void ClearClip() {
            C<TextBox>("ClipStartBox").Text = ""; C<TextBox>("ClipEndBox").Text = "";
            if (C<ComboBox>("ChapterBox").Items.Count > 0) C<ComboBox>("ChapterBox").SelectedIndex = 0;
            UpdateClipState();
        }

        void UpdateExtrasForMedia(MediaInfo info) {
            chosenSubs.Clear(); showAllAuto = false;
            BuildSubtitleChoices();
            var chapters = C<ComboBox>("ChapterBox");
            chapters.Items.Clear();
            chapters.Items.Add(new ComboBoxItem { Content = Loc.T(info.Chapters.Count > 0 ? "clip.chapter.pick" : "clip.chapter.none"), Tag = null });
            foreach (var ch in info.Chapters) chapters.Items.Add(new ComboBoxItem { Content = (string.IsNullOrWhiteSpace(ch.Title) ? "—" : ch.Title) + "  ·  " + Loc.Duration(ch.Start) + "–" + Loc.Duration(ch.End), Tag = ch });
            chapters.SelectedIndex = 0; chapters.IsEnabled = info.Chapters.Count > 0;
            C<TextBox>("ClipEndBox").Tag = info.Duration > 0 ? Loc.Duration(info.Duration) : Loc.T("clip.toEnd");
            UpdateClipState();
        }

        void RemoveAutoChoices() { foreach (var lang in chosenSubs.Where(l => l.StartsWith("auto:")).ToList()) chosenSubs.Remove(lang); }

        void BuildSubtitleChoices() {
            var list = C<WrapPanel>("SubtitleList"); var autoList = C<WrapPanel>("AutoSubtitleList");
            if (list == null) return;
            list.Children.Clear(); autoList.Children.Clear();
            var note = C<TextBlock>("SubtitleNote");
            var autoBox = C<CheckBox>("AutoSubsBox"); var allButton = C<Button>("AllAutoSubsButton");
            var subs = video == null ? new List<SubtitleTrack>() : video.Subtitles;
            var manual = subs.Where(t => !t.Automatic).ToList();
            var auto = subs.Where(t => t.Automatic).ToList();
            autoBox.Visibility = auto.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            allButton.Visibility = Visibility.Collapsed;
            foreach (var t in manual) list.Children.Add(SubtitleCheck(t.Lang, (t.Name ?? t.Lang) + " (" + t.Lang + ")"));
            if (autoBox.IsChecked == true && auto.Count > 0) {
                // Automatic captions can exist in ~100 translated languages; show the useful few first.
                var wanted = new[] { Loc.Lang, "en", "tr" };
                var shown = showAllAuto ? auto : auto.Where(t => t.IsOriginal || wanted.Contains(t.Lang)).ToList();
                if (shown.Count == 0) shown = auto.Take(6).ToList();
                foreach (var t in shown) autoList.Children.Add(SubtitleCheck("auto:" + t.Lang, Loc.F("subs.autoLabel", (t.IsOriginal ? Loc.T("subs.original") : t.Name ?? t.Lang) + " (" + t.Lang + ")")));
                if (!showAllAuto && shown.Count < auto.Count) { allButton.Content = Loc.F("subs.autoAll", auto.Count); allButton.Visibility = Visibility.Visible; }
            }
            string text = video == null ? Loc.T("subs.pending") : subs.Count == 0 ? Loc.T("subs.none") : "";
            note.Text = text;
            C<Expander>("SubtitleExpander").IsEnabled = video == null || subs.Count > 0;
            UpdateSubtitleHeader();
        }
        CheckBox SubtitleCheck(string key, string label) {
            var box = new CheckBox { Content = label, Tag = key, Margin = new Thickness(0, 0, 18, 8), IsChecked = chosenSubs.Contains(key) };
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            box.Click += (s, e) => { if (box.IsChecked == true) chosenSubs.Add(key); else chosenSubs.Remove(key); UpdateSubtitleHeader(); };
            return box;
        }
        void UpdateSubtitleHeader() {
            var header = C<TextBlock>("SubtitleHeader");
            if (header == null) return;
            bool clip = HasClipText;
            if (chosenSubs.Count > 0) header.Text = Loc.F("subs.header", chosenSubs.Count); else header.SetResourceReference(TextBlock.TextProperty, "s.subs.title");
            var note = C<TextBlock>("SubtitleNote");
            var extra = clip && chosenSubs.Count > 0 ? Loc.T("subs.clipNote") : AudioSelected && TagOf(C<ComboBox>("SubModeBox")) == "embed" && chosenSubs.Count > 0 ? Loc.T("subs.audioNote") : null;
            if (extra != null) note.Text = extra;
            else if (video != null && video.Subtitles.Count > 0) note.Text = "";
        }

        bool HasClipText { get { var a = C<TextBox>("ClipStartBox"); return a != null && (a.Text.Trim().Length > 0 || C<TextBox>("ClipEndBox").Text.Trim().Length > 0); } }

        // Reads the typed range; returns false with no exception when the fields are empty.
        bool TryReadClip(out double start, out double end) {
            start = 0; end = 0;
            if (!HasClipText) return false;
            var a = C<TextBox>("ClipStartBox").Text.Trim(); var b = C<TextBox>("ClipEndBox").Text.Trim();
            if (a.Length > 0 && !ClipTime.TryParse(a, out start)) throw new FlowException(ErrorCode.ClipInvalid);
            if (b.Length > 0) { if (!ClipTime.TryParse(b, out end)) throw new FlowException(ErrorCode.ClipInvalid); }
            else if (video != null && video.Duration > 0) end = video.Duration;
            else throw new FlowException(ErrorCode.ClipInvalid);
            ClipTime.Validate(start, end, video != null ? video.Duration : 0);
            return true;
        }

        void UpdateClipState() {
            var note = C<TextBlock>("ClipNote"); var header = C<TextBlock>("ClipHeader");
            if (note == null) return;
            var precise = TagOf(C<ComboBox>("ClipModeBox")) == "precise";
            double start, end;
            try {
                if (TryReadClip(out start, out end)) header.Text = Loc.F("clip.header", Loc.Duration(start), Loc.Duration(end), Loc.Duration(end - start));
                else header.SetResourceReference(TextBlock.TextProperty, "s.clip.title");
                note.Text = Loc.T(precise ? "clip.precise.note" : "clip.fast.note");
                note.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            } catch (FlowException ex) {
                header.SetResourceReference(TextBlock.TextProperty, "s.clip.title");
                note.Text = Loc.Error(ex);
                note.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            }
            UpdateSubtitleHeader();
            if (!loading) UpdateSelectionSummary();
        }

        partial void AddExtraOptions(JobOptions o) {
            o.SpeedLimit = SelectedSpeed();
            double start, end;
            try { if (TryReadClip(out start, out end)) { o.ClipStart = start; o.ClipEnd = end; o.ClipMode = TagOf(C<ComboBox>("ClipModeBox")) ?? "fast"; } } catch (FlowException) {}
            if (!o.HasClip && chosenSubs.Count > 0) {
                o.SubLangs = chosenSubs.Select(k => k.StartsWith("auto:") ? k.Substring(5) : k).Distinct().ToList();
                o.SubAuto = chosenSubs.Any(k => k.StartsWith("auto:"));
                o.SubMode = o.IsAudio ? "file" : TagOf(C<ComboBox>("SubModeBox")) ?? "file";
            }
        }
        partial void ValidateExtras(JobOptions o) {
            double start, end;
            TryReadClip(out start, out end);   // throws ClipInvalid for a bad range
            ScheduledTime();                   // throws ScheduleInvalid / SchedulePast
        }
        partial void OnOptionStates(bool audio) {
            var mode = C<ComboBox>("SubModeBox");
            if (mode == null) return;
            foreach (var item in mode.Items.OfType<ComboBoxItem>()) if (Convert.ToString(item.Tag) == "embed") item.IsEnabled = !audio;
            if (audio && TagOf(mode) == "embed") SelectTag(mode, "file");
        }
    }
}
