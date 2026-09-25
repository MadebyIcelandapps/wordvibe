// Caret: where on screen the text cursor is, so the strip and the list can sit
// right next to the words being typed. Classic Windows apps say so directly;
// browsers and newer apps are asked through UI Automation (the accessibility API).
//
// Written for C# 5 so the csc.exe that ships inside Windows can compile it.

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace SoundSpell
{
    static class Caret
    {
        // Screen rectangle of the caret (or of the text box, if that is all we can
        // learn), or Rectangle.Empty. `exact` tells which.
        public static Rectangle Find(out bool exact)
        {
            exact = true;
            Rectangle r = FromWin32();
            if (!r.IsEmpty) return r;
            r = FromAutomation(out exact);
            return r;
        }

        static Rectangle FromWin32()
        {
            IntPtr fg = Native.GetForegroundWindow();
            uint tid = Native.GetWindowThreadProcessId(fg, IntPtr.Zero);
            var gti = new Native.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            if (!Native.GetGUIThreadInfo(tid, ref gti) || gti.hwndCaret == IntPtr.Zero) return Rectangle.Empty;
            var p = new Native.POINT { x = gti.rcCaret.left, y = gti.rcCaret.top };
            Native.ClientToScreen(gti.hwndCaret, ref p);
            int h = Math.Max(8, gti.rcCaret.bottom - gti.rcCaret.top);
            return new Rectangle(p.x, p.y, Math.Max(1, gti.rcCaret.right - gti.rcCaret.left), h);
        }

        // UI Automation calls go into the other app and can be slow, so they run on
        // their own thread with a time limit.
        static Rectangle FromAutomation(out bool exact)
        {
            Rectangle result = Rectangle.Empty;
            bool isExact = false;
            var t = new Thread(delegate ()
            {
                try
                {
                    AutomationElement el = AutomationElement.FocusedElement;
                    if (el == null) return;
                    object pat;
                    if (el.TryGetCurrentPattern(TextPattern.Pattern, out pat))
                    {
                        TextPatternRange[] sel = ((TextPattern)pat).GetSelection();
                        if (sel != null && sel.Length > 0)
                        {
                            TextPatternRange range = sel[0].Clone();
                            System.Windows.Rect[] rects = range.GetBoundingRectangles();
                            if (rects == null || rects.Length == 0)
                            {
                                // A plain cursor has no size: measure the letter before it.
                                range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1);
                                rects = range.GetBoundingRectangles();
                                if (rects != null && rects.Length > 0)
                                {
                                    System.Windows.Rect last = rects[rects.Length - 1];
                                    result = new Rectangle((int)last.Right, (int)last.Top, 1, Math.Max(8, (int)last.Height));
                                    isExact = true;
                                    return;
                                }
                            }
                            else
                            {
                                System.Windows.Rect last = rects[rects.Length - 1];
                                result = new Rectangle((int)last.Right, (int)last.Top, 1, Math.Max(8, (int)last.Height));
                                isExact = true;
                                return;
                            }
                        }
                    }
                    System.Windows.Rect box = el.Current.BoundingRectangle;
                    if (!box.IsEmpty && box.Width > 0)
                        result = new Rectangle((int)box.Left, (int)box.Top, (int)box.Width, (int)box.Height);
                }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join(400);
            exact = isExact;
            return result;
        }
    }
}
