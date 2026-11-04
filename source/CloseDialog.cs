using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;

namespace Flow {
    internal enum CloseChoice { Cancel, Tray, Exit }

    internal sealed class CloseDialog {
        internal readonly Window Window;
        internal CloseChoice Choice { get; private set; }

        internal CloseDialog(Window owner, int active) {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CloseDialog.xaml"))
                Window = (Window)XamlReader.Load(stream);
            Window.Owner = owner;
            Window.Icon = owner.Icon;
            // Use the main window's button templates and the same live theme/language resources.
            Window.Resources.MergedDictionaries.Add(owner.Resources);
            Theme.Attach(Window);
            Loc.Attach(Window);
            if (active > 0) {
                ((FrameworkElement)Window.FindName("ActiveNotice")).Visibility = Visibility.Visible;
                ((TextBlock)Window.FindName("ActiveText")).Text = Loc.F("close.active", active);
            }
            var tray = (Button)Window.FindName("TrayButton");
            tray.Click += (s, e) => Finish(CloseChoice.Tray);
            ((Button)Window.FindName("ExitButton")).Click += (s, e) => Finish(CloseChoice.Exit);
            ((Button)Window.FindName("CancelButton")).Click += (s, e) => Finish(CloseChoice.Cancel);
            Window.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; Finish(CloseChoice.Cancel); } };
            Window.Loaded += (s, e) => { tray.Focus(); };
        }

        void Finish(CloseChoice choice) { Choice = choice; Window.Close(); }
        internal CloseChoice Show() { Window.ShowDialog(); return Choice; }
        internal static CloseChoice Ask(Window owner, int active) { return new CloseDialog(owner, active).Show(); }
    }
}
