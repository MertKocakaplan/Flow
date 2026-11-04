using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Flow;

namespace FlowTests {
    public static class Stage5Unit {
        public static void Run() {
            double v;
            T.Check(ClipTime.TryParse("90", out v) && v == 90 && ClipTime.TryParse("1:30", out v) && v == 90 && ClipTime.TryParse("01:02:03", out v) && v == 3723, "clip times: seconds, m:ss, h:mm:ss");
            T.Check(ClipTime.TryParse("1:30,5", out v) && v == 90.5 && ClipTime.TryParse("1:30.5", out v) && v == 90.5, "clip times accept both decimal separators");
            T.Check(!ClipTime.TryParse("1:75", out v) && !ClipTime.TryParse("abc", out v) && !ClipTime.TryParse("", out v) && !ClipTime.TryParse("-5", out v), "invalid clip times rejected");
            T.Rejects(() => ClipTime.Validate(10, 5, 0), ErrorCode.ClipInvalid, "end before start");
            T.Rejects(() => ClipTime.Validate(0, 0.5, 0), ErrorCode.ClipInvalid, "clip shorter than a second");
            T.Rejects(() => ClipTime.Validate(0, 30, 19), ErrorCode.ClipInvalid, "clip beyond the video length");
            ClipTime.Validate(5, 12, 19); T.Check(true, "valid clip accepted");

            var yt = MediaParser.Parse(T.Fixture("youtube.json"), Platform.YouTube);
            var o = Media.Opt("compatible", "mp4", 360, "h264");
            o.SubLangs = new List<string> { "en", "de" };
            var job = new DownloadJob { Url = "https://www.youtube.com/watch?v=jNQXAC9IVRw", Options = o };
            var sel = FormatSelector.Select(yt, o);
            var args = Engine.JobArgs(job, sel, "C:\\o", "n", "C:\\t");
            T.Check(args.Contains("--write-subs") && args[args.IndexOf("--sub-langs") + 1] == "en,de" && args[args.IndexOf("--convert-subs") + 1] == "srt" && !args.Contains("--embed-subs") && !args.Contains("--write-auto-subs"), "separate subtitle files as SRT for the chosen languages");
            o.SubMode = "embed"; o.SubAuto = true;
            args = Engine.JobArgs(job, sel, "C:\\o", "n", "C:\\t");
            T.Check(args.Contains("--embed-subs") && !args.Contains("--write-subs") && args.Contains("--write-auto-subs"), "embedded subtitles leave no side files; automatic captions only when chosen");
            var audio = Media.Opt("mp3", "mp3", 0, "auto"); audio.SubLangs = new List<string> { "en" }; audio.SubMode = "embed";
            var aArgs = Engine.JobArgs(new DownloadJob { Url = job.Url, Options = audio }, FormatSelector.Select(yt, audio), "C:\\o", "n", "C:\\t");
            T.Check(aArgs.Contains("--write-subs") && !aArgs.Contains("--embed-subs"), "audio files get subtitles as a separate file");
            o.ClipStart = 5; o.ClipEnd = 12.5; o.ClipMode = "fast";
            args = Engine.JobArgs(job, sel, "C:\\o", "n", "C:\\t");
            T.Check(args[args.IndexOf("--download-sections") + 1] == "*5-12.5" && !args.Contains("--force-keyframes-at-cuts") && !args.Contains("--embed-subs") && !args.Contains("--sub-langs"), "fast clip downloads only the range; subtitles skipped for clips");
            o.ClipMode = "precise";
            T.Check(Engine.JobArgs(job, sel, "C:\\o", "n", "C:\\t").Contains("--force-keyframes-at-cuts"), "precise clip cuts at exact times");
            job.ClipMethod = "local";
            T.Check(!Engine.JobArgs(job, sel, "C:\\o", "n", "C:\\t").Contains("--download-sections"), "local-cut fallback downloads the full file");
            var clipSel = FormatSelector.Select(yt, o);
            var full = FormatSelector.Select(yt, Media.Opt("compatible", "mp4", 360, "h264"));
            T.Check(clipSel.Bytes > 0 && clipSel.Bytes < full.Bytes * 0.5 && !clipSel.SizeExact, "clip size estimated from the range");
            EngineExecutor.Verify(o, clipSel, yt, EngineExecutor.ParseProbe("{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\"},{\"codec_type\":\"audio\",\"codec_name\":\"aac\"}],\"format\":{\"duration\":\"7.9\"}}", 100));
            T.Check(true, "clip verified against the range length, keyframe slack allowed");
            T.Rejects(() => EngineExecutor.Verify(o, clipSel, yt, EngineExecutor.ParseProbe("{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\"},{\"codec_type\":\"audio\",\"codec_name\":\"aac\"}],\"format\":{\"duration\":\"1.2\"}}", 100)), ErrorCode.VerifyFailed, "a much shorter clip is not accepted");

            var dir = T.Scratch("subs-move");
            var from = Path.Combine(dir, "parts"); Directory.CreateDirectory(from);
            File.WriteAllText(Path.Combine(from, "source.en.srt"), "1"); File.WriteAllText(Path.Combine(from, "source.de.srt"), "2");
            var moved = EngineExecutor.MoveSubtitles(from, "source", dir, "Video [id] 720p");
            T.Check(moved.Count == 2 && File.Exists(Path.Combine(dir, "Video [id] 720p.en.srt")) && EngineExecutor.SubtitleFiles(dir, "Video [id] 720p").Count == 2, "subtitle files follow the final name");
        }
    }

