using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Flow;

namespace FlowTests {
    public static partial class UiTests {
        static CloseChoice AnswerCloseDialog(Window owner, int active, string answer, string screenshot) {
            var dialog = new CloseDialog(owner, active);
            var window = dialog.Window;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000; window.Top = -32000; window.ShowActivated = false;
            Exception failure = null;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                try {
                    var tray = Find<Button>(window, "TrayButton");
                    T.Check(window.Owner == owner && !window.ShowInTaskbar && tray.IsDefault && Find<Button>(window, "CancelButton").IsCancel,
                        "close dialog is owned, keyboard-accessible and defaults to keeping downloads running");
                    T.Check(Find<Border>(window, "ActiveNotice").IsVisible == (active > 0), "close dialog reports active work only when present");
                    if (screenshot != null) {
                        T.Check(new[] { "TrayButton", "ExitButton", "CancelButton" }.All(name => Find<Button>(window, name).ActualHeight >= 40)
                            && AutomationProperties.GetName(tray) == Loc.T("close.tray")
                            && Visuals(window).OfType<TextBlock>().Where(t => t.IsVisible).All(t => t.ActualWidth <= window.ActualWidth),
                            "close dialog actions fit and use translated accessible labels: " + screenshot);
                        Console.WriteLine("SCREEN " + Render(window, screenshot));
                    }
                    if (answer == "window-close") window.Close();
                    else if (answer == "escape") window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    else Find<Button>(window, answer).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                } catch (Exception ex) { failure = ex; if (window.IsVisible) window.Close(); }
            }));
            var result = dialog.Show();
            if (failure != null) throw failure;
            return result;
        }

        static void CloseChoiceUi() {
            string xaml;
            using (var stream = typeof(Controller).Assembly.GetManifestResourceStream("CloseDialog.xaml"))
            using (var reader = new StreamReader(stream)) xaml = reader.ReadToEnd();
            T.Check(Regex.Matches(xaml, @"DynamicResource s\.([A-Za-z0-9.]+)").Cast<Match>().All(m => Loc.Has(m.Groups[1].Value)), "all close-dialog text has Turkish and English resources");
            var paths = NewPaths("ui-close-choice");
            var settings = SettingsRules.Defaults(paths);
            settings.CloseToTray = true; // Previously saved preference must not suppress the new question.
            settings.Notify = false; settings.Language = "tr"; settings.Theme = "light";
            Store.SaveSettings(paths, settings);
            var controller = Open(paths, 1000, 760);
            controller.ConfirmClose = true;
            int prompts = 0;
            foreach (var language in new[] { "tr", "en" }) foreach (var theme in new[] { "light", "dark" }) {
                Loc.Set(language); Theme.Set(theme);
                controller.ClosePrompt = (owner, active) => { prompts++; return AnswerCloseDialog(owner, active, "CancelButton", "close-" + language + "-" + theme); };
                controller.Window.Close(); Pump();
                T.Check(controller.Window.IsVisible && !controller.Queue.ShuttingDown, "Cancel keeps Flow open despite the old close-to-tray setting: " + language + " / " + theme);
            }
            foreach (var answer in new[] { "escape", "window-close" }) {
                controller.ClosePrompt = (owner, active) => { prompts++; return AnswerCloseDialog(owner, active, answer, null); };
                controller.Window.Close(); Pump();
                T.Check(controller.Window.IsVisible && !controller.Queue.ShuttingDown, answer + " dismisses the question without exiting or hiding Flow");
            }
            T.Check(prompts == 6, "every X press offers a fresh choice, including when the queue is idle");

            var options = Media.Opt("compatible", "mp4", 1080, "h264"); options.Folder = paths.DefaultDownloads;
            Directory.CreateDirectory(options.Folder);
            var running = controller.Queue.Add(new DownloadJob { Url = "https://example.com/slowClose", Platform = "Other", Title = "slowClose", Options = options }, false);
            Wait(() => running.IsActive, 3000);
            controller.ClosePrompt = (owner, active) => { prompts++; return AnswerCloseDialog(owner, active, "ExitButton", "close-exit-active-en-dark"); };
            controller.Window.Close(); Pump();
            var saved = JsonStore.Convert<QueueFile>(JsonStore.Load(paths.QueueFile).Data);
            T.Check(controller.Queue.ShuttingDown && !controller.Window.IsVisible && prompts == 7 && saved.Jobs.Any(j => j.Id == running.Job.Id && j.State == JobState.Paused),
                "Exit saves the active download paused and closes Flow without a second question");
        }
    }
}
