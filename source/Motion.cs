using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Flow {
    // Short motion for state changes only: hover and press in the templates, the page change in ShowPage.
    // XAML storyboards read MotionFast / MotionMedium as StaticResource, so Install must run before a window's
    // XAML is loaded. With Windows animations turned off every duration is zero and the change is instant.
    public static class Motion {
        static bool? enabled;
        public static bool Enabled {
            get { return enabled.HasValue ? enabled.Value : SystemParameters.ClientAreaAnimation; }
            set { enabled = value; }
        }
        public static Duration Fast { get { return new Duration(Enabled ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero); } }
        public static Duration Medium { get { return new Duration(Enabled ? TimeSpan.FromMilliseconds(200) : TimeSpan.Zero); } }

        public static void Install(ResourceDictionary target) {
            if (target == null) return;
            target["MotionFast"] = Fast;
            target["MotionMedium"] = Medium;
        }

        // A page that becomes visible settles in from transparent; never on first show, never with motion off.
        public static void Reveal(UIElement page) {
            if (!Enabled) { page.BeginAnimation(UIElement.OpacityProperty, null); page.Opacity = 1; return; }
            var fade = new DoubleAnimation(0, 1, Medium) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            page.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
