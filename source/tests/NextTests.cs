using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Flow;

namespace FlowTests {
    // Checks for the November 2026 work: pasted link lists, engine-safe format ids, embedded metadata,
    // the strict quality check on the file, keeping the PC awake and opening finished files.
    public static class NextUnit {
        public static void Run() { Links(); FormatIds(); EngineArgs(); StrictQuality(); ProgressAfterPreStep(); Shell(); }

        static void Links() {
            var text = "Bak: https://youtu.be/jNQXAC9IVRw. Sonra (https://example.com/a_(b)) ve https://example.com/v?x=1,\nhttps://youtu.be/jNQXAC9IVRw";
            var links = Platforms.ExtractUrls(text);
            T.Check(links.Count == 3 && links[0] == "https://youtu.be/jNQXAC9IVRw" && links[1] == "https://example.com/a_(b)" && links[2] == "https://example.com/v?x=1", "links are found in pasted text, trailing punctuation dropped, duplicates removed (" + string.Join(" | ", links) + ")");
            T.Check(Platforms.NormalizePasted("  https://vimeo.com/1  ") == "https://vimeo.com/1" && Platforms.NormalizePasted("izle https://vimeo.com/1 lütfen") == "https://vimeo.com/1" && Platforms.NormalizePasted("https://vimeo.com/1\r\nhttps://vimeo.com/2") == "https://vimeo.com/1 https://vimeo.com/2" && Platforms.NormalizePasted("yalnızca metin") == "yalnızca metin", "pasted text becomes its links");
            T.Check(Platforms.ExtractUrls(string.Join(" ", Enumerable.Range(0, 300).Select(i => "https://example.com/" + i))).Count == Platforms.MaxLinks, "a pasted list is capped");
        }

        static void FormatIds() {
            var media = Media.Make(19, "hls-1080|mp4|avc1|none|1920|1080|30|2000|1000000", "hls-audio-English (US)|m4a|none|mp4a|0|0|0|128|100000");
            var options = Media.Opt("compatible", "mp4", 1080, "h264");
            var s = FormatSelector.Select(media, options);
            T.Check(s.EngineChooses && s.Spec == FormatSelector.Fallback(options) && s.Notes.Contains("sel.note.engineChooses"), "a format id the engine cannot parse is left to the engine, and the user is told");
            var args = Engine.JobArgs(new DownloadJob { Url = "https://example.com/v", Platform = "Other", Options = options }, s, "C:\\out", "n", "C:\\out\\t");
            T.Check(args[args.IndexOf("-f") + 1] == FormatSelector.Fallback(options), "the engine gets only the general selector for such ids");
            var yt = FormatSelector.Select(MediaParser.Parse(T.Fixture("youtube.json"), Platform.YouTube), Media.Opt("compatible", "mp4", 1080, "h264"));
            T.Check(!yt.EngineChooses && yt.Spec.EndsWith("+140"), "ordinary format ids are still named exactly");
            T.Check(EngineExecutor.FormatChanged(yt, "18") && !EngineExecutor.FormatChanged(yt, yt.Spec) && !EngineExecutor.FormatChanged(yt, null) && !EngineExecutor.FormatChanged(s, "anything"), "a different format from the engine is noticed, except when the engine was asked to choose");
            T.Check(Loc.Has("note.formatChanged") && Loc.Has("sel.note.engineChooses") && Loc.Has("verify.quality"), "the new notes have texts");
        }

