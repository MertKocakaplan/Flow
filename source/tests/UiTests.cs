using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Flow;

namespace FlowTests {
    // WPF checks run in this process with an off-screen window; the user's desktop is never used.
    public static partial class UiTests {
        public static void Run() {
            if (Application.Current == null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            RunAll();
        }
        static partial void RunAll();

        public static void Pump() {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        public static void Wait(Func<bool> condition, int milliseconds) {
            var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (!condition() && DateTime.UtcNow < until) { Pump(); System.Threading.Thread.Sleep(15); }
            Pump();
        }
        public static void ShowOffscreen(Window window, double width, double height) {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000; window.Top = -32000; window.Width = width; window.Height = height;
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.Show(); Pump();
        }
        public static string Render(Window window, string name) {
            Pump();
            var content = (FrameworkElement)window.Content;
            var dpi = VisualTreeHelper.GetDpi(window);
            var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) {
                dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            }
            bitmap.Render(visual);
            var dir = Path.Combine(Environment.GetEnvironmentVariable("FLOW_TEST_OUT") ?? Path.GetTempPath(), "screens");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name + ".png");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
            return path;
        }
        public static T Find<T>(Window window, string name) where T : class { return window.FindName(name) as T; }
    }
}
