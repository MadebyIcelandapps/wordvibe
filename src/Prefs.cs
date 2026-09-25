// Settings (kept in HKCU\Software\SoundSpell) and the look of the popup.
//
// Written for C# 5 so the csc.exe that ships inside Windows can compile it.

using System;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SoundSpell
{
    static class Prefs
    {
        const string Key = @"Software\SoundSpell";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool Get(string name, bool fallback)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                object v = k == null ? null : k.GetValue(name);
                if (v == null) return fallback;
                try { return Convert.ToInt32(v) != 0; } catch (Exception) { return fallback; }
            }
        }

        public static void Set(string name, bool value)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }

        public static string GetText(string name, string fallback)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                object v = k == null ? null : k.GetValue(name);
                return v as string ?? fallback;
            }
        }

        public static void SetText(string name, string value)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value, RegistryValueKind.String);
        }

        public static bool StartsWithWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return (k != null && k.GetValue("SoundSpell") != null) || AdminMode.IsOn;
            }
            set
            {
                if (AdminMode.IsOn) return; // the admin task starts it at sign-in
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("SoundSpell", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("SoundSpell", false);
                }
            }
        }

        // The key that fixes the word just typed, without @@.
        public static readonly string[] Hotkeys = { "Tap Shift twice", "Ctrl+Space", "Tap right Ctrl", "None" };
        public static string Hotkey
        {
            get { return GetText("Hotkey", Hotkeys[0]); }
            set { SetText("Hotkey", value); }
        }
    }

    // Colours, font and size for the popup and windows. Cream and Verdana by
    // default: an off-white background and wide, clear letters are easier to read
    // with dyslexia than white on black.
    static class Theme
    {
        public static readonly string[] Sizes = { "Normal", "Large", "Extra large" };
        public static readonly string[] Colours = { "Cream", "Pale yellow", "Light blue", "White", "Dark" };
        static readonly string[] Fonts = { "Verdana", "OpenDyslexic", "Comic Sans MS", "Tahoma", "Arial" };

        public static Color Back, Text, Dim, Highlight, HighlightText, Border;
        public static string FontName;
        public static float Size;

        static Theme() { Load(); }

        public static string[] InstalledFonts()
        {
            var have = new System.Collections.Generic.List<string>();
            using (var fonts = new InstalledFontCollection())
                foreach (string f in Fonts)
                    foreach (FontFamily fam in fonts.Families)
                        if (string.Equals(fam.Name, f, StringComparison.OrdinalIgnoreCase)) { have.Add(f); break; }
            if (have.Count == 0) have.Add("Verdana");
            return have.ToArray();
        }

        public static void Load()
        {
            string size = Prefs.GetText("TextSize", Sizes[0]);
            Size = size == "Extra large" ? 20f : size == "Large" ? 16f : 13f;
            FontName = Prefs.GetText("Font", "Verdana");
            switch (Prefs.GetText("Colours", Colours[0]))
            {
                case "Dark":
                    Set(Color.FromArgb(32, 33, 36), Color.FromArgb(235, 236, 240), Color.FromArgb(160, 165, 175),
                        Color.FromArgb(38, 79, 120), Color.White, Color.FromArgb(70, 72, 78)); break;
                case "Pale yellow":
                    Set(Color.FromArgb(255, 253, 208), Color.FromArgb(30, 30, 30), Color.FromArgb(105, 100, 80),
                        Color.FromArgb(245, 228, 140), Color.FromArgb(20, 20, 20), Color.FromArgb(205, 195, 130)); break;
                case "Light blue":
                    Set(Color.FromArgb(223, 237, 250), Color.FromArgb(25, 30, 40), Color.FromArgb(85, 100, 120),
                        Color.FromArgb(178, 208, 240), Color.FromArgb(15, 20, 30), Color.FromArgb(150, 180, 210)); break;
                case "White":
                    Set(Color.White, Color.FromArgb(20, 20, 20), Color.FromArgb(100, 100, 100),
                        Color.FromArgb(210, 226, 246), Color.FromArgb(10, 10, 10), Color.FromArgb(190, 190, 190)); break;
                default: // Cream
                    Set(Color.FromArgb(253, 246, 227), Color.FromArgb(35, 35, 35), Color.FromArgb(110, 104, 92),
                        Color.FromArgb(240, 220, 165), Color.FromArgb(20, 20, 20), Color.FromArgb(205, 192, 160)); break;
            }
        }

        static void Set(Color back, Color text, Color dim, Color hi, Color hiText, Color border)
        {
            Back = back; Text = text; Dim = dim; Highlight = hi; HighlightText = hiText; Border = border;
        }

        public static Font Big() { return new Font(FontName, Size); }
        public static Font Small() { return new Font(FontName, Math.Max(9f, Size * 0.62f)); }
    }
}
