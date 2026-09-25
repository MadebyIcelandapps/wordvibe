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
        const float GlassOpacity = 0.35f;
        readonly Timer idle = new Timer();
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
            idle.Interval = 8000;
            idle.Tick += delegate { HideNow(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; // no-activate, tool window, topmost
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; } // WM_MOUSEACTIVATE: MA_NOACTIVATE
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            glass = Native.MakeFrosted(Handle, Theme.Back, GlassOpacity);
        }

        public bool Showing { get { return IsHandleCreated && Native.IsWindowVisible(Handle); } }
        public Rectangle ScreenBounds { get { return Showing ? Bounds : Rectangle.Empty; } }

        public void HideNow()
        {
            idle.Stop();
            if (IsHandleCreated) Native.ShowWindow(Handle, 0);
        }

        // Shows `list` just above `caret` (screen coordinates).
        public void ShowWords(List<StripWord> list, Rectangle caret, bool exact)
        {
            words = list;
            if (font != null) font.Dispose();
            font = new Font(Theme.FontName, Math.Max(11f, Theme.Size * 0.85f));
            if (!IsHandleCreated) CreateHandle();

            Rectangle screen = Screen.FromPoint(caret.Location).WorkingArea;
            int maxW = Math.Min(900, screen.Width - 40);
            int h, w;
            using (var g = CreateGraphics())
            {
                h = (int)Math.Ceiling(g.MeasureString("Ag", font).Height) + 16;
                w = LayoutWords(g, maxW, h);
            }
            // Just above the line being typed, starting a little left of the words.
            int x = exact ? caret.Left - w + 40 : caret.Left + 8;
            int y = exact ? caret.Top - h - 6 : caret.Top - h - 4;
            if (y < screen.Top) y = caret.Bottom + 6;
            x = Math.Max(screen.Left + 4, Math.Min(x, screen.Right - w - 4));
            Native.SetWindowPos(Handle, new IntPtr(-1), x, y, w, h, 0x0010 | 0x0040); // TOPMOST, NOACTIVATE | SHOWWINDOW
            if (Log.On)
            {
                var sb = new System.Text.StringBuilder();
                foreach (StripWord sw in words) sb.Append(sw.Text).Append('=').Append(sw.State).Append(' ');
                Log.Write("strip at " + x + "," + y + "," + w + "," + h + " glass=" + glass + " words: " + sb);
            }
            Invalidate();
            Update();
            idle.Stop();
            idle.Start();
        }

        // Places the words from the right (newest) end, dropping old ones that do not fit.
        int LayoutWords(Graphics g, int maxW, int h)
        {
            int pad = 6, gap = 6, left = 10 + h; // room for the speaker button
            int total = left;
            var widths = new int[words.Count];
            for (int i = 0; i < words.Count; i++) widths[i] = (int)g.MeasureString(words[i].Text, font).Width + 2 * pad;
            int first = words.Count;
            while (first > 0 && total + widths[first - 1] + gap <= maxW - 10) { first--; total += widths[first] + gap; }
            int x = left;
            for (int i = 0; i < words.Count; i++)
            {
                if (i < first) { words[i].Box = Rectangle.Empty; continue; }
                words[i].Box = new Rectangle(x, 5, widths[i], h - 10);
                x += widths[i] + gap;
            }
            speaker = new Rectangle(6, 5, h - 10, h - 10);
            return Math.Max(x + 4, 160);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Grayscale anti-aliasing keeps the text solid on the see-through glass.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            // On glass, see-through pixels show the blur, tinted by Windows at 35%.
            if (glass) g.Clear(Color.Transparent);
            else g.Clear(Theme.Back);
            using (var border = new Pen(Color.FromArgb(90, Theme.Border)))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

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
            using (var path = new GraphicsPath())
            {
                int d = radius * 2;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(b, path);
            }
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
