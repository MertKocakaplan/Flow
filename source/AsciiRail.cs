using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Flow {
    // The rail's ASCII field, after a WebGL "gradient ASCII" sketch, redrawn with WPF text. Glyphs from
    // " =+xX0S8#@" rise from the lower rail like a horizon glow; the upper rail, where the labels sit, stays
    // empty. It decodes in once, then rests (no timer). The pointer scrambles a short trail and a click sends
    // a ring outward; both fade within about a second. While moving it follows the display's frames. Windows animations off:
    // no intro, no trail, no ring. High contrast: nothing is drawn. It never takes input.
    public sealed class AsciiField : FrameworkElement {
        const string Glyphs = " =+xX0S8#@";
        const double CellW = 8.4, CellH = 14, GlyphSize = 11.5;
        const double TrailLife = 0.7, WaveLife = 1.2, IntroSeconds = 2.2, TrailRadius = 26, WaveSpeed = 300, WaveWidth = 16;
        const double RestAlpha = 0.3, ScrambleAlpha = 0.42;
        const int Levels = 12, MaxTrail = 48, MaxWaves = 4;
        const double TrailStep = 10;

        sealed class Mark { public Point At; public double Age; }
        readonly List<Mark> trail = new List<Mark>(), waves = new List<Mark>();
        bool running;
        TimeSpan last;
        double intro = 1, clock;
        FormattedText[,] cache;
        double cacheDip;

        public AsciiField() {
            IsHitTestVisible = false;
            Focusable = false;
            Loaded += (s, e) => { Theme.Changed += Restyle; Intro(); };
            Unloaded += (s, e) => { Theme.Changed -= Restyle; Sleep(); };
        }
        void Restyle() { cache = null; rest = null; InvalidateVisual(); }

        public bool IsAnimating { get { return running; } }

        // Runs the decode once when the window opens.
        public void Intro() {
            if (!Motion.Enabled) { intro = 1; InvalidateVisual(); return; }
            intro = 0; Wake();
        }
        public void Trail(Point at) {
            if (!Motion.Enabled) return;
            if (trail.Count > 0) {
                var head = trail[0];
                double gap = (at - head.At).Length;
                if (gap < 4) return;
                // Fill the gap between two pointer events so a fast move leaves one continuous trail.
                int steps = Math.Min(10, (int)(gap / TrailStep));
                for (int i = 1; i < steps; i++) trail.Insert(0, new Mark { At = head.At + (at - head.At) * ((double)i / steps), Age = head.Age * (1 - (double)i / steps) });
            }
            trail.Insert(0, new Mark { At = at });
            while (trail.Count > MaxTrail) trail.RemoveAt(trail.Count - 1);
            Wake();
        }
        public void Wave(Point at) {
            if (!Motion.Enabled) return;
            waves.Insert(0, new Mark { At = at });
            if (waves.Count > MaxWaves) waves.RemoveAt(waves.Count - 1);
            Wake();
        }

        // Moves the field forward; each display frame calls it while moving, tests call it directly.
        public void Advance(double seconds) {
            clock += seconds;
            intro = Math.Min(1, intro + seconds / IntroSeconds);
            foreach (var m in trail) m.Age += seconds;
            foreach (var m in waves) m.Age += seconds;
            trail.RemoveAll(m => m.Age >= TrailLife);
            waves.RemoveAll(m => m.Age >= WaveLife);
            if (intro >= 1 && trail.Count == 0 && waves.Count == 0) Sleep();
            InvalidateVisual();
        }

        // While something moves the field follows the display's own frames (usually 60 Hz); at rest it
        // leaves the frame clock entirely, so an idle rail costs nothing.
        void Wake() { if (running) return; running = true; last = TimeSpan.Zero; CompositionTarget.Rendering += OnFrame; }
        void Sleep() { if (!running) return; running = false; CompositionTarget.Rendering -= OnFrame; }
        void OnFrame(object sender, EventArgs e) {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (last == TimeSpan.Zero || now <= last) { last = now; return; }
            double dt = (now - last).TotalSeconds; last = now;
            Advance(Math.Min(0.1, dt));
        }

        // Opacity of the glyph drawn at a point (0 = empty); the tests use it to keep the labels clear.
        public double AlphaAt(Point at) {
            if (ActualWidth <= 0 || ActualHeight <= 0) return 0;
            int col = (int)Math.Floor(at.X / CellW), row = (int)Math.Floor(at.Y / CellH);
            int index; double alpha;
            Cell(col, row, out index, out alpha);
            return index == 0 ? 0 : alpha;
        }

        static double Hash(double x, double y) {
            double h = Math.Sin(x * 127.1 + y * 311.7) * 43758.5453;
            return h - Math.Floor(h);
        }
        static double Smooth(double a, double b, double t) { t = Math.Max(0, Math.Min(1, (t - a) / (b - a))); return t * t * (3 - 2 * t); }
        static double Noise(double x, double y) {
            double ix = Math.Floor(x), iy = Math.Floor(y), fx = x - ix, fy = y - iy;
            double ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy);
            double a = Hash(ix, iy), b = Hash(ix + 1, iy), c = Hash(ix, iy + 1), d = Hash(ix + 1, iy + 1);
            return a + (b - a) * ux + (c - a) * uy + (a - b - c + d) * ux * uy;
        }

        // The resting field: a horizon band in the lower rail, broken up by noise; nothing above mid-height.
        double Rest(double x, double y) {
            double xn = x / ActualWidth - 0.5, yn = y / ActualHeight;
            double arch = 0.8 - 0.08 * xn * xn;
            double band = Math.Exp(-Math.Pow((yn - arch) / 0.16, 2)) * Smooth(0.52, 0.66, yn);
            double grain = 0.5 * Noise(x / 22.0, y / 18.0) + 0.5 * Noise(x / 9.0 + 3.1, y / 8.0 - 1.7);
            return band * (0.3 + 0.7 * grain);
        }

        bool Moving { get { return trail.Count > 0 || waves.Count > 0; } }

        void Cell(int col, int row, out int index, out double alpha) {
            double x = (col + 0.5) * CellW, y = (row + 0.5) * CellH;
            double value = Rest(x, y);
            // Intro: the lower rail decodes first; a revealing cell shows a random glyph.
            double order = 1 - y / Math.Max(1, ActualHeight);
            double reveal = Math.Max(0, Math.Min(1, (intro * 1.45 - order) / 0.45));
            double scramble = reveal > 0 && reveal < 1 ? 1 - reveal : 0;
            var here = new Point(x, y);
            foreach (var m in trail) {
                double k = 1 - m.Age / TrailLife, d = (here - m.At).Length;
                scramble = Math.Max(scramble, k * k * Math.Exp(-(d * d) / (TrailRadius * TrailRadius)));
            }
            foreach (var m in waves) {
                double k = 1 - m.Age / WaveLife, d = (here - m.At).Length, ring = m.Age * WaveSpeed;
                scramble = Math.Max(scramble, k * k * Math.Exp(-Math.Pow((d - ring) / WaveWidth, 2)));
            }
            // Sparse like hand-set ASCII: a cell shows up only when its own chance beats the field's strength.
            bool present = Hash(col * 1.31 + 0.7, row * 2.17 + 4.1) < 0.25 + 0.75 * Smooth(0.08, 0.7, value);
            double jitter = (Hash(col * 0.71 + 9.2, row * 1.73) - 0.5) * 0.18;
            double tone = Math.Pow(Math.Max(0, Math.Min(1, value + jitter)), 0.75);
            index = present && reveal >= 1 ? (int)Math.Round(tone * (Glyphs.Length - 1)) : 0;
            alpha = present && reveal >= 1 ? value * RestAlpha : 0;
            // Each cell has a fixed threshold, so as the scramble fades cells drop out one by one, once;
            // the glyph turns on the cell's own beat, so the trail never pulses as a whole.
            if (scramble > 0.05 && Hash(col * 5.1 + 2.3, row * 3.3 + 0.9) < scramble * 1.3) {
                double beat = Math.Floor(clock * 14 + Hash(col * 0.37, row * 0.53) * 14);
                index = 1 + (int)Math.Floor(Hash(col + beat * 1.37, row + 7.1) * (Glyphs.Length - 1));
                alpha = Math.Max(alpha, scramble * ScrambleAlpha);
            }
        }

        void EnsureCache(double dip) {
            if (cache != null && Math.Abs(cacheDip - dip) < 0.001) return;
            cache = new FormattedText[Glyphs.Length, Levels + 1];
            cacheDip = dip;
            var ink = (TryFindResource("SidebarText") as SolidColorBrush ?? Brushes.White).Color;
            var face = new Typeface(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            for (int g = 1; g < Glyphs.Length; g++)
                for (int l = 1; l <= Levels; l++) {
                    var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255.0 * l / Levels), ink.R, ink.G, ink.B)); brush.Freeze();
                    cache[g, l] = new FormattedText(Glyphs[g].ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, GlyphSize, brush, null, TextFormattingMode.Display, dip);
                }
        }

        DrawingGroup rest;
        Size restSize;

        void Draw(DrawingContext dc, int col, int row, int index, double alpha) {
            int level = (int)Math.Round(alpha * Levels);
            if (index <= 0 || level <= 0) return;
            var text = cache[index, Math.Min(Levels, level)];
            dc.DrawText(text, new Point(col * CellW + (CellW - text.WidthIncludingTrailingWhitespace) / 2, row * CellH + (CellH - text.Height) / 2));
        }

        protected override void OnRender(DrawingContext dc) {
            if (Theme.Current == "contrast" || ActualWidth <= 0 || ActualHeight <= 0) return;
            EnsureCache(VisualTreeHelper.GetDpi(this).PixelsPerDip);
            int cols = (int)Math.Ceiling(ActualWidth / CellW), rows = (int)Math.Ceiling(ActualHeight / CellH);
            var size = new Size(ActualWidth, ActualHeight);
            dc.PushClip(new RectangleGeometry(new Rect(size)));
            if (intro < 1) {
                for (int row = 0; row < rows; row++)
                    for (int col = 0; col < cols; col++) { int index; double alpha; Cell(col, row, out index, out alpha); Draw(dc, col, row, index, alpha); }
            } else {
                // At rest the field is one cached drawing; moving cells are drawn over it.
                if (rest == null || restSize != size) {
                    rest = new DrawingGroup();
                    var keepTrail = trail.ToArray(); var keepWaves = waves.ToArray(); trail.Clear(); waves.Clear();
                    using (var r = rest.Open())
                        for (int row = 0; row < rows; row++)
                            for (int col = 0; col < cols; col++) { int index; double alpha; Cell(col, row, out index, out alpha); Draw(r, col, row, index, alpha); }
                    trail.AddRange(keepTrail); waves.AddRange(keepWaves);
                    rest.Freeze(); restSize = size;
                }
                dc.DrawDrawing(rest);
                var ground = TryFindResource("Sidebar") as Brush;   // covers a resting glyph under a moving one
                if (Moving)
                    for (int row = 0; row < rows; row++)
                        for (int col = 0; col < cols; col++) {
                            int index; double alpha; double restAlpha;
                            int restIndex; Cell(col, row, out index, out alpha);
                            RestCell(col, row, out restIndex, out restAlpha);
                            if (index != restIndex || Math.Abs(alpha - restAlpha) > 0.001) {
                                if (restIndex > 0 && ground != null) dc.DrawRectangle(ground, null, new Rect(col * CellW, row * CellH, CellW, CellH));
                                Draw(dc, col, row, index, alpha);
                            }
                        }
            }
            dc.Pop();
        }

        // The cell as it looks with nothing moving.
        void RestCell(int col, int row, out int index, out double alpha) {
            double x = (col + 0.5) * CellW, y = (row + 0.5) * CellH, value = Rest(x, y);
            bool present = Hash(col * 1.31 + 0.7, row * 2.17 + 4.1) < 0.25 + 0.75 * Smooth(0.08, 0.7, value);
            double jitter = (Hash(col * 0.71 + 9.2, row * 1.73) - 0.5) * 0.18;
            double tone = Math.Pow(Math.Max(0, Math.Min(1, value + jitter)), 0.75);
            index = present ? (int)Math.Round(tone * (Glyphs.Length - 1)) : 0;
            alpha = present ? value * RestAlpha : 0;
        }
    }
}
