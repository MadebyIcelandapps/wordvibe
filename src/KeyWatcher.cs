// Watches keys system-wide. Fixes "@@word" when the word ends, fixes the word
// just typed when the shortcut is pressed (tap Shift twice by default), fixes her
// usual mistakes by themselves, keeps the sentence strip up to date, and reads
// text out when Ctrl is tapped twice.
//
// Written for C# 5 so the csc.exe that ships inside Windows can compile it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace SoundSpell
{
    sealed class KeyWatcher
    {
        const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11,
                  VK_MENU = 0x12, VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_LEFT = 0x25, VK_RIGHT = 0x27,
                  VK_LWIN = 0x5B, VK_RWIN = 0x5C,
                  VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
        static readonly IntPtr Marker = new IntPtr(0x5350454C); // tags the keys we send ourselves

        static readonly Regex Trigger = new Regex(@"@@([\p{L}']{1,40})$", RegexOptions.Compiled);
        // The last word on the line and what was typed after it (spaces, punctuation).
        static readonly Regex LastWord = new Regex(@"(?<![\p{L}'@])([\p{L}']{2,40})([ .,;:!?)""]{0,3})$", RegexOptions.Compiled);
        static readonly Regex EndWord = new Regex(@"(?<![\p{L}'@])([\p{L}']{2,40})$", RegexOptions.Compiled);
        static readonly Regex WordBefore = new Regex(@"([\p{L}']+)[^\p{L}']*$", RegexOptions.Compiled);
        static readonly Regex Words = new Regex(@"[\p{L}'’]+", RegexOptions.Compiled);

        readonly TrayApp app;
        // What was typed lately on this line, as it is on screen. Cleared by Enter,
        // arrows, clicks, shortcuts and switching window, because then we no longer
        // know what is next to the cursor.
        readonly StringBuilder recent = new StringBuilder();
        readonly Native.LowLevelKeyboardProc proc, mouseProc;
        IntPtr hook = IntPtr.Zero, mouseHook = IntPtr.Zero;
        IntPtr lastWindow = IntPtr.Zero;
        Popup popup;             // made and used on the app's window thread
        SentenceStrip strip;     // same
        // The hooks run on their own thread, so nothing on the window thread (voice,
        // windows, settings) can ever hold up a key. All the typing state below lives
        // on this thread; other threads hand work over with OnHook.
        Control hookThread;

        // The last fix, which Ctrl+digit or a click can change while the popup is up.
        List<Suggestion> choices;
        string original;      // what was typed, without @@
        string inserted;      // what is on screen now in its place
        int fixStart;         // where `inserted` starts in `recent`
        bool autoFixed;       // it was one of her usual mistakes, fixed by itself
        bool pendingAccept;   // a fix she has not changed yet; kept if she types on

        // Matches shown while @@word is still being typed.
        string liveWord;
        List<Suggestion> liveFound;
        string liveFoundFor;
        volatile bool popupUp;

        // Lookups run on a worker so typing never waits for them.
        readonly object lookupLock = new object();
        readonly AutoResetEvent lookupWake = new AutoResetEvent(false);
        string lookupWanted, lookupPrevious;

        // The strip is worked out on another worker (finding the caret can be slow).
        readonly object stripLock = new object();
        readonly AutoResetEvent stripWake = new AutoResetEvent(false);
        string stripText;
        int stripVersion;
        Rectangle lastCaret = Rectangle.Empty;

        // True while the text cursor is in a password field. Then SoundSpell does
        // nothing at all: no strip, no list, no fixes, nothing remembered or logged.
        volatile bool passwordFocus;

        // Shortcut taps: a key pressed and let go with nothing else in between.
        bool shiftClean, rctrlClean, lctrlClean;
        DateTime lastShiftTap = DateTime.MinValue, lastCtrlTap = DateTime.MinValue;

        public bool Enabled = true;
        public bool AutoReplace = true;
        public bool AutoFix = true;
        public bool StripOn = true;
        public bool ReadOnCtrl = true;
        public string Hotkey = Prefs.Hotkeys[0];

        public KeyWatcher(TrayApp app)
        {
            this.app = app;
            proc = HookProc;
            mouseProc = MouseProc;
        }

        public void Start()
        {
            var ready = new ManualResetEvent(false);
            var t = new Thread(delegate ()
            {
                hookThread = new Control();
                hookThread.CreateControl();
                var h = hookThread.Handle;
                hook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
                mouseHook = Native.SetWindowsHookEx(WH_MOUSE_LL, mouseProc, Native.GetModuleHandle(null), 0);
                ready.Set();
                Application.Run(); // the message loop the hooks are called from
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Name = "keys";
            t.Start();
            ready.WaitOne();
            WatchFocus();
            foreach (ThreadStart work in new ThreadStart[] { LookupLoop, StripLoop })
            {
                var w = new Thread(work);
                w.IsBackground = true;
                w.Start();
            }
        }

        public void Stop()
        {
            if (hookThread == null) return;
            try
            {
                hookThread.Invoke((Action)delegate
                {
                    if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
                    if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
                    hook = mouseHook = IntPtr.Zero;
                    Application.ExitThread();
                });
            }
            catch (Exception) { }
        }

        // Browsers and newer apps say through UI Automation when a password field gets
        // the focus; classic Windows password boxes are checked on every key (below).
        void WatchFocus()
        {
            var t = new Thread(delegate ()
            {
                try
                {
                    Automation.AddAutomationFocusChangedEventHandler(delegate (object sender, AutomationFocusChangedEventArgs e)
                    {
                        bool pw = false;
                        try { var el = sender as AutomationElement; pw = el != null && el.Current.IsPassword; }
                        catch (Exception) { }
                        passwordFocus = pw;
                        if (pw) OnHook(ForgetForPassword);
                    });
                }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        void ForgetForPassword()
        {
            recent.Length = 0;
            liveWord = null;
            CloseAll();
            HideStrip();
        }

        // A classic Windows password box (an Edit control with the password style).
        static bool InClassicPasswordBox()
        {
            IntPtr fg = Native.GetForegroundWindow();
            uint tid = Native.GetWindowThreadProcessId(fg, IntPtr.Zero);
            var gti = new Native.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            if (!Native.GetGUIThreadInfo(tid, ref gti) || gti.hwndFocus == IntPtr.Zero) return false;
            var cls = new StringBuilder(64);
            Native.GetClassName(gti.hwndFocus, cls, cls.Capacity);
            if (cls.ToString().IndexOf("Edit", StringComparison.OrdinalIgnoreCase) < 0) return false;
            return (Native.GetWindowLong(gti.hwndFocus, -16).ToInt64() & 0x20) != 0; // GWL_STYLE, ES_PASSWORD
        }

        public bool InPassword { get { return passwordFocus || InClassicPasswordBox(); } }

        // Runs `a` on the hook thread, after the current hook call (if any) returns.
        void OnHook(Action a)
        {
            try { if (hookThread != null && !hookThread.IsDisposed) hookThread.BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        // A click in the window being typed in can move the text cursor, so whatever
        // was being typed no longer counts. Clicks on the list or the strip are ours.
        // A click in some other window (a screenshot tool, say) leaves the strip up a
        // moment longer. Which window was clicked is asked of Windows, rather than
        // compared by position, so scaled screens cannot get it wrong.
        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
                {
                    var pt = (Native.POINT)Marshal.PtrToStructure(lParam, typeof(Native.POINT));
                    IntPtr under = Native.GetAncestor(Native.WindowFromPoint(pt), 2); // GA_ROOT
                    IntPtr stripH = strip != null ? strip.Hwnd : IntPtr.Zero;
                    IntPtr popupH = popup != null ? popup.Hwnd : IntPtr.Zero;
                    bool ours = under != IntPtr.Zero && (under == stripH || under == popupH);
                    if (Log.On) Log.Write("click " + pt.x + "," + pt.y + (ours ? " on ours" : under == lastWindow ? " in the typing window" : " elsewhere"));
                    if (ours) { }
                    else if (under == lastWindow)
                    {
                        recent.Length = 0;
                        CloseAll();
                        HideStrip();
                    }
                    else
                    {
                        CloseAll();
                        app.Post(delegate { if (strip != null) strip.HideAfter(2500); });
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

        // Taps: the key goes down and up with no other key in between, so Shift for a
        // capital or Ctrl for Ctrl+C never counts.
        void WatchTaps(int vk, bool isDown, bool wasDown)
        {
            if (!isDown && passwordFocus) return;
            bool shift = vk == VK_LSHIFT || vk == VK_RSHIFT || vk == VK_SHIFT;
            if (isDown)
            {
                if (wasDown) return; // held down, repeating
                shiftClean = shift; rctrlClean = vk == VK_RCONTROL; lctrlClean = vk == VK_LCONTROL;
                if (!shift) lastShiftTap = DateTime.MinValue;
                if (vk != VK_LCONTROL) lastCtrlTap = DateTime.MinValue;
                return;
            }
            DateTime now = DateTime.UtcNow;
            if (shift && shiftClean)
            {
                shiftClean = false;
                if (Hotkey != Prefs.Hotkeys[0]) return;
                if ((now - lastShiftTap).TotalMilliseconds < 450) { lastShiftTap = DateTime.MinValue; FixLastWord(); }
                else lastShiftTap = now;
            }
            else if (vk == VK_RCONTROL && rctrlClean)
            {
                rctrlClean = false;
                if (Hotkey == Prefs.Hotkeys[2]) FixLastWord();
            }
            else if (vk == VK_LCONTROL && lctrlClean)
            {
                lctrlClean = false;
                if (!ReadOnCtrl) return;
                if ((now - lastCtrlTap).TotalMilliseconds < 450) { lastCtrlTap = DateTime.MinValue; string s = CurrentSentence(); app.Post(delegate { app.ReadAloud(s); }); }
                else lastCtrlTap = now;
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
            if (IsModifier(vk)) return false;
            if (InPassword)
            {
                if (recent.Length > 0 || popupUp) ForgetForPassword();
                if (Log.On) Log.Write("key in a password field: ignored");
                return false;
            }
            if (Log.On) Log.Write("key " + vk.ToString("X2") + " ctrl=" + Down(VK_CONTROL) + " popup=" + popupUp + " choices=" + (choices != null) + " recent=[" + recent + "]");
            // Screenshot keys (Print Screen, Win+Shift+S) leave everything as it is,
            // so the strip and the list can be in the picture.
            if (vk == 0x2C || Down(VK_LWIN) || Down(VK_RWIN)) return false;

            IntPtr fg = Native.GetForegroundWindow();
            if (fg != lastWindow) { lastWindow = fg; recent.Length = 0; CloseAll(); HideStrip(); }

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

            if ((ctrl || alt || win) && !altGr) { recent.Length = 0; CloseAll(); HideStrip(); return false; }
            if (vk == VK_ESCAPE) { CloseAll(); HideStrip(); return false; }

            if (vk == VK_BACK)
            {
                if (recent.Length > 0) recent.Length--;
                UpdateLive();
                StripChanged();
                return false;
            }
            if (vk == VK_RETURN) return TryFix("", VK_RETURN);
            if (vk == VK_TAB) return TryFix("", VK_TAB);
            if (vk == VK_SPACE) return TryFix(" ", VK_SPACE);

            char c = Translate(vk, scan, fg);
            if (c == '\0') { recent.Length = 0; CloseAll(); HideStrip(); return false; } // arrows, Home, dead keys...

            if (char.IsLetter(c) || c == '\'' || c == '@')
            {
                Remember(c.ToString());
                UpdateLive();
                StripChanged();
                return false;
            }
            if (char.IsControl(c)) { recent.Length = 0; CloseAll(); HideStrip(); return false; }
            return TryFix(c.ToString(), 0);
        }

        void Remember(string s)
        {
            recent.Append(s);
            if (recent.Length > 300) recent.Remove(0, recent.Length - 300);
        }

        string PreviousWord(int end)
        {
            Match m = WordBefore.Match(recent.ToString(0, Math.Max(0, Math.Min(end, recent.Length))));
            return m.Success ? m.Groups[1].Value : null;
        }

        // The sentence being typed: everything after the last . ! or ? on the line.
        static int SentenceStart(string text)
        {
            for (int i = text.Length - 2; i >= 0; i--)
                if ((text[i] == '.' || text[i] == '!' || text[i] == '?') && char.IsWhiteSpace(text[i + 1])) return i + 1;
            return 0;
        }

        string CurrentSentence()
        {
            string text = recent.ToString();
            return text.Substring(SentenceStart(text)).Replace("@@", "").Trim();
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
                if (r == -1)
                {
                    if (autoFixed) app.RemoveAutoFix(original); // she did not want this one fixed
                    Pick(original, true);
                    return true;
                }
                if (r < 0 || r >= choices.Count) return false;
                if (choices[r].AddToMyWords) { app.AddMyWord(choices[r].Word); pendingAccept = false; CloseAll(); StripChanged(); return true; }
                Pick(choices[r].Word, false);
                return true;
            }
            Match live = Trigger.Match(recent.ToString());
            List<Suggestion> list = LiveList();
            if (list == null || r < 0 || r >= list.Count) return false;
            // Picked while still typing @@word: that is the final choice.
            string word = list[r].Word;
            string typed = live.Groups[1].Value.TrimEnd('\'');
            Replace(live.Index, live.Length, word, "", 0);
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
                OnHook(delegate
                {
                    if (word != liveWord || choices != null || found.Count == 0) return; // typing moved on
                    liveFound = found;
                    liveFoundFor = word;
                    ShowPopup(found, 0, AutoReplace
                        ? "Space: use 1     Ctrl+number or click: pick     point: hear"
                        : "Ctrl+number or click: put that word in     point: hear", null);
                });
            }
        }

        // A word just ended with `term` (a space, punctuation, Enter or Tab).
        // @@word is swapped; one of her usual mistakes is fixed by itself.
        // Enter and Tab after @@word are held back, so fixing a word never sends a
        // message or leaves the box; the next Enter does that as usual.
        bool TryFix(string term, int termVk)
        {
            Match m = Trigger.Match(recent.ToString());
            string word = m.Success ? m.Groups[1].Value.TrimEnd('\'') : "";
            List<Suggestion> found = word.Length > 0 && word == liveFoundFor ? liveFound : null;
            liveWord = null; liveFound = null; liveFoundFor = null;
            Speller sp = app.Speller;

            if (word.Length == 0)
            {
                if (AutoFix && sp != null && TryAutoFix(term, termVk)) return true;
                CloseAll();
                if (termVk == VK_RETURN || termVk == VK_TAB) { recent.Length = 0; HideStrip(); }
                else { Remember(term); StripChanged(); }
                return false;
            }
            if (found == null && sp != null) found = sp.SuggestFull(word, 5, PreviousWord(m.Index));
            if (found == null || found.Count == 0)
            {
                CloseAll();
                if (termVk == VK_RETURN || termVk == VK_TAB) recent.Length = 0; else Remember(term);
                return false;
            }

            CloseAll();
            original = word;
            fixStart = m.Index;
            if (AutoReplace)
            {
                Replace(m.Index, m.Length, found[0].Word, term, 0);
                choices = found;
                pendingAccept = true;
                ShowPopup(found, 1, (termVk == VK_RETURN ? "Enter: send     " : "") +
                    "Ctrl+number or click: pick another     Ctrl+0: keep \"" + word + "\"", null);
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
                choices = found;
                ShowPopup(found, 0, "Ctrl+number or click: put that word in     point: hear", null);
            }
            StripChanged();
            return true;
        }

        // One of her usual mistakes (see TrayApp.Learn): fix it as the word ends.
        bool TryAutoFix(string term, int termVk)
        {
            Match m = EndWord.Match(recent.ToString());
            if (!m.Success) return false;
            string word = m.Groups[1].Value.Trim('\'');
            string fix = app.AutoFixFor(word);
            if (fix == null) return false;
            fix = Speller.MatchCase(word, fix);
            if (fix == word) return false;

            CloseAll();
            original = word;
            fixStart = m.Index;
            bool enter = termVk == VK_RETURN || termVk == VK_TAB;
            Replace(m.Index, m.Length, fix, term, enter ? termVk : 0);
            if (enter) { recent.Length = 0; HideStrip(); return true; } // sent: nothing left to change
            autoFixed = true;
            pendingAccept = true;
            var list = new List<Suggestion> { new Suggestion(fix, "fixed by itself") };
            foreach (Suggestion s in app.Speller.SuggestFull(word, 5, PreviousWord(m.Index)))
                if (!string.Equals(s.Word, fix, StringComparison.OrdinalIgnoreCase)) list.Add(s);
            choices = list;
            ShowPopup(list, 1, "Fixed by itself     Ctrl+number or click: pick another     Ctrl+0: keep \"" + word + "\"", null);
            app.Post(delegate { app.Say(fix); });
            StripChanged();
            return true;
        }

        // The shortcut: fix the word just before the cursor, or, if that one is fine,
        // the nearest red word earlier in the sentence.
        bool FixLastWord()
        {
            Speller sp = app.Speller;
            if (sp == null) return false;
            string text = recent.ToString();
            Match m = LastWord.Match(text);
            if (m.Success && !sp.IsWord(m.Groups[1].Value) || !m.Success)
            {
                if (!m.Success) return FixRedWordBefore(text.Length);
                return FixAt(m.Groups[1].Index, m.Groups[1].Value.Trim('\''));
            }
            if (StripOn && FixRedWordBefore(m.Index)) return true;
            return FixAt(m.Groups[1].Index, m.Groups[1].Value.Trim('\''));
        }

        bool FixRedWordBefore(int end)
        {
            Speller sp = app.Speller;
            string text = recent.ToString();
            int start = SentenceStart(text);
            StripWord red = null;
            foreach (StripWord w in SentenceWords(text, sp))
                if (w.State == WordState.Bad && w.Start + w.Text.Length <= end && w.Start >= start) red = w;
            return red != null && FixAt(red.Start, red.Text);
        }

        // Replace the word at `start` in the line with its best match, and show the others.
        bool FixAt(int start, string word, Point? listAt = null)
        {
            Speller sp = app.Speller;
            if (sp == null || word.Length < 2) return false;
            List<Suggestion> found = sp.SuggestFull(word, 5, PreviousWord(start));
            if (found.Count == 0) return false;
            if (!sp.IsWord(word))
                found.Add(new Suggestion(word, "add to My words") { AddToMyWords = true });

            CloseAll();
            original = word;
            fixStart = start;
            if (found[0].Word != word) Replace(start, word.Length, found[0].Word, "", 0);
            else inserted = word;
            choices = found;
            pendingAccept = true;
            ShowPopup(found, 1, "Ctrl+number or click: pick another     Ctrl+0: keep \"" + word + "\"", listAt);
            string said = found[0].Word;
            app.Post(delegate { app.Say(said); });
            StripChanged();
            return true;
        }

        // A word on the strip was clicked: fix that one.
        void OnStripWord(StripWord w, Point below)
        {
            string text = recent.ToString();
            if (w.Start + w.Text.Length > text.Length || text.Substring(w.Start, w.Text.Length) != w.Text) return; // line changed
            FixAt(w.Start, w.Text.Trim('\''), below);
        }

        // Puts `word` on screen in place of recent[start .. start+len], then types
        // `after` (and presses `vkAfter`) at the end of the line. When only a space or
        // punctuation follows the word, it is rubbed out and typed again; when more
        // follows, the cursor steps back to the word with the arrow keys and returns.
        void Replace(int start, int len, string word, string after, int vkAfter)
        {
            start = Math.Max(0, Math.Min(start, recent.Length));
            len = Math.Min(len, recent.Length - start);
            string tail = recent.ToString(start + len, recent.Length - start - len);
            var keys = new List<Native.INPUT>();
            bool shortTail = tail.Length <= 3 && !Regex.IsMatch(tail, @"[\p{L}\d]");
            if (shortTail)
            {
                for (int i = 0; i < len + tail.Length; i++) AddVk(keys, VK_BACK);
                AddText(keys, word);
                TypeText(keys, tail);
            }
            else
            {
                for (int i = 0; i < tail.Length; i++) AddVk(keys, VK_LEFT, true);
                for (int i = 0; i < len; i++) AddVk(keys, VK_BACK);
                AddText(keys, word);
                for (int i = 0; i < tail.Length; i++) AddVk(keys, VK_RIGHT, true);
            }
            TypeText(keys, after);
            if (vkAfter != 0) AddVk(keys, vkAfter);
            SendLater(keys);
            recent.Remove(start, len);
            recent.Insert(start, word);
            Remember(after);
            inserted = word;
            fixStart = start;
        }

        static void TypeText(List<Native.INPUT> keys, string text)
        {
            foreach (char ch in text) { if (ch == ' ') AddVk(keys, VK_SPACE); else AddText(keys, ch.ToString()); }
        }

        void Pick(string word, bool keep)
        {
            pendingAccept = false;
            if (inserted != null && fixStart + inserted.Length <= recent.Length && recent.ToString(fixStart, inserted.Length) == inserted)
                Replace(fixStart, inserted.Length, word, "", 0);
            app.Learn(original, keep ? original : word);
            CloseAll();
            StripChanged();
            app.Post(delegate { app.Say(word); });
        }

        void CloseAll()
        {
            // A fix she did not change counts as a pick of that word.
            if (pendingAccept && original != null && inserted != null) app.Learn(original, inserted);
            pendingAccept = false;
            autoFixed = false;
            choices = null;
            liveWord = null; liveFound = null; liveFoundFor = null;
            if (!popupUp) return;
            popupUp = false;
            app.Post(delegate { if (popup != null) popup.HideNow(); });
        }

        void ShowPopup(List<Suggestion> found, int current, string footer, Point? at)
        {
            popupUp = true;
            Point where = at ?? CaretPoint();
            var list = new List<Suggestion>(found);
            app.Post(delegate
            {
                if (!popupUp) return;
                if (popup == null || popup.IsDisposed)
                {
                    popup = new Popup();
                    popup.TimedOut += delegate { OnHook(CloseAll); };
                    popup.RowClicked += delegate (int r) { OnHook(delegate { PickRow(r); }); };
                    popup.RowPointed += delegate (int r) { if (app.HearOnPoint) OnHook(delegate { HearRow(r); }); };
                }
                popup.ShowChoices(list, current, footer, where);
            });
        }

        // Just under the caret. Uses the quick Win32 answer, or where the strip worker
        // last found the caret, or the mouse.
        Point CaretPoint()
        {
            bool exact;
            Rectangle r = QuickCaret(out exact);
            if (!r.IsEmpty) return new Point(r.Left, r.Bottom + 4);
            Point c = Cursor.Position;
            return new Point(c.X + 12, c.Y + 20);
        }

        Rectangle QuickCaret(out bool exact)
        {
            exact = true;
            IntPtr fg = Native.GetForegroundWindow();
            uint tid = Native.GetWindowThreadProcessId(fg, IntPtr.Zero);
            var gti = new Native.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            if (Native.GetGUIThreadInfo(tid, ref gti) && gti.hwndCaret != IntPtr.Zero)
            {
                var p = new Native.POINT { x = gti.rcCaret.left, y = gti.rcCaret.top };
                Native.ClientToScreen(gti.hwndCaret, ref p);
                return new Rectangle(p.x, p.y, 1, Math.Max(8, gti.rcCaret.bottom - gti.rcCaret.top));
            }
            lock (stripLock) return lastCaret;
        }

        // ---- the sentence strip ------------------------------------------------------

        void StripChanged()
        {
            if (!StripOn) return;
            lock (stripLock) { stripText = recent.ToString(); stripVersion++; }
            stripWake.Set();
        }

        void HideStrip()
        {
            lock (stripLock) { stripText = null; stripVersion++; }
            app.Post(delegate { if (strip != null) strip.HideNow(); });
        }

        // The words of the sentence being typed, each marked right, wrong, a name, or
        // still being typed.
        static List<StripWord> SentenceWords(string text, Speller sp)
        {
            var list = new List<StripWord>();
            int from = SentenceStart(text);
            bool first = true;
            foreach (Match m in Words.Matches(text, from))
            {
                if (m.Index >= 2 && text[m.Index - 1] == '@' && text[m.Index - 2] == '@') continue; // @@word has its own list
                string w = m.Value.Trim('\'', '’');
                if (w.Length == 0) continue;
                var sw = new StripWord { Text = m.Value, Start = m.Index };
                if (m.Index + m.Length == text.Length) sw.State = WordState.Typing;
                else if (sp == null || sp.IsWord(w) || w.Length == 1) sw.State = WordState.Good;
                else if (!first && char.IsUpper(w[0])) sw.State = WordState.Name;
                else sw.State = WordState.Bad;
                list.Add(sw);
                first = false;
            }
            return list;
        }

        void StripLoop()
        {
            while (true)
            {
                stripWake.WaitOne();
                Thread.Sleep(40); // let a burst of keys settle
                string text; int version;
                lock (stripLock) { text = stripText; version = stripVersion; }
                if (text == null || passwordFocus) continue;
                List<StripWord> words = SentenceWords(text, app.Speller);
                bool exact;
                Rectangle caret = Caret.Find(out exact);
                if (caret.IsEmpty)
                {
                    Native.RECT wr;
                    if (Native.GetWindowRect(Native.GetForegroundWindow(), out wr))
                        caret = new Rectangle(wr.left + 40, wr.bottom - 70, 1, 20);
                    exact = false;
                }
                lock (stripLock)
                {
                    if (exact) lastCaret = caret;
                    if (version != stripVersion) continue; // more typing already
                }
                app.Post(delegate
                {
                    lock (stripLock) { if (version != stripVersion) return; }
                    if (strip == null || strip.IsDisposed)
                    {
                        strip = new SentenceStrip();
                        strip.WordClicked += delegate (StripWord w)
                        {
                            Point below = strip.Below(w);
                            OnHook(delegate { OnStripWord(w, below); });
                        };
                        strip.SpeakerClicked += delegate
                        {
                            OnHook(delegate { string s = CurrentSentence(); app.Post(delegate { app.ReadAloud(s, true); }); });
                        };
                    }
                    bool onlyRed = Prefs.GetText("StripShow", SentenceStrip.Shows[0]) == SentenceStrip.Shows[1];
                    bool anyRed = false;
                    foreach (StripWord sw in words) if (sw.State == WordState.Bad) anyRed = true;
                    if (words.Count == 0 || (onlyRed && !anyRed)) { strip.HideNow(); return; }
                    strip.ShowWords(words, caret, exact);
                });
            }
        }

        // ---- keys ------------------------------------------------------------------------

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
            OnHook(delegate
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

        // Copies the selection with Ctrl+C (for reading it out).
        public void SendCopy()
        {
            var keys = new List<Native.INPUT>();
            keys.Add(Key((ushort)VK_LCONTROL, 0, 0));
            keys.Add(Key((ushort)'C', 0, 0));
            keys.Add(Key((ushort)'C', 0, Native.KEYEVENTF_KEYUP));
            keys.Add(Key((ushort)VK_LCONTROL, 0, Native.KEYEVENTF_KEYUP));
            OnHook(delegate { SendLater(keys); });
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

        static void AddVk(List<Native.INPUT> keys, int vk) { AddVk(keys, vk, false); }

        static void AddVk(List<Native.INPUT> keys, int vk, bool extended)
        {
            uint ext = extended ? Native.KEYEVENTF_EXTENDEDKEY : 0;
            keys.Add(Key((ushort)vk, 0, ext));
            keys.Add(Key((ushort)vk, 0, ext | Native.KEYEVENTF_KEYUP));
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