    public static partial class UiTests {
        public static void Stage5Ui() {
            var paths = NewPaths("ui-extras");
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = "light";
            Store.SaveSettings(paths, s);
            var c = Open(paths, 1180, 1100, new FakeExecutor());
            var w = c.Window;
            T.Check(!Find<Expander>(w, "SubtitleExpander").IsEnabled == false && Find<TextBlock>(w, "SubtitleNote").Text == Loc.T("subs.pending"), "subtitle section waits for details");
            c.InfoSource = (u, token) => Task.FromResult(MediaParser.Parse(T.Fixture("youtube.json"), Platform.YouTube));
            var box = Find<TextBox>(w, "UrlBox");
            box.Text = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
            Wait(() => Find<WrapPanel>(w, "SubtitleList").Children.Count > 0, 4000);
            var subs = Find<WrapPanel>(w, "SubtitleList").Children.OfType<CheckBox>().ToList();
            T.Check(subs.Count == 2 && subs.Any(b => Convert.ToString(b.Tag) == "en") && Find<CheckBox>(w, "AutoSubsBox").Visibility == Visibility.Visible, "the video's own subtitle languages are offered, automatic ones on request");
            Find<Expander>(w, "SubtitleExpander").IsExpanded = true; Find<Expander>(w, "ClipExpander").IsExpanded = true; Pump();
            var en = subs.First(b => Convert.ToString(b.Tag) == "en");
            en.IsChecked = true; en.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, en)); Pump();
            T.Check(Find<TextBlock>(w, "SubtitleHeader").Text == Loc.F("subs.header", 1), "chosen subtitle count shown in the header");
            var auto = Find<CheckBox>(w, "AutoSubsBox"); auto.IsChecked = true; auto.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, auto)); Pump();
            T.Check(Find<WrapPanel>(w, "AutoSubtitleList").Children.Count > 0, "automatic captions listed when asked");
            Console.WriteLine("SCREEN " + Render(w, "stage5-subtitles-clip"));
            // Chapter fills the range
            var chapters = Find<ComboBox>(w, "ChapterBox");
            T.Check(chapters.IsEnabled && chapters.Items.Count == 4, "chapters offered as ranges");
            chapters.SelectedIndex = 2; Pump();
            T.Check(Find<TextBox>(w, "ClipStartBox").Text == "0:05" && Find<TextBox>(w, "ClipEndBox").Text == "0:17" && Find<TextBlock>(w, "ClipHeader").Text == Loc.F("clip.header", "0:05", "0:17", "0:12"), "chapter sets start and end");
            T.Check(Find<TextBlock>(w, "NamePreview").Text.Contains("00m05s-00m17s") && Find<TextBlock>(w, "SubtitleNote").Text == Loc.T("subs.clipNote"), "clip shows in the file name; subtitles explained as skipped");
            var clipJob = c.Enqueue(true);
            T.Check(clipJob != null && clipJob.Job.Options.ClipStart == 5 && clipJob.Job.Options.ClipEnd == 17 && clipJob.Job.Options.SubLangs.Count == 0, "queued job carries the range, without subtitles");
            // A bad range is explained and not queued
            box.Text = "https://www.youtube.com/watch?v=jNQXAC9IVRw"; Wait(() => Find<ComboBox>(w, "ChapterBox").Items.Count == 4, 4000);
            Find<TextBox>(w, "ClipStartBox").Text = "0:10"; Find<TextBox>(w, "ClipEndBox").Text = "0:40"; Pump();
            T.Check(Find<TextBlock>(w, "ClipNote").Text == Loc.Error(ErrorCode.ClipInvalid), "range beyond the video explained");
            T.Check(c.Enqueue(false) == null && c.MessageTextValue == Loc.Error(ErrorCode.ClipInvalid), "invalid range is not queued");
            Find<Button>(w, "ClearClipButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump();
            // Subtitles are carried by the job; embedding is unavailable for audio
            var enAgain = Find<WrapPanel>(w, "SubtitleList").Children.OfType<CheckBox>().First(b => Convert.ToString(b.Tag) == "en");
            enAgain.IsChecked = true; enAgain.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, enAgain)); Pump();
            SelectByTag(Find<ComboBox>(w, "SubModeBox"), "embed"); Pump();
            var subJob = c.Enqueue(true);
            T.Check(subJob != null && subJob.Job.Options.SubLangs.SequenceEqual(new[] { "en" }) && subJob.Job.Options.SubMode == "embed" && !subJob.Job.Options.HasClip, "queued job carries subtitle choices");
            box.Text = "https://www.youtube.com/watch?v=jNQXAC9IVRw"; Wait(() => Find<WrapPanel>(w, "SubtitleList").Children.Count > 0, 4000);
            SelectByTag(Find<ComboBox>(w, "SubModeBox"), "embed");
            SelectByTag(Find<ComboBox>(w, "ProfileBox"), "mp3"); Pump();
            var embedItem = Find<ComboBox>(w, "SubModeBox").Items.OfType<ComboBoxItem>().First(i => Convert.ToString(i.Tag) == "embed");
            T.Check(!embedItem.IsEnabled && TagOf(Find<ComboBox>(w, "SubModeBox")) == "file", "audio profiles cannot embed subtitles");
            SelectByTag(Find<ComboBox>(w, "ProfileBox"), "compatible"); Pump();
            // No subtitles in the source
            c.InfoSource = (u, token) => Task.FromResult(MediaParser.Parse(T.Fixture("instagram.json"), Platform.Instagram));
            box.Text = "https://www.instagram.com/reel/Chunk8-jurw/";
            Wait(() => Find<TextBlock>(w, "SubtitleNote").Text == Loc.T("subs.none"), 4000);
            T.Check(!Find<Expander>(w, "SubtitleExpander").IsEnabled && !Find<ComboBox>(w, "ChapterBox").IsEnabled, "no subtitle or chapter options when the source has none");
            w.Close();
        }
    }

    public static partial class LiveTests {
        public static void Stage5Live(QueueService q, string root) {
            const string zoo = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
            var outDir = Path.Combine(root, "extras");
            var subs = Media.Opt("compatible", "mp4", 360, "h264"); subs.Folder = outDir; subs.SubLangs = new List<string> { "en", "de" };
            var a = RunJob(q, zoo, subs, 240);
            Note("Subtitles as files: " + Describe(a) + " note=" + a.Job.Note);
            T.Check(a.Job.State == JobState.Completed && EngineExecutor.SubtitleFiles(outDir, Path.GetFileNameWithoutExtension(a.Job.OutputPath)).Count == 2 && a.Job.Note == null, "live: two subtitle files saved next to the video");
            var embed = subs.Copy(); embed.SubLangs = new List<string> { "en" }; embed.SubMode = "embed"; embed.Format = "mkv"; embed.Profile = "best"; embed.Codec = "auto"; embed.Folder = Path.Combine(root, "extras-embed");
            var b = RunJob(q, zoo, embed, 240);
            T.Check(b.Job.State == JobState.Completed && Probe(b.Job.OutputPath).SubtitleStreams == 1 && Directory.GetFiles(embed.Folder).Length == 1, "live: subtitle embedded without leftover files");
            foreach (var mode in new[] { "fast", "precise" }) {
                var clip = Media.Opt("compatible", "mp4", 360, "h264"); clip.Folder = outDir; clip.ClipStart = 5; clip.ClipEnd = 12; clip.ClipMode = mode;
                var c = RunJob(q, zoo, clip, 240);
                var d = c.Job.State == JobState.Completed ? Probe(c.Job.OutputPath).Duration : -1;
                Note("Clip " + mode + ": " + Describe(c) + " duration=" + d);
                T.Check(c.Job.State == JobState.Completed && d > 6.5 && d < (mode == "precise" ? 7.6 : 10.5) && c.Job.OutputPath.Contains("00m05s-00m12s"), "live: " + mode + " clip of 7 s has the expected length");
            }
            var local = Media.Opt("compatible", "mp4", 360, "h264"); local.Folder = Path.Combine(root, "extras-local"); local.ClipStart = 5; local.ClipEnd = 12; local.ClipMode = "precise";
            var parsed = Platforms.Parse(zoo);
            var item = q.Add(new DownloadJob { Url = parsed.Url, Platform = "YouTube", Options = local, ClipMethod = "local" }, false);
            UiTests.Wait(() => JobState.IsFinished(item.Job.State) || item.Job.State == JobState.NeedsAction, 240000);
            var ld = item.Job.State == JobState.Completed ? Probe(item.Job.OutputPath) : null;
            Note("Local cut: " + Describe(item) + " duration=" + (ld == null ? -1 : ld.Duration));
            T.Check(ld != null && ld.Duration > 6.5 && ld.Duration < 7.6 && ld.VCodec == "h264" && ld.ACodec == "aac", "live: full download + local precise cut gives an exact H.264/AAC clip");
        }
    }
}
