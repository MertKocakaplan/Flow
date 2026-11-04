using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flow {
    // The rail's ASCII field, after a WebGL "gradient ASCII" sketch, painted with WPF. Glyphs from
    // " =+xX0S8#@" rise from the foot of the rail and thin out as they climb, so the field is densest at the
    // bottom and gone before the navigation labels. It drifts upward on its own, slowly, like warm air; the
    // pointer does not touch it. It fades in once when the window opens and follows the display's frames
    // (30 a second) while the window is on screen. Windows animations off: one still frame. High contrast: nothing is drawn.
    public sealed class AsciiField : FrameworkElement {
        const string Glyphs = " =+xX0S8#@";
        const double CellW = 8.4, CellH = 14, GlyphSize = 11.5;
        const double QuietTop = 300;     // above this line (px from the top) the rail stays empty: the labels end near 270
        const double MaxAlpha = 0.34, FadeInSeconds = 1.4;
        const double Drift = 16;         // px per second the grain climbs
        const double FrameSeconds = 1 / 30.0;

        bool running;
        TimeSpan last;
        double clock, fade = 1, pending;
        double cacheDip;

        public AsciiField() {
            IsHitTestVisible = false;
            Focusable = false;
            Loaded += (s, e) => { Theme.Changed += Restyle; Start(); };
            Unloaded += (s, e) => { Theme.Changed -= Restyle; Sleep(); };
            IsVisibleChanged += (s, e) => { if (IsVisible) Start(); else Sleep(); };
        }
        void Restyle() { if (Theme.Current == "contrast") Sleep(); else Start(); InvalidateVisual(); }

        public bool IsAnimating { get { return running; } }
        public int CanvasHeight { get { return canvas == null ? 0 : canvasH; } }

        // Fades the field in and sets it drifting; with Windows animations off it shows one still frame.
        public void Start() {
            if (!Motion.Enabled || Theme.Current == "contrast" || !IsLoaded) { Sleep(); fade = 1; InvalidateVisual(); return; }
            if (running) return;
            if (clock == 0) fade = 0;
            Wake();
        }

        // Moves the field forward; each display frame calls it, tests call it directly.
        public void Advance(double seconds) {
            clock += seconds;
            fade = Math.Min(1, fade + seconds / FadeInSeconds);
            // Same size: repaint the bitmap in place, no new render pass; otherwise lay it out again.
            if (canvas != null && canvasW == (int)Math.Ceiling(ActualWidth * cacheDip) && canvasTop == (int)Math.Floor(FieldTop * cacheDip) && canvasH == (int)Math.Ceiling(ActualHeight * cacheDip) - canvasTop) Paint(); else InvalidateVisual();
        }

        void Wake() { if (running) return; running = true; last = TimeSpan.Zero; CompositionTarget.Rendering += OnFrame; }
        void Sleep() { if (!running) return; running = false; CompositionTarget.Rendering -= OnFrame; }
        void OnFrame(object sender, EventArgs e) {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (last == TimeSpan.Zero || now <= last) { last = now; return; }
            double dt = (now - last).TotalSeconds; last = now;
            var window = Window.GetWindow(this);
            if (window != null && window.WindowState == WindowState.Minimized) return;   // nothing to see, nothing to draw
            // The drift is slow, so 30 steps a second read as smooth and cost half of 60.
            pending += dt;
            if (pending + 0.002 < FrameSeconds) return;
            Advance(Math.Min(0.1, pending)); pending = 0;
        }

        // Opacity of the glyph drawn at a point (0 = empty); the tests use it to check the shape of the field.
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

        // Top of the field: never above the quiet line, and on a tall window a little lower still.
        public double FieldTop { get { return Math.Max(QuietTop, ActualHeight * 0.3); } }

        // Strength of the field at a point: 1 at the foot of the rail, thinning to 0 at the top, broken by a
        // grain that climbs with the clock; the reach of each column breathes a little, so the top edge wanders.
        double Value(double x, double y) {
            double top = FieldTop, span = ActualHeight - top;
            if (span <= 0 || y < top) return 0;
            double climb = (ActualHeight - y) / span;                              // 0 at the bottom, 1 at the top
            double reach = 0.82 + 0.18 * Noise(x / 46.0 + 5.3, clock * 0.12);
            double rise = Math.Pow(Math.Max(0, 1 - climb / reach), 0.95);
            double rising = y + clock * Drift;
            double grain = 0.55 * Noise(x / 24.0, rising / 20.0) + 0.45 * Noise(x / 10.0 + 3.1, (y + clock * Drift * 1.6) / 9.0 - 1.7);
            double footing = 0.35 + 0.65 * Smooth(8, 38, ActualHeight - y);       // keeps the version line readable
            return rise * footing * (0.3 + 0.7 * grain);
        }

        void Cell(int col, int row, out int index, out double alpha) {
            double x = (col + 0.5) * CellW, y = (row + 0.5) * CellH;
            double value = Value(x, y);
            index = 0; alpha = 0;
            if (value <= 0) return;
            // Sparse like hand-set ASCII, but soft: a cell eases in as the field's strength passes its own chance,
            // so as the grain climbs glyphs fade up and away instead of switching on and off.
            double density = 0.3 + 0.7 * Smooth(0.0, 0.6, value);
            double present = Smooth(-0.14, 0.14, density - Hash(col * 1.31 + 0.7, row * 2.17 + 4.1));
            if (present <= 0) return;
            double jitter = (Hash(col * 0.71 + 9.2, row * 1.73) - 0.5) * 0.16;
            double tone = Math.Pow(Math.Max(0, Math.Min(1, value + jitter)), 0.7);
            index = Math.Max(1, (int)Math.Round(tone * (Glyphs.Length - 1)));
            alpha = value * present * MaxAlpha * fade;
        }

        // The field is painted into one small bitmap: each glyph is rasterized once into a coverage sprite,
        // and a frame only copies sprites at their opacity. WPF then has a single image to compose, so the
        // drift costs a few percent of a core instead of re-laying out a thousand pieces of text every frame.
        byte[][] sprites;          // per glyph: coverage 0..255, spriteW x spriteH device pixels
        int spriteW, spriteH;
        Color ink;
        WriteableBitmap canvas;
        int[] pixels;
        int canvasTop, canvasW, canvasH;   // device pixels; the bitmap covers the rail from the field's top down

        void EnsureSprites(double dip) {
            if (sprites != null && Math.Abs(cacheDip - dip) < 0.001) return;
            cacheDip = dip;
            Typeface face = null;
            foreach (var name in new[] { "Cascadia Mono", "Consolas", "Courier New" }) {
                GlyphTypeface found;
                var candidate = new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                if (candidate.TryGetGlyphTypeface(out found)) { face = candidate; break; }
            }
            spriteW = (int)Math.Ceiling(CellW * dip) + 1; spriteH = (int)Math.Ceiling(CellH * dip) + 1;
            sprites = new byte[Glyphs.Length][];
            for (int g = 1; g < Glyphs.Length; g++) {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) {
                    var text = new FormattedText(Glyphs[g].ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? new Typeface("Consolas"), GlyphSize, Brushes.White, null, TextFormattingMode.Ideal, dip);
                    dc.DrawText(text, new Point((CellW - text.WidthIncludingTrailingWhitespace) / 2, (CellH - text.Height) / 2));
                }
                var bitmap = new RenderTargetBitmap(spriteW, spriteH, 96 * dip, 96 * dip, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var raw = new int[spriteW * spriteH];
                bitmap.CopyPixels(raw, spriteW * 4, 0);
                var coverage = new byte[raw.Length];
                for (int i = 0; i < raw.Length; i++) coverage[i] = (byte)((uint)raw[i] >> 24);
                sprites[g] = coverage;
            }
            canvas = null;
        }

        void EnsureCanvas(double dip) {
            int top = (int)Math.Floor(FieldTop * dip), w = (int)Math.Ceiling(ActualWidth * dip), h = (int)Math.Ceiling(ActualHeight * dip) - top;
            if (w <= 0 || h <= 0) { canvas = null; return; }
            if (canvas != null && canvasTop == top && canvasW == w && canvasH == h) return;
            canvasTop = top; canvasW = w; canvasH = h;
            canvas = new WriteableBitmap(w, h, 96 * dip, 96 * dip, PixelFormats.Pbgra32, null);
            pixels = new int[w * h];
        }

        // Fills the bitmap with the field as it is now.
        void Paint() {
            if (canvas == null) return;
            Array.Clear(pixels, 0, pixels.Length);
            double dip = cacheDip;
            int cols = (int)Math.Ceiling(ActualWidth / CellW), rows = (int)Math.Ceiling(ActualHeight / CellH);
            int first = Math.Max(0, (int)Math.Floor(FieldTop / CellH));
            for (int row = first; row < rows; row++) {
                int y0 = (int)Math.Round(row * CellH * dip) - canvasTop;
                for (int col = 0; col < cols; col++) {
                    int index; double alpha;
                    Cell(col, row, out index, out alpha);
                    if (index <= 0 || alpha < 0.004) continue;
                    var sprite = sprites[index];
                    int x0 = (int)Math.Round(col * CellW * dip), strength = (int)Math.Round(alpha * 256);
                    for (int sy = 0; sy < spriteH; sy++) {
                        int y = y0 + sy;
                        if (y < 0 || y >= canvasH) continue;
                        int line = y * canvasW, from = sy * spriteW;
                        for (int sx = 0; sx < spriteW; sx++) {
                            int x = x0 + sx;
                            if (x >= canvasW) break;
                            int a = sprite[from + sx] * strength >> 8;
                            if (a == 0) continue;
                            int at = line + x;
                            if (((uint)pixels[at] >> 24) >= a) continue;   // neighbouring sprites may share an edge pixel
                            pixels[at] = (a << 24) | ((ink.R * a / 255) << 16) | ((ink.G * a / 255) << 8) | (ink.B * a / 255);
                        }
                    }
                }
            }
            canvas.WritePixels(new Int32Rect(0, 0, canvasW, canvasH), pixels, canvasW * 4, 0);
        }

        protected override void OnRender(DrawingContext dc) {
            if (Theme.Current == "contrast" || ActualWidth <= 0 || ActualHeight <= 0) return;
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            EnsureSprites(dip);
            ink = (TryFindResource("SidebarText") as SolidColorBrush ?? Brushes.White).Color;
            EnsureCanvas(dip);
            if (canvas == null) return;
            Paint();
            dc.DrawImage(canvas, new Rect(0, canvasTop / dip, canvasW / dip, canvasH / dip));
        }
    }
}