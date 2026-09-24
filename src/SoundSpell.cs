// SoundSpell: a tray app. Type @@ and a word spelled the way it sounds, then a
// space (or punctuation, Enter, Tab), and the word is swapped for the real
// spelling in whatever app you are typing in. A small popup lists other
// matches while you type: Ctrl+1..5 picks one, Ctrl+0 puts back what you typed.
// Enter after @@word only fixes the word; the next Enter goes through as usual.
//
// Written for C# 5 so the csc.exe that ships inside Windows can compile it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Speech.Synthesis;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SoundSpell
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool first;
            using (var only = new Mutex(true, @"Local\SoundSpell.Tray", out first))
            {
                if (!first) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }

    static class Settings
    {
        const string Key = @"Software\SoundSpell";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool Get(string name, bool fallback)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                object v = k == null ? null : k.GetValue(name);
                return v == null ? fallback : Convert.ToInt32(v) != 0;
            }
        }

        public static void Set(string name, bool value)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }

        public static bool StartsWithWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("SoundSpell") != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("SoundSpell", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("SoundSpell", false);
                }
            }
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly KeyWatcher watcher;
        readonly Control ui;
        TryForm tryForm;
        volatile Speller speller;
        ToolStripMenuItem enabledItem, autoItem, startupItem;

        public TrayApp()
        {
            ui = new Control();
            ui.CreateControl();
            var h = ui.Handle; // the handle lets background threads post back to this one

            tray = new NotifyIcon();
            tray.Icon = Art.MakeIcon(Settings.Get("Enabled", true));
            tray.Text = "SoundSpell - type @@word";
            tray.ContextMenuStrip = BuildMenu();
            tray.DoubleClick += delegate { ShowTry(); };
            tray.Visible = true;

            watcher = new KeyWatcher(this);
            watcher.Enabled = Settings.Get("Enabled", true);
            watcher.AutoReplace = Settings.Get("AutoReplace", true);
            watcher.Start();

            // Load the word list off the UI thread so the tray icon appears at once.
            Reload();
            WatchMyWords();

            if (!Settings.Get("Welcomed", false))
            {
                Settings.Set("Welcomed", true);
                tray.ShowBalloonTip(8000, "SoundSpell is running",
                    "Type @@ and a word the way it sounds, then space. Example: @@nesesary becomes necessary.\n" +
                    "It lives in the hidden icons by the clock.", ToolTipIcon.Info);
            }
        }

        public Speller Speller { get { return speller; } }

        public bool ReadAloud;
        SpeechSynthesizer voice;
        FileSystemWatcher myWordsWatcher;

        // Your own words (names, places, school words) go first in the list.
        public static string MyWordsPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundSpell", "my-words.txt"); }
        }

        void Reload()
        {
            var loader = new Thread(delegate ()
            {
                try { speller = LoadSpeller(); }
                catch (Exception e) { Post(delegate { tray.ShowBalloonTip(5000, "SoundSpell", "Could not load the word list: " + e.Message, ToolTipIcon.Error); }); }
            });
            loader.IsBackground = true;
            loader.Start();
        }

        void WatchMyWords()
        {
            string dir = Path.GetDirectoryName(MyWordsPath);
            Directory.CreateDirectory(dir);
            myWordsWatcher = new FileSystemWatcher(dir, "my-words.txt");
            myWordsWatcher.Changed += delegate { Reload(); };
            myWordsWatcher.Created += delegate { Reload(); };
            myWordsWatcher.EnableRaisingEvents = true;
        }

        void EditMyWords()
        {
            string path = MyWordsPath;
            if (!File.Exists(path))
                File.WriteAllText(path, "# Your own words, one per line: names, places, words from school.\r\n# They come first in the suggestions. Save the file and they work straight away.\r\n");
            System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\"");
        }

        public void Say(string word)
        {
            if (!ReadAloud) return;
            try
            {
                if (voice == null) { voice = new SpeechSynthesizer(); voice.Rate = -1; }
                voice.SpeakAsyncCancelAll();
                voice.SpeakAsync(word);
            }
            catch (Exception) { } // no voice installed
        }

        public void SayNow(string word)
        {
            bool was = ReadAloud; ReadAloud = true; Say(word); ReadAloud = was;
        }

        public void Post(Action a) { if (!ui.IsDisposed) ui.BeginInvoke(a); }

        static Speller LoadSpeller()
        {
            var all = new StringBuilder();
            if (File.Exists(MyWordsPath))
            {
                for (int tries = 0; ; tries++)
                {
                    try
                    {
                        foreach (string line in File.ReadAllLines(MyWordsPath))
                        {
                            string w = line.Trim();
                            if (w.Length > 0 && !w.StartsWith("#")) all.Append(w).Append('\n');
                        }
                        break;
                    }
                    catch (IOException) { if (tries > 5) break; Thread.Sleep(200); } // Notepad still saving
                }
            }
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoundSpell.words.txt");
            if (s == null) s = File.OpenRead(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "words.txt"));
            using (var r = new StreamReader(s, Encoding.UTF8)) all.Append(r.ReadToEnd());
            return new Speller(new StringReader(all.ToString()));
        }

        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            var title = new ToolStripMenuItem("SoundSpell: type @@word then space");
            title.Enabled = false;
            m.Items.Add(title);
            m.Items.Add(new ToolStripSeparator());

            enabledItem = new ToolStripMenuItem("On", null, delegate
            {
                watcher.Enabled = !watcher.Enabled;
                Settings.Set("Enabled", watcher.Enabled);
                enabledItem.Checked = watcher.Enabled;
                var old = tray.Icon;
                tray.Icon = Art.MakeIcon(watcher.Enabled);
                old.Dispose();
            });
            enabledItem.Checked = Settings.Get("Enabled", true);
            m.Items.Add(enabledItem);

            autoItem = new ToolStripMenuItem("Replace the word automatically", null, delegate
            {
                watcher.AutoReplace = !watcher.AutoReplace;
                Settings.Set("AutoReplace", watcher.AutoReplace);
                autoItem.Checked = watcher.AutoReplace;
            });
            autoItem.Checked = Settings.Get("AutoReplace", true);
            autoItem.ToolTipText = "Off: only show suggestions, and Ctrl+1..5 puts one in.";
            m.Items.Add(autoItem);

            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate
            {
                Settings.StartsWithWindows = !Settings.StartsWithWindows;
                startupItem.Checked = Settings.StartsWithWindows;
            });
            startupItem.Checked = Settings.StartsWithWindows;
            m.Items.Add(startupItem);

            ReadAloud = Settings.Get("ReadAloud", false);
            var readItem = new ToolStripMenuItem("Read the word out loud");
            readItem.Checked = ReadAloud;
            readItem.ToolTipText = "Hear the fixed word, so you can tell it is the right one.";
            readItem.Click += delegate
            {
                ReadAloud = !ReadAloud;
                Settings.Set("ReadAloud", ReadAloud);
                readItem.Checked = ReadAloud;
            };
            m.Items.Add(readItem);
            m.Items.Add(new ToolStripMenuItem("My words...", null, delegate { EditMyWords(); }));

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Try it...", null, delegate { ShowTry(); }));
            m.Items.Add(new ToolStripMenuItem("How to use", null, delegate { ShowHelp(); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Exit", null, delegate { ExitThread(); }));
            return m;
        }

        void ShowTry()
        {
            if (tryForm == null || tryForm.IsDisposed) tryForm = new TryForm(this);
            tryForm.Show();
            tryForm.Activate();
        }

        static void ShowHelp()
        {
            MessageBox.Show(
                "In any app, type @@ and then a word spelled the way it sounds, and then a space.\n\n" +
                "    @@nesesary  ->  necessary\n" +
                "    @@sykology  ->  psychology\n" +
                "    @@wensday  ->  Wednesday\n\n" +
                "Matches show up while you type:\n" +
                "    Space or punctuation puts in the first one\n" +
                "    Enter puts it in and waits; press Enter again to send\n" +
                "    Ctrl+1 to Ctrl+5 puts in that word instead\n" +
                "    Ctrl+0 puts back what you typed\n\n" +
                "Capitals carry over: @@Wensday gives Wednesday, @@THRU gives THROUGH.\n\n" +
                "Turn on \"Read the word out loud\" to hear each fixed word.\n" +
                "Put names and your own words in \"My words\" so they are found first.\n\n" +
                "SoundSpell sits with the hidden icons by the clock (the ^ arrow). " +
                "Right-click it to turn it off, change settings, or exit.",
                "SoundSpell", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected override void ExitThreadCore()
        {
            watcher.Stop();
            if (myWordsWatcher != null) myWordsWatcher.Dispose();
            if (voice != null) voice.Dispose();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }

    // Watches keys system-wide and fixes "@@word" when a word ends.
    sealed class KeyWatcher
    {
        const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11,
                  VK_MENU = 0x12, VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_LWIN = 0x5B, VK_RWIN = 0x5C,
                  VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
        static readonly IntPtr Marker = new IntPtr(0x5350454C); // tags the keys we send ourselves

        static readonly Regex Trigger = new Regex(@"@@([\p{L}']{1,40})$", RegexOptions.Compiled);

        readonly TrayApp app;
        readonly StringBuilder tail = new StringBuilder();
        readonly Native.LowLevelKeyboardProc proc, mouseProc;
        IntPtr hook = IntPtr.Zero, mouseHook = IntPtr.Zero;
        IntPtr lastWindow = IntPtr.Zero;
        Popup popup;

        // The last fix, which Ctrl+digit can change while the popup is up.
        List<string> choices;
        string original;      // what was typed, without @@
        string inserted;      // what is on screen now in place of @@word
        string termText;      // the key that ended the word, as it is on screen ("" when held back)

        // Suggestions shown while @@word is still being typed.
        string liveWord;
        List<string> liveFound;
        string liveFoundFor;  // the word liveFound was looked up for
        bool popupUp;

        // Lookups run on a worker so typing never waits for them.
        readonly object lookupLock = new object();
        readonly AutoResetEvent lookupWake = new AutoResetEvent(false);
        string lookupWanted;

        public bool Enabled = true;
        public bool AutoReplace = true;

        public KeyWatcher(TrayApp app)
        {
            this.app = app;
            proc = HookProc;
            mouseProc = MouseProc;
        }

        // A click can move the text cursor, so whatever was being typed no longer counts.
        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
                {
                    tail.Length = 0;
                    if (popupUp) CloseAll();
                }
            }
            return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
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
                    if (k.vkCode < 256) held[k.vkCode] = isDown;
                    if (isDown)
                    {
                        bool swallow = false;
                        try { swallow = OnKeyDown((int)k.vkCode, (int)k.scanCode); }
                        catch (Exception e) { tail.Length = 0; if (Log.On) Log.Write("error " + e); }
                        if (swallow) return new IntPtr(1);
                    }
                }
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
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

        static bool IsModifier(int vk)
        {
            return vk == VK_SHIFT || vk == VK_CONTROL || vk == VK_MENU || vk == VK_LWIN || vk == VK_RWIN || vk == VK_CAPITAL
                || (vk >= VK_LSHIFT && vk <= VK_RMENU);
        }

        bool OnKeyDown(int vk, int scan)
        {
            if (Log.On) Log.Write("key " + vk.ToString("X2") + " ctrl=" + Down(VK_CONTROL) + " popup=" + popupUp + " choices=" + (choices != null) + " tail=[" + tail + "]");
            if (IsModifier(vk)) return false;

            IntPtr fg = Native.GetForegroundWindow();
            if (fg != lastWindow) { lastWindow = fg; tail.Length = 0; CloseAll(); }

            bool ctrl = Down(VK_CONTROL), alt = Down(VK_MENU), win = Down(VK_LWIN) || Down(VK_RWIN);
            bool altGr = ctrl && alt && Down(VK_RMENU);

            // Ctrl+digit while the popup is up picks a word.
            if (popupUp && ctrl && !alt && !win && vk >= '0' && vk <= '9')
            {
                int n = vk - '0';
                if (choices != null && (n == 0 || n <= choices.Count))
                {
                    Pick(n == 0 ? original : choices[n - 1]);
                    return true;
                }
                Match live = Trigger.Match(tail.ToString());
                string liveTyped = live.Success ? live.Groups[1].Value.TrimEnd('\'') : null;
                if (choices == null && liveTyped != null && liveTyped == liveFoundFor && n >= 1 && n <= liveFound.Count)
                {
                    // Picked while still typing: that is the final choice.
                    string word = liveFound[n - 1];
                    ReplaceTyped(live, word, "");
                    CloseAll();
                    app.Post(delegate { app.Say(word); });
                    return true;
                }
            }

            // Anything else typed means the last fix is settled.
            if (choices != null) CloseAll();

            if ((ctrl || alt || win) && !altGr) { tail.Length = 0; CloseAll(); return false; }

            if (vk == VK_BACK)
            {
                if (tail.Length > 0) tail.Length--;
                UpdateLive();
                return false;
            }
            if (vk == VK_RETURN) return TryFix("", VK_RETURN);
            if (vk == VK_TAB) return TryFix("", VK_TAB);
            if (vk == VK_SPACE) return TryFix(" ", VK_SPACE);

            char c = Translate(vk, scan, fg);
            if (c == '\0') { tail.Length = 0; CloseAll(); return false; } // arrows, Home, Esc, dead keys...

            if (char.IsLetter(c) || c == '\'' || c == '@')
            {
                tail.Append(c);
                if (tail.Length > 64) tail.Remove(0, tail.Length - 64);
                UpdateLive();
                return false;
            }
            if (char.IsControl(c)) { tail.Length = 0; CloseAll(); return false; }
            return TryFix(c.ToString(), 0);
        }

        // While @@word is being typed, look it up in the background and show the matches.
        void UpdateLive()
        {
            Match m = Trigger.Match(tail.ToString());
            string word = m.Success ? m.Groups[1].Value.TrimEnd('\'') : "";
            if (word.Length < 2)
            {
                liveWord = null; liveFound = null; liveFoundFor = null;
                if (popupUp) CloseAll();
                return;
            }
            liveWord = word;
            lock (lookupLock) lookupWanted = word;
            lookupWake.Set();
        }

        void LookupLoop()
        {
            while (true)
            {
                lookupWake.WaitOne();
                string word;
                lock (lookupLock) { word = lookupWanted; lookupWanted = null; }
                Speller sp = app.Speller;
                if (word == null || sp == null) continue;
                List<string> found = sp.Suggest(word, 5);
                app.Post(delegate
                {
                    if (word != liveWord || choices != null || found.Count == 0) return; // typing moved on
                    liveFound = found;
                    liveFoundFor = word;
                    ShowPopup(found, 0, word, AutoReplace
                        ? "Space or Enter: use 1      Ctrl+number: pick"
                        : "Ctrl+number: put that word in");
                });
            }
        }

        // The word just ended with `term`. If it was @@word, swap it.
        // Enter and Tab are held back, so fixing a word never sends a message
        // or leaves the box; the next Enter does that as usual.
        bool TryFix(string term, int termVk)
        {
            Match m = Trigger.Match(tail.ToString());
            tail.Length = 0;
            string word = m.Success ? m.Groups[1].Value.TrimEnd('\'') : "";
            List<string> found = word.Length > 0 && word == liveFoundFor ? liveFound : null;
            liveWord = null; liveFound = null; liveFoundFor = null;
            if (word.Length == 0) { CloseAll(); return false; }
            Speller sp = app.Speller;
            if (found == null && sp != null) found = sp.Suggest(word, 5);
            if (found == null || found.Count == 0) { CloseAll(); return false; }

            original = word;
            if (AutoReplace)
            {
                ReplaceTyped(m, found[0], term);
                choices = found;
                ShowPopup(found, 1, word, termVk == VK_RETURN
                    ? "Enter: send      Ctrl+number: pick      Ctrl+0: keep \"" + word + "\""
                    : "Ctrl+number: pick      Ctrl+0: keep \"" + word + "\"");
                string said = found[0];
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
                }
                inserted = m.Value;
                termText = term;
                choices = found;
                ShowPopup(found, 0, word, "Ctrl+number: put that word in");
            }
            return true;
        }

        // Swap the typed @@word (and nothing after it) for `word`, then type `term`.
        void ReplaceTyped(Match m, string word, string term)
        {
            var keys = new List<Native.INPUT>();
            for (int i = 0; i < m.Value.Length; i++) AddVk(keys, VK_BACK);
            AddText(keys, word);
            if (term == " ") AddVk(keys, VK_SPACE); else AddText(keys, term);
            SendLater(keys);
            tail.Length = 0;
            inserted = word;
            termText = term;
        }

        void Pick(string word)
        {
            var keys = new List<Native.INPUT>();
            int erase = inserted.Length + termText.Length;
            for (int i = 0; i < erase; i++) AddVk(keys, VK_BACK);
            AddText(keys, word);
            AddText(keys, termText);
            SendLater(keys);
            inserted = word;
            CloseAll();
            app.Post(delegate { app.Say(word); });
        }

        void CloseAll()
        {
            choices = null;
            original = null;
            liveWord = null; liveFound = null; liveFoundFor = null;
            if (!popupUp) return;
            popupUp = false;
            app.Post(delegate { if (popup != null) popup.HideNow(); });
        }

        void ShowPopup(List<string> found, int current, string typed, string footer)
        {
            popupUp = true;
            Point at = CaretPoint();
            var list = new List<string>(found);
            app.Post(delegate
            {
                if (!popupUp) return;
                if (popup == null || popup.IsDisposed) { popup = new Popup(); popup.TimedOut += delegate { CloseAll(); }; }
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

    // The little list that shows next to the text, without taking focus.
    sealed class Popup : Form
    {
        readonly System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer();
        List<string> items = new List<string>();
        int current;
        string footer = "";
        // Verdana: wide letters that are hard to mix up (b/d, I/l), easier to read with dyslexia.
        readonly Font font = new Font("Verdana", 13f);
        readonly Font small = new Font("Verdana", 9f);
        const int Row = 30;

        public Popup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // Not TopMost = true: WinForms then activates the window when it shows,
            // and the fixed word gets typed into the popup instead of the app.
            BackColor = Color.FromArgb(32, 33, 36);
            DoubleBuffered = true;
            hideTimer.Interval = 15000;
            hideTimer.Tick += delegate { hideTimer.Stop(); HideNow(); if (TimedOut != null) TimedOut(this, EventArgs.Empty); };
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

        public event EventHandler TimedOut;

        public void HideNow()
        {
            hideTimer.Stop();
            if (IsHandleCreated) Native.ShowWindow(Handle, 0); // SW_HIDE
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; } // WM_MOUSEACTIVATE: MA_NOACTIVATE
            base.WndProc(ref m);
        }

        public void ShowChoices(List<string> list, int current, string footer, Point at)
        {
            items = list; this.current = current; this.footer = footer;
            int w = 220;
            using (var g = CreateGraphics())
            {
                foreach (string s in items) w = Math.Max(w, (int)g.MeasureString(s, font).Width + 100);
                w = Math.Max(w, (int)g.MeasureString(footer, small).Width + 24);
            }
            int h = 12 + items.Count * Row + 26;
            Rectangle screen = Screen.FromPoint(at).WorkingArea;
            int x = Math.Min(Math.Max(at.X, screen.Left), screen.Right - w);
            int y = at.Y + h > screen.Bottom ? at.Y - h - 30 : at.Y;
            // Shown with plain Win32 calls that never take focus from the app being typed in.
            Native.SetWindowPos(Handle, new IntPtr(-1), x, y, w, h, 0x0010 | 0x0040); // HWND_TOPMOST, NOACTIVATE | SHOWWINDOW
            Invalidate();
            Update();
            hideTimer.Stop();
            hideTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var border = new Pen(Color.FromArgb(70, 72, 78))) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            int y = 6;
            for (int i = 0; i < items.Count; i++)
            {
                bool on = i + 1 == current;
                if (on) using (var b = new SolidBrush(Color.FromArgb(38, 79, 120))) g.FillRectangle(b, 4, y, Width - 8, Row - 2);
                using (var dim = new SolidBrush(Color.FromArgb(160, 165, 175)))
                    g.DrawString("Ctrl+" + (i + 1), small, dim, 10, y + 7);
                using (var fg = new SolidBrush(on ? Color.White : Color.FromArgb(230, 232, 236)))
                    g.DrawString(items[i], font, fg, 72, y + 3);
                y += Row;
            }
            using (var dim = new SolidBrush(Color.FromArgb(160, 165, 175)))
                g.DrawString(footer, small, dim, 10, y + 5);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { hideTimer.Dispose(); font.Dispose(); small.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // A window to try spellings and see every suggestion.
    sealed class TryForm : Form
    {
        readonly TrayApp app;
        readonly TextBox box = new TextBox();
        readonly ListBox list = new ListBox();

        public TryForm(TrayApp app)
        {
            this.app = app;
            Text = "SoundSpell - try it";
            Icon = Art.MakeIcon(true);
            ClientSize = new Size(420, 330);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Verdana", 12f);

            var hint = new Label();
            hint.Text = "Type a word the way it sounds.\nClick a match to hear it, double-click to copy it.";
            hint.SetBounds(12, 8, 396, 44);
            box.SetBounds(12, 56, 396, 30);
            list.SetBounds(12, 94, 396, 226);
            Controls.Add(hint); Controls.Add(box); Controls.Add(list);

            box.TextChanged += delegate { Refresh(box.Text.Trim()); };
            list.SelectedIndexChanged += delegate
            {
                if (list.SelectedItem != null && app.Speller != null) app.SayNow(list.SelectedItem.ToString());
            };
            list.DoubleClick += delegate
            {
                if (list.SelectedItem != null) { Clipboard.SetText(list.SelectedItem.ToString()); Text = "Copied: " + list.SelectedItem; }
            };
        }

        void Refresh(string word)
        {
            list.Items.Clear();
            if (word.StartsWith("@@")) word = word.Substring(2);
            if (word.Length == 0) return;
            Speller sp = app.Speller;
            if (sp == null) { list.Items.Add("(still loading the word list)"); return; }
            foreach (string s in sp.Suggest(word, 8)) list.Items.Add(s);
        }
    }

    // Set SOUNDSPELL_LOG to a file path to trace what the key watcher sees and does.
    static class Log
    {
        static readonly string Path = Environment.GetEnvironmentVariable("SOUNDSPELL_LOG");
        public static readonly bool On = !string.IsNullOrEmpty(Path);
        public static void Write(string line)
        {
            try { File.AppendAllText(Path, DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine); } catch (Exception) { }
        }
    }

    static class Art
    {
        // Drawn at runtime so no .ico file is needed.
        public static Icon MakeIcon(bool on)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using (var path = Rounded(new Rectangle(1, 1, 30, 30), 8))
                    using (var b = new SolidBrush(on ? Color.FromArgb(37, 99, 235) : Color.FromArgb(110, 110, 118)))
                        g.FillPath(b, path);
                    using (var f = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("@", f, Brushes.White, new RectangleF(0, 0, 32, 31), sf);
                }
                IntPtr h = bmp.GetHicon();
                using (var tmp = Icon.FromHandle(h))
                {
                    var icon = (Icon)tmp.Clone();
                    Native.DestroyIcon(h);
                    return icon;
                }
            }
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    static class Native
    {
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public InputUnion u; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize, flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern short GetKeyState(int vk);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
        [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint threadId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder buf, int size, uint flags, IntPtr layout);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    }
}
