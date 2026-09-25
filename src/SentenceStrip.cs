// The sentence strip: a frosted-glass bar just above the text being typed that
// shows the sentence so far, with a soft green behind words that are spelled
// right and a soft red behind ones to check. Click a red word to fix it; click
// the speaker to hear the sentence. It never takes focus from the app.
//
// Written for C# 5 so the csc.exe that ships inside Windows can compile it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SoundSpell
{
    sealed class StripWord
    {
        public string Text;
        public int Start;          // where it starts in KeyWatcher's line of text
        public WordState State;
        public Rectangle Box;      // on the strip, set when drawn
    }

    enum WordState { Typing, Good, Bad, Name }

    sealed class SentenceStrip : Form
    {
        readonly Timer idle = new Timer();
        string glassFor;       // the settings the glass was made with
        bool layered;          // drawn as a see-through picture (no blur), see ApplyGlass
        public static int TestExtraWidth;   // the automatic checks add blank space to measure
        List<StripWord> words = new List<StripWord>();
        Rectangle speaker;
        bool glass;
        Font font;

        public event Action<StripWord> WordClicked;
        public event Action SpeakerClicked;

        public SentenceStrip()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            idle.Interval = 6000;
            idle.Tick += delegate { HideNow(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; // no-activate, tool window, topmost
                if (layered) cp.ExStyle |= 0x00080000;                // WS_EX_LAYERED
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; } // WM_MOUSEACTIVATE: MA_NOACTIVATE
            base.WndProc(ref m);
        }

        // Frosted glass: what is behind is blurred and tinted at the chosen opacity
        // (35% by default). Re-made when the settings change.
        //
        // Two ways to draw it:
        // - Frosted: Windows blurs what is behind and tints it. Needs Windows'
        //   "Transparency effects" to be on.
        // - See-through: SoundSpell paints the tint itself as a picture with holes
        //   in it (a layered window). No blur, but it works whatever Windows' setting.
        void ApplyGlass()
        {
            int opacity = Prefs.GetNumber("GlassOpacity", 35);
            bool blur = Prefs.Get("GlassBlur", true) && WindowsTransparencyOn;
            string key = opacity + "/" + blur + "/" + Theme.Back.ToArgb();
            if (key == glassFor) return;
            glassFor = key;
            if (layered == blur) { layered = !blur; RecreateHandle(); Hwnd = Handle; }
            glass = blur && Native.MakeFrosted(Handle, Theme.Back, opacity / 100f, true);
            if (blur && !glass) { layered = true; RecreateHandle(); Hwnd = Handle; } // blur refused: paint it ourselves
            if (Log.On) Log.Write("strip glass: " + (glass ? "frosted" : "see-through, no blur") + ", opacity " + opacity + "%");
        }

        // Windows' own switch (Settings, Accessibility, Visual effects, Transparency effects).
        public static bool WindowsTransparencyOn
        {
            get
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("EnableTransparency");
                    try { return v == null || Convert.ToInt32(v) != 0; } catch (Exception) { return true; }
                }
            }
        }

        // What the strip looks like with the current settings, for the settings window.
        public static string GlassDescription()
        {
            if (!Prefs.Get("GlassBlur", true)) return "See-through, no blur (frosted is switched off).";
            if (!WindowsTransparencyOn) return "See-through, no blur: Windows' Transparency effects are off (Settings, Accessibility, Visual effects).";
            return "Frosted: what is behind is blurred and tinted.";
        }

        // The window handle, safe to read from any thread (for telling clicks apart).
        public volatile IntPtr Hwnd;

        public bool Showing { get { return IsHandleCreated && Native.IsWindowVisible(Handle); } }
        public Rectangle ScreenBounds { get { return Showing ? Bounds : Rectangle.Empty; } }

        public void HideNow()
        {
            idle.Stop();
            if (IsHandleCreated && Native.IsWindowVisible(Handle))
            {
                Native.ShowWindow(Handle, 0);
                if (Log.On) Log.Write("strip hidden");
            }
        }

        // Hide in `ms`, unless shown again before then.
        public void HideAfter(int ms)
        {
            if (!Showing) return;
            idle.Stop();
            idle.Interval = ms;
            idle.Start();
        }

        // Sizes: small by default, so it stays out of the way.
        public static readonly string[] Sizes = { "Small", "Medium", "Large" };
        public static readonly string[] Places = { "Just above my typing", "Just below my typing", "Bottom of the screen" };
        public static readonly string[] Shows = { "Always while I type", "Only when a word needs a look" };

        // Shows `list` just above `caret` (screen coordinates).
        public void ShowWords(List<StripWord> list, Rectangle caret, bool exact)
        {
            words = list;
            string size = Prefs.GetText("StripSize", Sizes[0]);
            float pt = size == "Large" ? 13f : size == "Medium" ? 11f : 9f;
            if (font != null) font.Dispose();
            font = new Font(Theme.FontName, pt);
            if (!IsHandleCreated) CreateHandle();
            Hwnd = Handle;
            ApplyGlass();

            Rectangle screen = Screen.FromPoint(caret.Location).WorkingArea;
            int maxW = Math.Min(size == "Large" ? 800 : size == "Medium" ? 640 : 520, screen.Width - 40);
            int h, w;
            using (var g = CreateGraphics())
            {
                h = (int)Math.Ceiling(g.MeasureString("Ag", font).Height) + 8;
                w = LayoutWords(g, maxW, h);
            }
            w += TestExtraWidth;
            string place = Prefs.GetText("StripPlace", Places[0]);
            int x, y;
            if (place == Places[2])
            {
                // Out of the way, centred at the bottom of the screen.
                x = screen.Left + (screen.Width - w) / 2;
                y = screen.Bottom - h - 12;
            }
            else
            {
                // Ends just past the cursor, so it sits over the words just typed.
                x = exact ? caret.Left - w + 24 : caret.Left + 8;
                bool below = place == Places[1];
                y = below ? caret.Bottom + 4 : caret.Top - h - 4;
                if (!below && y < screen.Top) y = caret.Bottom + 4;
                if (below && y + h > screen.Bottom) y = caret.Top - h - 4;
            }
            x = Math.Max(screen.Left + 4, Math.Min(x, screen.Right - w - 4));
            if (layered)
            {
                RenderLayered(x, y, w, h);
                Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040); // TOPMOST, NOSIZE|NOMOVE|NOACTIVATE|SHOW
            }
            else Native.SetWindowPos(Handle, new IntPtr(-1), x, y, w, h, 0x0010 | 0x0040); // TOPMOST, NOACTIVATE | SHOWWINDOW
            if (Log.On)
            {
                var sb = new System.Text.StringBuilder();
                foreach (StripWord sw in words) sb.Append(sw.Text).Append('=').Append(sw.State).Append(' ');
                Log.Write("strip at " + x + "," + y + "," + w + "," + h + " glass=" + (glass || layered) + " mode=" + (layered ? "layered" : "frosted") + " words: " + sb);
            }
            Invalidate();
            Update();
            idle.Stop();
            idle.Interval = 6000;
            idle.Start();
        }

        // Places the words from the right (newest) end, dropping old ones that do not fit.
        int LayoutWords(Graphics g, int maxW, int h)
        {
            int pad = 4, gap = 3, left = 6 + h; // room for the speaker button
            int total = left;
            var widths = new int[words.Count];
            for (int i = 0; i < words.Count; i++) widths[i] = (int)g.MeasureString(words[i].Text, font).Width + 2 * pad;
            int first = words.Count;
            while (first > 0 && total + widths[first - 1] + gap <= maxW - 10) { first--; total += widths[first] + gap; }
            int x = left;
            for (int i = 0; i < words.Count; i++)
            {
                if (i < first) { words[i].Box = Rectangle.Empty; continue; }
                words[i].Box = new Rectangle(x, 3, widths[i], h - 6);
                x += widths[i] + gap;
            }
            speaker = new Rectangle(3, 3, h - 6, h - 6);
            return Math.Max(x + 2, 60);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (layered) return; // drawn by RenderLayered
            var g = e.Graphics;
            // On glass, see-through pixels show the blur, tinted by Windows.
            if (glass) g.Clear(Color.Transparent);
            else g.Clear(Theme.Back);
            using (var border = new Pen(Color.FromArgb(90, Theme.Border)))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            DrawWords(g);
        }

        // See-through without Windows' help: the strip is drawn into a picture whose
        // background is the tint at the chosen opacity, with rounded corners, and
        // Windows shows it pixel by pixel.
        void RenderLayered(int x, int y, int w, int h)
        {
            int alpha = (int)(255 * Prefs.GetNumber("GlassOpacity", 35) / 100f);
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(Color.FromArgb(alpha, Theme.Back))) Fill(g, b, new Rectangle(0, 0, w - 1, h - 1), 8);
                    using (var border = new Pen(Color.FromArgb(Math.Min(255, alpha + 60), Theme.Border)))
                    using (var path = RoundPath(new Rectangle(0, 0, w - 1, h - 1), 8))
                        g.DrawPath(border, path);
                    DrawWords(g);
                }
                Native.ShowLayered(Handle, bmp, x, y);
            }
        }

        void DrawWords(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Grayscale anti-aliasing keeps the text solid on the see-through glass.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using (var text = new SolidBrush(Theme.Text))
            using (var dim = new SolidBrush(Theme.Dim))
            {
                // Speaker button: reads the sentence out.
                using (var b = new SolidBrush(Color.FromArgb(70, Theme.Highlight))) Fill(g, b, speaker, 6);
                using (var icon = new Font("Segoe MDL2 Assets", font.Size * 0.8f))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("", icon, text, speaker, sf);

                foreach (StripWord w in words)
                {
                    if (w.Box.IsEmpty) continue;
                    Color tint = w.State == WordState.Good ? Color.FromArgb(110, 76, 175, 80)
                               : w.State == WordState.Bad ? Color.FromArgb(140, 229, 57, 53)
                               : Color.FromArgb(40, Theme.Dim);
                    using (var b = new SolidBrush(tint)) Fill(g, b, w.Box, 7);
                    if (w.State == WordState.Bad)
                        using (var pen = new Pen(Color.FromArgb(220, 198, 40, 40), 2f))
                            g.DrawLine(pen, w.Box.Left + 5, w.Box.Bottom - 3, w.Box.Right - 5, w.Box.Bottom - 3);
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(w.Text, font, w.State == WordState.Typing ? dim : text, w.Box, sf);
                }
            }
        }

        static void Fill(Graphics g, Brush b, Rectangle r, int radius)
        {
            using (var path = RoundPath(r, radius)) g.FillPath(b, path);
        }

        static GraphicsPath RoundPath(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            idle.Stop(); idle.Start();
            if (speaker.Contains(e.Location)) { if (SpeakerClicked != null) SpeakerClicked(); return; }
            foreach (StripWord w in words)
                if (!w.Box.IsEmpty && w.Box.Contains(e.Location)) { if (WordClicked != null) WordClicked(w); return; }
        }

        // Screen point just under a word, for the list of matches.
        public Point Below(StripWord w)
        {
            return PointToScreen(new Point(w.Box.Left, Height + 2));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { idle.Dispose(); if (font != null) font.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
