// SoundSpell: a tray app. Type a word the way it sounds and tap Shift twice (or
// type @@ before it and then a space), and the word is swapped for the real
// spelling in whatever app you are typing in. A small popup lists other matches:
// Ctrl+number or a click picks one, Ctrl+0 puts back what you typed, pointing at
// a word reads it out. Enter after @@word only fixes the word.
//
// Written for C# 5 so the csc.exe that ships inside Windows can compile it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
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
        static int Main(string[] args)
        {
            // For the automatic checks: SoundSpell.exe --voice-test <voice id> <out.wav> <text>
            if (args.Length == 4 && args[0] == "--voice-test") return Voice.SelfTest(args[1], args[3], args[2]);
            // For the automatic checks: SoundSpell.exe --strip-test
            // shows the strip with sample words at 300,300 for a few seconds, with blank
            // space on its right so the glass can be measured.
            if (args.Length == 1 && args[0] == "--strip-test")
            {
                Application.EnableVisualStyles();
                var strip = new SentenceStrip();
                SentenceStrip.TestExtraWidth = 160;
                var words = new List<StripWord> {
                    new StripWord { Text = "see", State = WordState.Good },
                    new StripWord { Text = "wensday", State = WordState.Bad } };
                var host = new Form { ShowInTaskbar = false, Opacity = 0, Size = new System.Drawing.Size(1, 1) };
                host.Shown += delegate { strip.ShowWords(words, new System.Drawing.Rectangle(560, 300, 1, 20), true); };
                var close = new System.Windows.Forms.Timer { Interval = 4000 };
                close.Tick += delegate { host.Close(); };
                close.Start();
                Application.Run(host);
                return 0;
            }
            // For the automatic checks: SoundSpell.exe --update-window-test <WHATSNEW.md>
            // shows the update window as if 99.0.0 were out, for a few seconds.
            if (args.Length == 2 && args[0] == "--update-window-test")
            {
                Application.EnableVisualStyles();
                var notes = Updater.NotesSince(File.ReadAllText(args[1]), "3.0.0");
                var form = new UpdateForm("99.0.0", notes);
                form.Shown += delegate { Log.Write("update window shown: " + notes.Count + " notes, " + form.Bounds); };
                var close = new System.Windows.Forms.Timer { Interval = 4000 };
                close.Tick += delegate { form.Close(); };
                close.Start();
                Application.Run(form);
                return notes.Count > 0 ? 0 : 5;
            }

            bool first;
            using (var only = new Mutex(true, @"Local\SoundSpell.Tray", out first))
            {
                if (!first) return 0;
                // Real pixels everywhere: mouse clicks, the caret and our windows then
                // agree on where things are, on scaled screens too.
                try { if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) Native.SetProcessDPIAware(); }
                catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
            return 0;
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly KeyWatcher watcher;
        readonly Control ui;
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();
        TryForm tryForm;
        SettingsForm settingsForm;
        volatile Speller speller;
        ToolStripMenuItem enabledItem;
        FileSystemWatcher myWordsWatcher;
        readonly Dictionary<string, string> autoFixes = new Dictionary<string, string>(StringComparer.Ordinal);
        UpdateForm updateForm;

        public bool SayFixed, HearOnPoint;

        public TrayApp()
        {
            ui = new Control();
            ui.CreateControl();
            var h = ui.Handle; // the handle lets background threads post back to this one

            tray = new NotifyIcon();
            tray.Icon = Art.MakeIcon(Prefs.Get("Enabled", true));
            tray.Text = "SoundSpell";
            tray.ContextMenuStrip = BuildMenu();
            tray.DoubleClick += delegate { ShowTry(); };
            tray.Visible = true;

            watcher = new KeyWatcher(this);
            Apply();
            watcher.Start();

            // Load the word list off the UI thread so the tray icon appears at once.
            LoadAutoFixes();
            Reload();
            WatchMyWords();

            // Look for a new version a minute after starting, then once a day.
            updateTimer.Interval = 60 * 1000;
            updateTimer.Tick += delegate
            {
                updateTimer.Interval = 6 * 60 * 60 * 1000; // then every 6 hours
                if (Prefs.Get("AutoUpdate", true)) CheckForUpdates(false);
            };
            updateTimer.Start();

            if (!Prefs.Get("Welcomed2", false))
            {
                Prefs.Set("Welcomed2", true);
                tray.ShowBalloonTip(10000, "SoundSpell is running",
                    "Type a word the way it sounds, then tap Shift twice. Or type @@ before it.\n" +
                    "Right-click the icon by the clock for settings.", ToolTipIcon.Info);
            }
        }

        public Speller Speller { get { return speller; } }

        // Reads the settings into the running app.
        public void Apply()
        {
            watcher.Enabled = Prefs.Get("Enabled", true);
            watcher.AutoReplace = Prefs.Get("AutoReplace", true);
            watcher.Hotkey = Prefs.Hotkey;
            watcher.AutoFix = Prefs.Get("AutoFix", true);
            watcher.StripOn = Prefs.Get("Strip", true);
            watcher.ReadOnCtrl = Prefs.Get("ReadOnCtrl", true);
            SayFixed = Prefs.Get("ReadAloud", false);
            HearOnPoint = Prefs.Get("HearOnPoint", true);
            Voice.Chosen = Prefs.GetText("Voice", Voice.WindowsVoice);
            Voice.Slow = Prefs.GetText("VoiceSpeed", "Slow") == "Slow";
            Theme.Load();
            string key = watcher.Hotkey == "None" ? "type @@ before a word" : watcher.Hotkey.ToLowerInvariant() + " after a word";
            tray.Text = "SoundSpell: " + key;
        }

        // ---- files in %APPDATA%\SoundSpell --------------------------------------

        static string DataPath(string name)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundSpell");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        // Her own words (names, places, school words) go first in the list.
        public static string MyWordsPath { get { return DataPath("my-words.txt"); } }
        static string PicksPath { get { return DataPath("picks.txt"); } }
        public static string AutoFixPath { get { return DataPath("auto-fixes.txt"); } }

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
            myWordsWatcher = new FileSystemWatcher(Path.GetDirectoryName(MyWordsPath), "my-words.txt");
            myWordsWatcher.Changed += delegate { Reload(); };
            myWordsWatcher.Created += delegate { Reload(); };
            myWordsWatcher.EnableRaisingEvents = true;
        }

        static void EditMyWordsHeader()
        {
            File.WriteAllText(MyWordsPath,
                "# Your own words, one per line: names, places, words from school.\r\n" +
                "# They come first in the suggestions. Save the file and they work straight away.\r\n" +
                "# Add how a word sounds after = to help it be found, like: Niamh=neev\r\n", new UTF8Encoding(false));
        }

        void EditMyWords()
        {
            if (!File.Exists(MyWordsPath)) EditMyWordsHeader();
            System.Diagnostics.Process.Start("notepad.exe", "\"" + MyWordsPath + "\"");
        }

        static TextReader Resource(string name)
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoundSpell." + name);
            if (s == null)
            {
                string path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), name);
                if (!File.Exists(path)) return null;
                s = File.OpenRead(path);
            }
            return new StreamReader(s, Encoding.UTF8);
        }

        static Speller LoadSpeller()
        {
            string mine = "";
            if (File.Exists(MyWordsPath))
            {
                for (int tries = 0; ; tries++)
                {
                    try { mine = File.ReadAllText(MyWordsPath, Encoding.UTF8); break; }
                    catch (IOException) { if (tries > 5) break; Thread.Sleep(200); } // Notepad still saving
                }
            }
            Speller sp = Speller.Build(Resource("words.txt"), Resource("irish.txt"), new StringReader(mine), Resource("homophones.txt"));
            try { if (File.Exists(PicksPath)) using (var r = new StreamReader(PicksPath, Encoding.UTF8)) sp.LoadPicks(r); }
            catch (IOException) { }
            return sp;
        }

        // She picked `chosen` for `typed`: remember it for next time.
        public void Learn(string typed, string chosen)
        {
            Speller sp = speller;
            if (sp == null || string.IsNullOrEmpty(typed)) return;
            sp.Learn(typed, chosen);
            Post(delegate
            {
                try { using (var w = new StreamWriter(PicksPath, false, new UTF8Encoding(false))) sp.SavePicks(w); }
                catch (IOException) { }
            });
            // The same fix three times: from now on it happens by itself.
            string key = Speller.Plain(typed);
            bool added = false;
            lock (autoFixes)
            {
                if (Prefs.Get("AutoFix", true) && chosen != typed && !sp.IsWord(typed) && !autoFixes.ContainsKey(key)
                    && sp.Picked(typed, chosen) >= 3)
                {
                    autoFixes[key] = chosen;
                    added = true;
                }
            }
            if (added)
            {
                SaveAutoFixes();
                Post(delegate
                {
                    tray.ShowBalloonTip(8000, "Fixed by itself from now on",
                        "\"" + typed + "\" will turn into \"" + chosen + "\" as you type.\nCtrl+0 right after puts it back and stops this.",
                        ToolTipIcon.Info);
                });
            }
        }

        // ---- her usual mistakes, fixed by themselves (auto-fixes.txt: typed=fixed) ----

        void LoadAutoFixes()
        {
            var read = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(AutoFixPath))
                    foreach (string line in File.ReadAllLines(AutoFixPath, Encoding.UTF8))
                    {
                        string l = line.Trim();
                        int eq = l.IndexOf('=');
                        if (l.Length == 0 || l[0] == '#' || eq <= 0) continue;
                        read[Speller.Plain(l.Substring(0, eq))] = l.Substring(eq + 1).Trim(); // empty: never fix this one
                    }
            }
            catch (IOException) { }
            lock (autoFixes)
            {
                autoFixes.Clear();
                foreach (var kv in read) autoFixes[kv.Key] = kv.Value;
            }
        }

        void SaveAutoFixes()
        {
            var sb = new StringBuilder();
            sb.Append("# Words fixed by themselves as you type: typed=fixed. A line with nothing after = means never fix that word.\r\n");
            lock (autoFixes) foreach (var kv in autoFixes) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
            try { File.WriteAllText(AutoFixPath, sb.ToString(), new UTF8Encoding(false)); } catch (IOException) { }
        }

        public string AutoFixFor(string word)
        {
            string fix;
            Speller sp = speller;
            if (sp == null || sp.IsWord(word)) return null; // never touch a real word
            lock (autoFixes) return autoFixes.TryGetValue(Speller.Plain(word), out fix) && fix.Length > 0 ? fix : null;
        }

        public void RemoveAutoFix(string word)
        {
            lock (autoFixes) autoFixes[Speller.Plain(word)] = "";
            SaveAutoFixes();
        }

        public void EditAutoFixes()
        {
            if (!File.Exists(AutoFixPath)) SaveAutoFixes();
            var p = System.Diagnostics.Process.Start("notepad.exe", "\"" + AutoFixPath + "\"");
            if (p != null) { p.EnableRaisingEvents = true; p.Exited += delegate { Post(LoadAutoFixes); }; }
        }

        public void AddMyWord(string word)
        {
            try
            {
                if (!File.Exists(MyWordsPath)) EditMyWordsHeader();
                File.AppendAllText(MyWordsPath, word + "\r\n", new UTF8Encoding(false));
            }
            catch (IOException) { }
        }

        // ---- speech --------------------------------------------------------------

        public void Say(string word) { if (SayFixed) SayNow(word); }

        public void SayNow(string word) { Voice.Say(word); }

        // Tap Ctrl twice: read out the selected text, or else the sentence being typed.
        // A second double tap while it is reading stops it.
        public void ReadAloud(string sentence, bool sentenceOnly = false)
        {
            if (Voice.Speaking) { Voice.Stop(); return; }
            if (sentenceOnly || IsConsole(Native.GetForegroundWindow())) { Voice.Say(sentence); return; }

            IDataObject saved = null;
            try { saved = CopyOf(Clipboard.GetDataObject()); } catch (Exception) { }
            uint before = Native.GetClipboardSequenceNumber();
            watcher.SendCopy();
            int tries = 0;
            var wait = new System.Windows.Forms.Timer { Interval = 60 };
            wait.Tick += delegate
            {
                bool changed = Native.GetClipboardSequenceNumber() != before;
                if (!changed && ++tries < 25) return; // up to 1.5 s: some apps are slow to copy
                wait.Stop();
                wait.Dispose();
                string selected = null;
                if (changed)
                {
                    try { selected = Clipboard.GetText(); } catch (Exception) { }
                    try { if (saved != null) Clipboard.SetDataObject(saved, true); else Clipboard.Clear(); } catch (Exception) { }
                }
                string text = !string.IsNullOrEmpty(selected) && selected.Trim().Length > 0 ? selected : sentence;
                Voice.Say(text);
            };
            wait.Start();
        }

        static IDataObject CopyOf(IDataObject data)
        {
            if (data == null) return null;
            var copy = new DataObject();
            foreach (string f in data.GetFormats(false))
                try { object v = data.GetData(f, false); if (v != null) copy.SetData(f, v); } catch (Exception) { }
            return copy;
        }

        // Ctrl+C stops programs in a command window, so there only the sentence is read.
        static bool IsConsole(IntPtr hwnd)
        {
            var cls = new StringBuilder(64);
            Native.GetClassName(hwnd, cls, cls.Capacity);
            string c = cls.ToString();
            return c == "ConsoleWindowClass" || c == "CASCADIA_HOSTING_WINDOW_CLASS" || c == "PseudoConsoleWindow";
        }

        // ---- updates -------------------------------------------------------------

        // Looks for a newer version on GitHub. When there is one, the update window
        // comes up (unless she skipped that version or said "Later" today, and did
        // not ask herself).
        public void CheckForUpdates(bool asked)
        {
            var t = new Thread(delegate ()
            {
                string latest = Updater.Latest();
                string notes = Updater.IsNewer(latest) ? Updater.Notes() : null;
                Post(delegate
                {
                    if (Updater.IsNewer(latest))
                    {
                        if (asked || UpdateForm.ShouldOffer(latest)) ShowUpdate(latest, Updater.NotesSince(notes, Updater.Version));
                    }
                    else if (asked)
                        MessageBox.Show(latest == null ? "Could not reach GitHub to check. Is the internet on?" :
                            "You have the newest version (" + Updater.Version + ").", "SoundSpell");
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        void ShowUpdate(string latest, List<string> notes)
        {
            if (updateForm != null && !updateForm.IsDisposed) updateForm.Close();
            updateForm = new UpdateForm(latest, notes);
            updateForm.Show();
        }

        // ---- menu and windows ----------------------------------------------------

        public void Post(Action a) { if (!ui.IsDisposed) ui.BeginInvoke(a); }

        public void Quit() { ExitThread(); }

        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            var title = new ToolStripMenuItem("SoundSpell " + Updater.Version + (AdminMode.IsElevated ? " (administrator)" : ""));
            title.Enabled = false;
            m.Items.Add(title);
            m.Items.Add(new ToolStripSeparator());

            enabledItem = new ToolStripMenuItem("On", null, delegate
            {
                Prefs.Set("Enabled", !Prefs.Get("Enabled", true));
                Apply();
                enabledItem.Checked = watcher.Enabled;
                var old = tray.Icon;
                tray.Icon = Art.MakeIcon(watcher.Enabled);
                old.Dispose();
            });
            enabledItem.Checked = Prefs.Get("Enabled", true);
            m.Items.Add(enabledItem);
            m.Items.Add(new ToolStripMenuItem("Settings...", null, delegate { ShowSettings(); }));
            m.Items.Add(new ToolStripMenuItem("Try it...", null, delegate { ShowTry(); }));
            m.Items.Add(new ToolStripMenuItem("My words...", null, delegate { EditMyWords(); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("How to use", null, delegate { ShowHelp(); }));
            m.Items.Add(new ToolStripMenuItem("Check for updates", null, delegate { CheckForUpdates(true); }));
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

        void ShowSettings()
        {
            if (settingsForm == null || settingsForm.IsDisposed) settingsForm = new SettingsForm(this);
            settingsForm.Show();
            settingsForm.Activate();
        }

        void ShowHelp()
        {
            string key = watcher.Hotkey == "None" ? null : watcher.Hotkey;
            MessageBox.Show(
                (key != null
                    ? "Type a word the way it sounds, then " + key.ToLowerInvariant() + ".\n" +
                      "    nesesary  ->  necessary\n\n" +
                      "Or type @@ before the word and then a space:\n"
                    : "Type @@ and then a word the way it sounds, then a space:\n") +
                "    @@wensday  ->  Wednesday\n" +
                "    @@neev  ->  Niamh\n\n" +
                "A list of matches shows next to the text:\n" +
                "    Ctrl+1 to Ctrl+7, or a click, puts in that word\n" +
                "    Ctrl+0 puts back what you typed\n" +
                "    Point at a word, or press Ctrl+Shift+number, to hear it\n" +
                "    Enter after @@word only fixes it; press Enter again to send\n\n" +
                "Words that sound alike (there, their, they're) show what each one means.\n" +
                "The strip above your typing shows the sentence: green is right, red needs a look.\n" +
                "    Click a red word to fix it, or tap Shift twice for the nearest one\n" +
                "Tap Ctrl twice to hear the selected text, or the sentence you are typing.\n" +
                "Mistakes you fix the same way 3 times get fixed by themselves after that.\n" +
                "SoundSpell learns: what you pick comes first next time.\n\n" +
                "Put names and your own words in \"My words\" so they are found first.\n" +
                "SoundSpell sits with the hidden icons by the clock (the ^ arrow).",
                "SoundSpell", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected override void ExitThreadCore()
        {
            watcher.Stop();
            updateTimer.Dispose();
            if (myWordsWatcher != null) myWordsWatcher.Dispose();
            Voice.Stop();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
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
            ClientSize = new Size(560, 360);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font(Theme.FontName, 12f);
            BackColor = Theme.Back;
            ForeColor = Theme.Text;

            var hint = new Label();
            hint.Text = "Type a word the way it sounds.\nClick a match to hear it, double-click to copy it.";
            hint.SetBounds(12, 8, 536, 44);
            box.SetBounds(12, 56, 536, 30);
            list.SetBounds(12, 94, 536, 256);
            Controls.Add(hint); Controls.Add(box); Controls.Add(list);

            box.TextChanged += delegate { Refresh(box.Text.Trim()); };
            list.SelectedIndexChanged += delegate
            {
                var picked = list.SelectedItem as Suggestion;
                if (picked != null) app.SayNow(picked.Word);
            };
            list.DoubleClick += delegate
            {
                var picked = list.SelectedItem as Suggestion;
                if (picked != null) { Clipboard.SetText(picked.Word); Text = "Copied: " + picked.Word; }
            };
        }

        void Refresh(string word)
        {
            list.Items.Clear();
            if (word.StartsWith("@@")) word = word.Substring(2);
            if (word.Length == 0) return;
            Speller sp = app.Speller;
            if (sp == null) { list.Items.Add("(still loading the word list)"); return; }
            foreach (Suggestion s in sp.SuggestFull(word, 8, null)) list.Items.Add(s);
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
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

        // ---- see-through pictures (layered windows) -------------------------------

        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT at, ref SIZE size, IntPtr src, ref POINT from, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        // Shows `picture` (with its see-through pixels) as the layered window's content at x, y.
        public static void ShowLayered(IntPtr hwnd, System.Drawing.Bitmap picture, int x, int y)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr mem = CreateCompatibleDC(screen);
            IntPtr bits = picture.GetHbitmap(System.Drawing.Color.FromArgb(0));
            IntPtr old = SelectObject(mem, bits);
            try
            {
                var size = new SIZE { cx = picture.Width, cy = picture.Height };
                var from = new POINT();
                var at = new POINT { x = x, y = y };
                var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_ALPHA
                UpdateLayeredWindow(hwnd, screen, ref at, ref size, mem, ref from, 0, ref blend, 2); // ULW_ALPHA
            }
            finally
            {
                SelectObject(mem, old);
                DeleteObject(bits);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // ---- frosted glass (Windows 10 and 11) ------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }

        [StructLayout(LayoutKind.Sequential)]
        struct CompositionData { public int Attribute; public IntPtr Data; public int SizeOfData; }

        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Turns the blur off again. Always returns true.
        public static bool ClearFrost(IntPtr hwnd)
        {
            try
            {
                var accent = new AccentPolicy { AccentState = 0 };
                int size = Marshal.SizeOf(accent);
                IntPtr mem = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, mem, false);
                    var data = new CompositionData { Attribute = 19, Data = mem, SizeOfData = size };
                    SetWindowCompositionAttribute(hwnd, ref data);
                }
                finally { Marshal.FreeHGlobal(mem); }
            }
            catch (EntryPointNotFoundException) { }
            return true;
        }

        // Tints the window with `tint` at `opacity`, and blurs what is behind it if
        // `blur`. Plain blur, not acrylic: Windows 11 acrylic adds a milky layer that
        // makes it far less see-through than the chosen opacity. Returns false where
        // Windows cannot do it (then the window stays solid).
        public static bool MakeFrosted(IntPtr hwnd, System.Drawing.Color tint, float opacity, bool blur)
        {
            int a = Math.Max(0, Math.Min(255, (int)(opacity * 255)));
            int colour = (a << 24) | (tint.B << 16) | (tint.G << 8) | tint.R; // AABBGGRR
            try
            {
                foreach (int state in blur ? new[] { 3, 4 } : new[] { 2 }) // blur (acrylic if not), or tint only
                {
                    var accent = new AccentPolicy { AccentState = state, AccentFlags = 2, GradientColor = colour };
                    int size = Marshal.SizeOf(accent);
                    IntPtr mem = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.StructureToPtr(accent, mem, false);
                        var data = new CompositionData { Attribute = 19, Data = mem, SizeOfData = size }; // WCA_ACCENT_POLICY
                        if (SetWindowCompositionAttribute(hwnd, ref data) != 0)
                        {
                            int round = 2; // DWMWCP_ROUND, Windows 11 only
                            try { DwmSetWindowAttribute(hwnd, 33, ref round, 4); } catch (Exception) { }
                            return true;
                        }
                    }
                    finally { Marshal.FreeHGlobal(mem); }
                }
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            return false;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    }
}