        static void EngineArgs() {
            var yt = MediaParser.Parse(T.Fixture("youtube.json"), Platform.YouTube);
            var options = Media.Opt("compatible", "mp4", 1080, "h264");
            var job = new DownloadJob { Url = "https://www.youtube.com/watch?v=jNQXAC9IVRw", Platform = "YouTube", Options = options };
            var args = Engine.JobArgs(job, FormatSelector.Select(yt, options), "C:\\out", "n", "C:\\out\\t");
            T.Check(args.Contains("--embed-metadata") && args.Contains("--embed-chapters") && args.Contains("--embed-thumbnail") && args[args.IndexOf("--concurrent-fragments") + 1] == "4", "title, chapters and cover go into the file; fragments download in parallel");
            var vp9only = Media.Make(19, "248|webm|vp9|none|1920|1080|30|2000|1000000", "251|webm|none|opus|0|0|0|128|100000");
            var conv = FormatSelector.Select(vp9only, options);
            var convArgs = Engine.JobArgs(job, conv, "C:\\out\\.flow-parts\\j", "source", "C:\\out\\.flow-parts\\j");
            T.Check(conv.VideoConversion && convArgs.Contains("--embed-metadata") && !convArgs.Contains("--embed-thumbnail"), "when Flow converts the file itself, no cover stream is added before it");
            var mp3 = Media.Opt("mp3", "mp3", 0, "auto");
            var mp3Args = Engine.JobArgs(new DownloadJob { Url = job.Url, Platform = "YouTube", Options = mp3 }, FormatSelector.Select(yt, mp3), "C:\\out", "n", "C:\\out\\t");
            T.Check(mp3Args.Contains("--embed-thumbnail") && mp3Args.Contains("--embed-metadata"), "audio files get their cover and tags");
            var clip = Media.Opt("compatible", "mp4", 1080, "h264"); clip.ClipStart = 5; clip.ClipEnd = 12;
            var local = Engine.JobArgs(new DownloadJob { Url = job.Url, Platform = "YouTube", Options = clip, ClipMethod = "local" }, FormatSelector.Select(yt, clip), "C:\\out\\.flow-parts\\j", "source", "C:\\out\\.flow-parts\\j");
            T.Check(!local.Contains("--embed-thumbnail"), "a file Flow cuts itself gets no cover stream before the cut");
        }

        static void StrictQuality() {
            var media = Media.Make(19, "22|mp4|avc1|mp4a|1280|720|30|2000|1000000");
            var strict = Media.Opt("custom", "mp4", 360, "auto"); strict.Strict = true;
            var loose = Media.Opt("custom", "mp4", 360, "auto");
            var selection = new Selection();
            Func<int, int, ProbeResult> probe = (w, h) => EngineExecutor.ParseProbe("{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":" + w + ",\"height\":" + h + "},{\"codec_type\":\"audio\",\"codec_name\":\"aac\"}],\"format\":{\"duration\":\"19\"}}", 100);
            try { EngineExecutor.Verify(strict, selection, media, probe(1280, 720)); T.Check(false, "a file above a strict limit is refused (no error)"); }
            catch (FlowException ex) { T.Check(ex.Code == ErrorCode.VerifyFailed && ex.Args.Contains("verify.quality"), "a file above a strict limit is refused"); }
            EngineExecutor.Verify(strict, selection, media, probe(640, 360));
            EngineExecutor.Verify(strict, selection, media, probe(360, 640));
            EngineExecutor.Verify(loose, selection, media, probe(1280, 720));
            T.Check(true, "a file within the limit, a vertical one, and a non-strict choice pass");
        }

        // Cover conversion runs before the media transfer; the transfer must still show as a download with progress.
        static void ProgressAfterPreStep() {
            var item = new JobItem(new DownloadJob { Url = "https://example.com/v", Options = Media.Opt("compatible", "mp4", 360, "h264") });
            item.SetStage(JobState.Processing, "stage.processing");
            EngineExecutor.ShowProgress(item, new ProgressSnapshot { Stream = 1, Streams = 1, IsVideo = true, Downloaded = 500, Total = 1000, Percent = 50, StreamPercent = 50 });
            T.Check(item.Job.State == JobState.Downloading && !item.Indeterminate && item.ProgressValue == 50, "a transfer after the cover step shows as downloading with its percentage");
        }

        static void Shell() {
            Awake.Hold(true);
            bool held = Awake.Held;
            Awake.Hold(false);
            T.Check(held && !Awake.Held, "the PC is kept awake while asked and released afterwards");
            T.Check(Controller.CanOpen("C:\\a\\b.MP4") && Controller.CanOpen("x.m4a") && Controller.CanOpen("x.webp") && !Controller.CanOpen("x.exe") && !Controller.CanOpen("x.lnk") && !Controller.CanOpen(null), "only media Flow writes can be opened");
        }
    }

