// Watches keys system-wide. Fixes "@@word" when the word ends, fixes the word
// just typed when the shortcut is pressed (tap Shift twice by default), and lets
// her pick, hear, or undo from the popup.
//
// Written for C# 5 so the csc.exe that ships inside Windows can compile it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace SoundSpell
{
    sealed class KeyWatcher
    {
        const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11,
                  VK_MENU = 0x12, VK_CAPITAL = 0x14, VK_SPACE = 0x20, VK_LWIN = 0x5B, VK_RWIN = 0x5C,
                  VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
        static readonly IntPtr Marker = new IntPtr(0x5350454C); // tags the keys we send ourselves

        static readonly Regex Trigger = new Regex(@"@@([\p{L}']{1,40})$", RegexOptions.Compiled);
        // The last word on the line and what was typed after it (spaces, punctuation).
        static readonly Regex LastWord = new Regex(@"(?<![\p{L}'@])([\p{L}']{2,40})([ .,;:!?)""]{0,3})$", RegexOptions.Compiled);
        static readonly Regex WordBefore = new Regex(@"([\p{L}']+)[^\p{L}']*$", RegexOptions.Compiled);

        readonly TrayApp app;
        // What was typed lately on this line, as it is on screen. Cleared by Enter,
        // arrows, clicks, shortcuts and switching window, because then we no longer
        // know what is next to the cursor.
        readonly StringBuilder recent = new StringBuilder();
        readonly Native.LowLevelKeyboardProc proc, mouseProc;
        IntPtr hook = IntPtr.Zero, mouseHook = IntPtr.Zero;
        IntPtr lastWindow = IntPtr.Zero;
        Popup popup;

        // The last fix, which Ctrl+digit or a click can change while the popup is up.
        List<Suggestion> choices;
        string original;      // what was typed, without @@
        string inserted;      // what is on screen now in place of it
        string termText;      // what is on screen after it (" ", ". ", "" when held back)

        // Matches shown while @@word is still being typed.
        string liveWord;
        List<Suggestion> liveFound;
        string liveFoundFor;
        bool popupUp;

        // Lookups run on a worker so typing never waits for them.
        readonly object lookupLock = new object();
        readonly AutoResetEvent lookupWake = new AutoResetEvent(false);
        string lookupWanted, lookupPrevious;

        // Shortcut taps: a key pressed and let go with nothing else in between.
        bool shiftClean, rctrlClean;
        DateTime lastShiftTap = DateTime.MinValue;

        public bool Enabled = true;
        public bool AutoReplace = true;
        public string Hotkey = Prefs.Hotkeys[0];

        public KeyWatcher(TrayApp app)
        {
            this.app = app;
            proc = HookProc;
            mouseProc = MouseProc;
        }

        public void Start()
        {
            hook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
            mouseHook = Native.SetWindowsHookEx(WH_MOUSE_LL, mouseProc, Native.GetModuleHandle(null), 0);
            var worker = new Thread(LookupLoop);
            worker.IsBackground = true;
            worker.Start();
        }

        public void Stop()
        {
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            hook = mouseHook = IntPtr.Zero;
        }

        // A click can move the text cursor, so whatever was being typed no longer counts
        // (unless the click is on the popup itself).
        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
                {
                    var pt = (Native.POINT)Marshal.PtrToStructure(lParam, typeof(Native.POINT));
                    bool onPopup = popup != null && popup.ScreenBounds.Contains(pt.x, pt.y);
                    if (!onPopup)
                    {
                        recent.Length = 0;
                        if (popupUp) CloseAll();
                    }
                }
            }
            return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Enabled)
            {
                int msg = wParam.ToInt32();
                var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                if (k.dwExtraInfo != Marker)
                {
                    bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    // While our own typing is waiting to go out, hold the user's keys
                    // back and replay them after it, so nothing lands in the middle.
                    if (busy)
                    {
                        queued.Add(k);
                        return new IntPtr(1);
                    }
                    int vk = (int)k.vkCode;
                    bool wasDown = vk < 256 && held[vk];
                    if (vk < 256) held[vk] = isDown;
                    try
                    {
                        WatchTaps(vk, isDown, wasDown);
                        if (isDown && OnKeyDown(vk, (int)k.scanCode)) return new IntPtr(1);
                    }
                    catch (Exception e) { recent.Length = 0; if (Log.On) Log.Write("error " + e); }
                }
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        // "Tap Shift twice" and "Tap right Ctrl": the key goes down and up with no
        // other key in between, so Shift+letter for capitals never counts.
        void WatchTaps(int vk, bool isDown, bool wasDown)
        {
            bool shift = vk == VK_LSHIFT || vk == VK_RSHIFT || vk == VK_SHIFT;
            if (isDown)
            {
                if (wasDown) return; // held down, repeating
                if (shift) shiftClean = true;
                else if (vk == VK_RCONTROL) rctrlClean = true;
                else { shiftClean = false; rctrlClean = false; lastShiftTap = DateTime.MinValue; }
                return;
            }
            if (shift && shiftClean)
            {
                shiftClean = false;
                if (Hotkey != Prefs.Hotkeys[0]) return;
                DateTime now = DateTime.UtcNow;
                if ((now - lastShiftTap).TotalMilliseconds < 450) { lastShiftTap = DateTime.MinValue; FixLastWord(); }
                else lastShiftTap = now;
            }
            else if (vk == VK_RCONTROL && rctrlClean)
            {
                rctrlClean = false;
                if (Hotkey == Prefs.Hotkeys[2]) FixLastWord();
            }
        }

        // Which keys are down, from the key events themselves (GetAsyncKeyState lags
        // behind inside a low-level hook).
        readonly bool[] held = new bool[256];
        bool busy;
        readonly List<Native.KBDLLHOOKSTRUCT> queued = new List<Native.KBDLLHOOKSTRUCT>();

        bool Down(int vk)
        {
            if (vk == VK_CONTROL) return held[VK_LCONTROL] || held[VK_RCONTROL] || held[VK_CONTROL];
            if (vk == VK_MENU) return held[VK_LMENU] || held[VK_RMENU] || held[VK_MENU];
            if (vk == VK_SHIFT) return held[VK_LSHIFT] || held[VK_RSHIFT] || held[VK_SHIFT];
            return held[vk];
        }

        static bool IsModifier(int vk)
        {
            return vk == VK_SHIFT || vk == VK_CONTROL || vk == VK_MENU || vk == VK_LWIN || vk == VK_RWIN || vk == VK_CAPITAL
                || (vk >= VK_LSHIFT && vk <= VK_RMENU);
        }

        bool OnKeyDown(int vk, int scan)
        {
            if (Log.On) Log.Write("key " + vk.ToString("X2") + " ctrl=" + Down(VK_CONTROL) + " popup=" + popupUp + " choices=" + (choices != null) + " recent=[" + recent + "]");
            if (IsModifier(vk)) return false;

            IntPtr fg = Native.GetForegroundWindow();
            if (fg != lastWindow) { lastWindow = fg; recent.Length = 0; CloseAll(); }

            bool ctrl = Down(VK_CONTROL), alt = Down(VK_MENU), win = Down(VK_LWIN) || Down(VK_RWIN), shift = Down(VK_SHIFT);
            bool altGr = ctrl && alt && Down(VK_RMENU);

            // Ctrl+number picks, Ctrl+Shift+number reads that word out, Ctrl+0 undoes.
            if (popupUp && ctrl && !alt && !win && vk >= '0' && vk <= '9')
            {
                int n = vk - '0';
                List<Suggestion> shown = choices ?? LiveList();
                if (shift)
                {
                    if (shown != null && n >= 1 && n <= shown.Count) { string w = shown[n - 1].Word; app.Post(delegate { app.SayNow(w); }); return true; }
                }
                else if (PickRow(n == 0 ? -1 : n - 1)) return true;
            }

            if (ctrl && !alt && !win && vk == VK_SPACE && Hotkey == Prefs.Hotkeys[1] && FixLastWord()) return true;

            // Anything else typed means the last fix is settled.
            if (choices != null) CloseAll();

            if ((ctrl || alt || win) && !altGr) { recent.Length = 0; CloseAll(); return false; }

            if (vk == VK_BACK)
            {
                if (recent.Length > 0) recent.Length--;
                UpdateLive();
                return false;
            }
            if (vk == VK_RETURN) return TryFix("", VK_RETURN);
            if (vk == VK_TAB) return TryFix("", VK_TAB);
            if (vk == VK_SPACE) return TryFix(" ", VK_SPACE);

            char c = Translate(vk, scan, fg);
            if (c == '\0') { recent.Length = 0; CloseAll(); return false; } // arrows, Home, Esc, dead keys...

            if (char.IsLetter(c) || c == '\'' || c == '@')
            {
                Remember(c.ToString());
                UpdateLive();
                return false;
            }
            if (char.IsControl(c)) { recent.Length = 0; CloseAll(); return false; }
            return TryFix(c.ToString(), 0);
        }

        void Remember(string s)
        {
            recent.Append(s);
            if (recent.Length > 200) recent.Remove(0, recent.Length - 200);
        }

        string PreviousWord(int end)
        {
            Match m = WordBefore.Match(recent.ToString(0, Math.Max(0, Math.Min(end, recent.Length))));
            return m.Success ? m.Groups[1].Value : null;
        }

        // The live list, if it is for exactly what is typed after @@ right now.
        List<Suggestion> LiveList()
        {
            Match live = Trigger.Match(recent.ToString());
            string typed = live.Success ? live.Groups[1].Value.TrimEnd('\'') : null;
            return typed != null && typed == liveFoundFor ? liveFound : null;
        }

        // Row r (0-based) of the popup was picked by Ctrl+number or a click; -1 means
        // Ctrl+0, put back what was typed. Returns false if there was nothing to pick.
        public bool PickRow(int r)
        {
            if (choices != null)
            {
                if (r == -1) { Pick(original, true); return true; }
                if (r >= 0 && r < choices.Count) { Pick(choices[r].Word, false); return true; }
                return false;
            }
            Match live = Trigger.Match(recent.ToString());
            List<Suggestion> list = LiveList();
            if (list == null || r < 0 || r >= list.Count) return false;
            // Picked while still typing @@word: that is the final choice.
            string word = list[r].Word;
            string typed = live.Groups[1].Value.TrimEnd('\'');
            ReplaceAt(live.Index, word, "", 0);
            app.Learn(typed, word);
            CloseAll();
            app.Post(delegate { app.Say(word); });
            return true;
        }

        public void HearRow(int r)
        {
            List<Suggestion> list = choices ?? LiveList();
            if (list != null && r >= 0 && r < list.Count) app.SayNow(list[r].Word);
        }

        // While @@word is being typed, look it up in the background and show the matches.
        void UpdateLive()
        {
            Match m = Trigger.Match(recent.ToString());
            string word = m.Success ? m.Groups[1].Value.TrimEnd('\'') : "";
            if (word.Length < 2)
            {
                liveWord = null; liveFound = null; liveFoundFor = null;
                if (popupUp && choices == null) CloseAll();
                return;
            }
            liveWord = word;
            lock (lookupLock) { lookupWanted = word; lookupPrevious = PreviousWord(m.Index); }
            lookupWake.Set();
        }

        void LookupLoop()
        {
            while (true)
            {
                lookupWake.WaitOne();
                string word, previous;
                lock (lookupLock) { word = lookupWanted; previous = lookupPrevious; lookupWanted = null; }
                Speller sp = app.Speller;
                if (word == null || sp == null) continue;
                List<Suggestion> found = sp.SuggestFull(word, 5, previous);
                app.Post(delegate
                {
                    if (word != liveWord || choices != null || found.Count == 0) return; // typing moved on
                    liveFound = found;
                    liveFoundFor = word;
                    ShowPopup(found, 0, AutoReplace
                        ? "Space: use 1     Ctrl+number or click: pick     point: hear"
                        : "Ctrl+number or click: put that word in     point: hear");
                });
            }
        }

        // The word just ended with `term`. If it was @@word, swap it.
        // Enter and Tab are held back, so fixing a word never sends a message
        // or leaves the box; the next Enter does that as usual.
        bool TryFix(string term, int termVk)
        {
            Match m = Trigger.Match(recent.ToString());
            string word = m.Success ? m.Groups[1].Value.TrimEnd('\'') : "";
            List<Suggestion> found = word.Length > 0 && word == liveFoundFor ? liveFound : null;
            liveWord = null; liveFound = null; liveFoundFor = null;
            Speller sp = app.Speller;
            if (word.Length > 0 && found == null && sp != null) found = sp.SuggestFull(word, 5, PreviousWord(m.Index));
            if (word.Length == 0 || found == null || found.Count == 0)
            {
                if (termVk == VK_RETURN || termVk == VK_TAB) recent.Length = 0; else Remember(term);
                CloseAll();
                return false;
            }

            CloseAll();
            original = word;
            if (AutoReplace)
            {
                ReplaceAt(m.Index, found[0].Word, term, termVk);
                choices = found;
                ShowPopup(found, 1, (termVk == VK_RETURN ? "Enter: send     " : "") +
                    "Ctrl+number or click: pick another     Ctrl+0: keep \"" + word + "\"");
                string said = found[0].Word;
                app.Post(delegate { app.Say(said); });
            }
            else
            {
                // Only suggesting: leave the text, put the key back unless it was Enter/Tab.
                if (term.Length > 0)
                {
                    var keys = new List<Native.INPUT>();
                    if (termVk != 0) AddVk(keys, termVk); else AddText(keys, term);
                    SendLater(keys);
                    Remember(term);
                }
                inserted = m.Value;
                termText = term;
                choices = found;
                ShowPopup(found, 0, "Ctrl+number or click: put that word in     point: hear");
            }
            return true;
        }

        // Fix the word just before the cursor (the shortcut). Returns false if there
        // is no word there that we know about.
        bool FixLastWord()
        {
            Speller sp = app.Speller;
            if (sp == null) return false;
            string text = recent.ToString();
            Match m = LastWord.Match(text);
            if (!m.Success) return false;
            string word = m.Groups[1].Value.Trim('\'');
            string after = m.Groups[2].Value;
            if (word.Length < 2) return false;
            List<Suggestion> found = sp.SuggestFull(word, 5, PreviousWord(m.Index));
            if (found.Count == 0) return false;

            CloseAll();
            original = word;
            if (found[0].Word != word) ReplaceAt(m.Index, found[0].Word, after, 0);
            else { inserted = word; termText = after; }
            choices = found;
            ShowPopup(found, 1, "Ctrl+number or click: pick another     Ctrl+0: keep \"" + word + "\"");
            string said = found[0].Word;
            app.Post(delegate { app.Say(said); });
            return true;
        }

        // Swap everything typed from `start` to the cursor for `word`, then `term`.
        void ReplaceAt(int start, string word, string term, int termVk)
        {
            var keys = new List<Native.INPUT>();
            for (int i = start; i < recent.Length; i++) AddVk(keys, VK_BACK);
            AddText(keys, word);
            if (term == " ") AddVk(keys, VK_SPACE); else AddText(keys, term);
            SendLater(keys);
            recent.Length = Math.Min(start, recent.Length);
            Remember(word + term);
            inserted = word;
            termText = term;
        }

        void Pick(string word, bool keep)
        {
            var keys = new List<Native.INPUT>();
            int erase = inserted.Length + termText.Length;
            for (int i = 0; i < erase; i++) AddVk(keys, VK_BACK);
            AddText(keys, word);
            AddText(keys, termText);
            SendLater(keys);
            recent.Length = Math.Max(0, recent.Length - erase);
            Remember(word + termText);
            app.Learn(original, keep ? original : word);
            inserted = word;
            CloseAll();
            app.Post(delegate { app.Say(word); });
        }

        void CloseAll()
        {
            choices = null;
            liveWord = null; liveFound = null; liveFoundFor = null;
            if (!popupUp) return;
            popupUp = false;
            app.Post(delegate { if (popup != null) popup.HideNow(); });
        }

        void ShowPopup(List<Suggestion> found, int current, string footer)
        {
            popupUp = true;
            Point at = CaretPoint();
            var list = new List<Suggestion>(found);
            app.Post(delegate
            {
                if (!popupUp) return;
                if (popup == null || popup.IsDisposed)
                {
                    popup = new Popup();
                    popup.TimedOut += delegate { CloseAll(); };
                    popup.RowClicked += delegate (int r) { PickRow(r); };
                    popup.RowPointed += delegate (int r) { if (app.HearOnPoint) HearRow(r); };
                }
                popup.ShowChoices(list, current, footer, at);
            });
        }

        static Point CaretPoint()
        {
            IntPtr fg = Native.GetForegroundWindow();
            uint tid = Native.GetWindowThreadProcessId(fg, IntPtr.Zero);
            var gti = new Native.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            if (Native.GetGUIThreadInfo(tid, ref gti) && gti.hwndCaret != IntPtr.Zero)
            {
                var p = new Native.POINT { x = gti.rcCaret.left, y = gti.rcCaret.bottom };
                Native.ClientToScreen(gti.hwndCaret, ref p);
                return new Point(p.x, p.y + 4);
            }
            Point c = Cursor.Position;
            return new Point(c.X + 12, c.Y + 20);
        }

        char Translate(int vk, int scan, IntPtr fg)
        {
            var state = new byte[256];
            foreach (int m in new[] { VK_SHIFT, VK_LSHIFT, VK_RSHIFT, VK_CONTROL, VK_LCONTROL, VK_RCONTROL, VK_MENU, VK_LMENU, VK_RMENU })
                if (Down(m)) state[m] = 0x80;
            if ((Native.GetKeyState(VK_CAPITAL) & 1) != 0) state[VK_CAPITAL] = 0x01;

            IntPtr layout = Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(fg, IntPtr.Zero));
            var buf = new StringBuilder(8);
            // Flag 4: do not disturb the keyboard state, so dead keys (´ ¨ ^) still work for the app.
            int n = Native.ToUnicodeEx((uint)vk, (uint)scan, state, buf, buf.Capacity, 4, layout);
            return n == 1 ? buf[0] : '\0';
        }

        // Keys sent from inside the hook callback get lost, so typing goes out just
        // after it returns.
        void SendLater(List<Native.INPUT> keys)
        {
            busy = true;
            app.Post(delegate
            {
                try
                {
                    var up = ReleaseModifiers();
                    Send(keys);
                    RestoreModifiers(up);
                }
                finally
                {
                    busy = false;
                    ReplayQueued();
                }
            });
        }

        void ReplayQueued()
        {
            if (queued.Count == 0) return;
            var keys = new List<Native.INPUT>();
            foreach (var k in queued)
            {
                uint flags = 0;
                if ((k.flags & 0x01) != 0) flags |= Native.KEYEVENTF_EXTENDEDKEY;
                if ((k.flags & 0x80) != 0) flags |= Native.KEYEVENTF_KEYUP;
                var i = Key((ushort)k.vkCode, (ushort)k.scanCode, flags);
                i.u.ki.dwExtraInfo = IntPtr.Zero; // goes through the hook again like any key
                keys.Add(i);
            }
            queued.Clear();
            Send(keys);
        }

        // Held Ctrl/Alt/Win/Shift would turn our Backspaces into shortcuts; lift them while we type.
        List<int> ReleaseModifiers()
        {
            var up = new List<int>();
            foreach (int m in new[] { VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN, VK_LSHIFT, VK_RSHIFT })
                if (held[m]) up.Add(m);
            if (up.Count == 0) return up;
            var keys = new List<Native.INPUT>();
            foreach (int m in up) keys.Add(Key((ushort)m, 0, Native.KEYEVENTF_KEYUP));
            Send(keys);
            return up;
        }

        static void RestoreModifiers(List<int> up)
        {
            if (up.Count == 0) return;
            var keys = new List<Native.INPUT>();
            foreach (int m in up) if (m != VK_LMENU && m != VK_RMENU && m != VK_LWIN && m != VK_RWIN) keys.Add(Key((ushort)m, 0, 0));
            if (keys.Count > 0) Send(keys);
        }

        static void AddVk(List<Native.INPUT> keys, int vk)
        {
            keys.Add(Key((ushort)vk, 0, 0));
            keys.Add(Key((ushort)vk, 0, Native.KEYEVENTF_KEYUP));
        }

        static void AddText(List<Native.INPUT> keys, string text)
        {
            foreach (char ch in text)
            {
                keys.Add(Key(0, ch, Native.KEYEVENTF_UNICODE));
                keys.Add(Key(0, ch, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP));
            }
        }

        static Native.INPUT Key(ushort vk, ushort scan, uint flags)
        {
            var i = new Native.INPUT();
            i.type = Native.INPUT_KEYBOARD;
            i.u.ki.wVk = vk;
            i.u.ki.wScan = scan;
            i.u.ki.dwFlags = flags;
            i.u.ki.dwExtraInfo = Marker;
            return i;
        }

        static void Send(List<Native.INPUT> keys)
        {
            if (keys.Count == 0) return;
            uint sent = Native.SendInput((uint)keys.Count, keys.ToArray(), Marshal.SizeOf(typeof(Native.INPUT)));
            if (Log.On)
            {
                var title = new StringBuilder(128);
                Native.GetWindowText(Native.GetForegroundWindow(), title, title.Capacity);
                Log.Write("SendInput " + keys.Count + " -> " + sent + " into [" + title + "]");
            }
        }
    }
}
