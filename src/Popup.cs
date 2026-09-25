// The list of matches that shows next to the text, without taking focus from the
// app being typed in. Point at a word to hear it; click it to put it in.
//
// Written for C# 5 so the csc.exe that ships inside Windows can compile it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SoundSpell
{
    sealed class Popup : Form
    {
        readonly Timer hideTimer = new Timer();
        List<Suggestion> items = new List<Suggestion>();
        int current;          // 1-based row that is on screen now, 0 for none
        int hover = -1;
        string footer = "";
        Font font, small;
        int row;

        public event EventHandler TimedOut;
        public event Action<int> RowPointed;   // 0-based
        public event Action<int> RowClicked;   // 0-based

        public Popup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // Not TopMost = true: WinForms then activates the window when it shows,
            // and the fixed word gets typed into the popup instead of the app.
            DoubleBuffered = true;
            hideTimer.Interval = 15000;
            hideTimer.Tick += delegate { HideNow(); if (TimedOut != null) TimedOut(this, EventArgs.Empty); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; // no-activate, tool window, topmost
                cp.ClassStyle |= 0x00020000; // drop shadow
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; } // WM_MOUSEACTIVATE: MA_NOACTIVATE
            base.WndProc(ref m);
        }

        public bool Showing { get { return IsHandleCreated && Native.IsWindowVisible(Handle); } }

        // The window handle, safe to read from any thread (for telling clicks apart).
        public volatile IntPtr Hwnd;

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Hwnd = Handle; }

        public Rectangle ScreenBounds { get { return Showing ? Bounds : Rectangle.Empty; } }

        public void HideNow()
        {
            hideTimer.Stop();
            hover = -1;
            if (IsHandleCreated) Native.ShowWindow(Handle, 0); // SW_HIDE
        }

        Point mouseWhenShown;

        public void ShowChoices(List<Suggestion> list, int current, string footer, Point at)
        {
            items = list; this.current = current; this.footer = footer; hover = -1;
            mouseWhenShown = Control.MousePosition;
            if (font != null) { font.Dispose(); small.Dispose(); }
            font = Theme.Big(); small = Theme.Small();
            BackColor = Theme.Back;

            int w = 240, labelW, wordW = 0, meaningW = 0;
            using (var g = CreateGraphics())
            {
                row = (int)Math.Ceiling(g.MeasureString("Ag", font).Height) + 10;
                labelW = (int)g.MeasureString("Ctrl+9 ", small).Width + 8;
                foreach (Suggestion s in items)
                {
                    wordW = Math.Max(wordW, (int)g.MeasureString(s.Word, font).Width);
                    if (s.Meaning != null) meaningW = Math.Max(meaningW, (int)g.MeasureString(s.Meaning, small).Width);
                }
                w = Math.Max(w, 10 + labelW + wordW + (meaningW > 0 ? meaningW + 24 : 0) + 16);
                w = Math.Max(w, (int)g.MeasureString(footer, small).Width + 24);
            }
            wordX = 10 + labelW;
            meaningX = wordX + wordW + 18;
            int h = 12 + items.Count * row + (int)(small.Height * 1.8f);
            Rectangle screen = Screen.FromPoint(at).WorkingArea;
            w = Math.Min(w, screen.Width - 20);
            int x = Math.Min(Math.Max(at.X, screen.Left), screen.Right - w);
            int y = at.Y + h > screen.Bottom ? at.Y - h - 30 : at.Y;
            // Shown with plain Win32 calls that never take focus from the app being typed in.
            Native.SetWindowPos(Handle, new IntPtr(-1), x, y, w, h, 0x0010 | 0x0040); // HWND_TOPMOST, NOACTIVATE | SHOWWINDOW
            Invalidate();
            Update();
            hideTimer.Stop();
            hideTimer.Start();
        }

        int wordX, meaningX;

        int RowAt(int y)
        {
            int r = (y - 6) / Math.Max(1, row);
            return y >= 6 && r >= 0 && r < items.Count ? r : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // The list can pop up under a mouse that is not moving: that is not pointing.
            if (Control.MousePosition == mouseWhenShown) return;
            int r = RowAt(e.Y);
            if (r != hover)
            {
                hover = r;
                Invalidate();
                if (r >= 0 && RowPointed != null) RowPointed(r);
            }
            hideTimer.Stop(); hideTimer.Start();
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int r = RowAt(e.Y);
            if (r >= 0 && RowClicked != null) RowClicked(r);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (font == null) return;
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var border = new Pen(Theme.Border)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            int y = 6;
            using (var dim = new SolidBrush(Theme.Dim))
            using (var text = new SolidBrush(Theme.Text))
            using (var hiText = new SolidBrush(Theme.HighlightText))
            using (var hi = new SolidBrush(Theme.Highlight))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    bool on = i + 1 == current || i == hover;
                    if (on) g.FillRectangle(hi, 4, y, Width - 8, row - 2);
                    float mid = y + (row - 2) / 2f;
                    g.DrawString("Ctrl+" + (i + 1), small, dim, 10, mid - small.Height / 2f);
                    g.DrawString(items[i].Word, font, on ? hiText : text, wordX, mid - font.Height / 2f);
                    if (items[i].Meaning != null)
                        g.DrawString(items[i].Meaning, small, dim, meaningX, mid - small.Height / 2f);
                    y += row;
                }
                g.DrawString(footer, small, dim, 10, y + 4);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { hideTimer.Dispose(); if (font != null) { font.Dispose(); small.Dispose(); } }
            base.Dispose(disposing);
        }
    }
}