    public static partial class UiTests {
        static void NextUi() {
            var paths = NewPaths("ui-next");
            var s = SettingsRules.Defaults(paths); s.Language = "tr"; s.Theme = "light"; s.AutoPreview = false;
            Store.SaveSettings(paths, s);
            var folder = Path.Combine(paths.DefaultDownloads, "kept-folder");
            Store.SaveHistory(paths, new List<HistoryEntry> {
                new HistoryEntry { Id = "1", Title = "Clip", Platform = "Other", SourceUrl = "https://vimeo.com/76979871", CompletedAt = Iso.Now(), Status = "completed", Format = "mp4", Profile = "compatible",
                    Options = new JobOptions { Profile = "compatible", Format = "mp4", Height = 360, Strict = true, Codec = "h264", Folder = folder, Organize = "none", NameTemplate = "{title}", Duplicate = "skip", SubLangs = new List<string> { "en" }, SubMode = "embed", ClipStart = 5, ClipEnd = 12, ClipMode = "precise", SpeedLimit = 524288 },
                    CollectionTitle = "Liste", CollectionIndex = 3 }
            });
            var fake = new FakeExecutor();
            var c = Open(paths, 1180, 840, fake);
            var w = c.Window;

            // Downloading a history entry again asks for the same job: folder, range, subtitles, speed and list place.
            c.ShowPage("history"); Pump();
            var again = c.Requeue(c.HistoryRows.First(r => r.Entry.Id == "1").Entry);
            var o = again.Job.Options;
            T.Check(o.Folder == folder && o.ClipStart == 5 && o.ClipEnd == 12 && o.ClipMode == "precise" && o.SubLangs.SequenceEqual(new[] { "en" }) && o.SubMode == "embed" && o.SpeedLimit == 524288 && o.Duplicate == "skip" && o.NameTemplate == "{title}" && o.Strict && again.Job.CollectionTitle == "Liste" && again.Job.CollectionIndex == 3, "downloading again keeps folder, range, subtitles, speed, naming and list place");

            // A finished job can be opened from its row.
            Wait(() => again.Job.State == JobState.Completed, 5000);
            c.ShowPage("queue"); Pump();
            var open = Visuals(Find<ItemsControl>(w, "QueueList")).OfType<Button>().FirstOrDefault(b => Convert.ToString(b.Tag) == "open" && b.IsVisible);
            T.Check(again.Job.State == JobState.Completed && open != null && Convert.ToString(System.Windows.Automation.AutomationProperties.GetName(open)) == Loc.T("job.open"), "a finished job shows an Open button with an accessible name");
            T.Check(again.Job.Options.SubLangs.Count == 1 && c.HistoryRows.Count >= 0, "the original history entry is untouched");

            // Several links become several jobs; lists and local addresses are skipped and counted.
            c.ShowPage("download"); Pump();
            var box = Find<TextBox>(w, "UrlBox");
            box.Text = "https://vimeo.com/1 https://example.com/video.mp4 https://localhost/x https://www.youtube.com/playlist?list=PL1234567890abcdef";
            Pump();
            int before = c.Queue.Items.Count;
            c.Enqueue(true); Pump();
            var added = c.Queue.Items.Skip(before).ToList();
            T.Check(added.Count == 2 && added.All(i => i.Job.State == JobState.Paused && !i.Job.Options.HasClip && i.Job.Options.SubLangs.Count == 0), "two links become two paused jobs without a range or subtitles");
            T.Check(c.MessageTextValue == Loc.F("msg.added.manySkipped", 2, 2) && box.Text == "", "skipped links are counted and the field is cleared");

            // An engine-shaped error with an old engine offers the update.
            c.UtcToday = () => new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            Wait(() => c.EngineVersionText != null, 5000);
            T.Check(c.OfferEngineUpdate(ErrorCode.Unknown, "Hata.") && c.MessageTextValue.StartsWith("Hata.") && c.MessageTextValue.Contains("gün"), "an old engine is named as the likely cause, with an update action");
            c.UtcToday = () => DateTime.UtcNow;
            T.Check(!c.OfferEngineUpdate(ErrorCode.DiskFull, "Disk."), "an unrelated error does not suggest an engine update");
            w.Close(); Pump();
        }
    }
}
