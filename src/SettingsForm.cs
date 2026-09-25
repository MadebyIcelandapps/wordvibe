// The settings window. Every change takes effect straight away.
//
// Written for C# 5 so the csc.exe that ships inside Windows can compile it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SoundSpell
{
    sealed class SettingsForm : Form
    {
        readonly Panel preview = new Panel();
        Label glassNowLabel;
        int y = 12;
        bool loading = true;

        public SettingsForm(TrayApp app)
        {
            Text = "SoundSpell settings";
            Icon = Art.MakeIcon(true);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Verdana", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 100);

            Heading("Fixing words");
            Choice("Shortcut to fix the word you just typed:", Prefs.Hotkeys, Prefs.Hotkey,
                delegate (string v) { Prefs.Hotkey = v; app.Apply(); });
            Note("Type a word the way it sounds, then use the shortcut. @@word works too.");
            Check("Put in the first match by itself (off: only show the list)", Prefs.Get("AutoReplace", true),
                delegate (bool v) { Prefs.Set("AutoReplace", v); app.Apply(); });
            Check("Show the sentence strip while I type (green: right, red: check it)", Prefs.Get("Strip", true),
                delegate (bool v) { Prefs.Set("Strip", v); app.Apply(); });
            Choice("    Strip size:", SentenceStrip.Sizes, Prefs.GetText("StripSize", SentenceStrip.Sizes[0]),
                delegate (string v) { Prefs.SetText("StripSize", v); });
            Choice("    Where:", SentenceStrip.Places, Prefs.GetText("StripPlace", SentenceStrip.Places[0]),
                delegate (string v) { Prefs.SetText("StripPlace", v); });
            Choice("    Show it:", SentenceStrip.Shows, Prefs.GetText("StripShow", SentenceStrip.Shows[0]),
                delegate (string v) { Prefs.SetText("StripShow", v); });
            Slider("    Glass opacity:", 10, 90, Prefs.GetNumber("GlassOpacity", 35), "%",
                delegate (int v) { Prefs.SetNumber("GlassOpacity", v); });
            Check("    Frosted (blur what is behind it)", Prefs.Get("GlassBlur", true),
                delegate (bool v) { Prefs.Set("GlassBlur", v); if (glassNowLabel != null) glassNowLabel.Text = "Now: " + SentenceStrip.GlassDescription(); });
            var glassNow = new Label { Text = "Now: " + SentenceStrip.GlassDescription(), MaximumSize = new Size(ClientSize.Width - 60, 0), AutoSize = true, ForeColor = SystemColors.GrayText, Font = new Font("Verdana", 9f) };
            glassNow.Location = new Point(40, y - 4);
            Controls.Add(glassNow);
            glassNowLabel = glassNow;
            y += glassNow.PreferredHeight + 10;
            Check("Fix my usual mistakes by themselves", Prefs.Get("AutoFix", true),
                delegate (bool v) { Prefs.Set("AutoFix", v); app.Apply(); });
            Note("A mistake you fix the same way 3 times gets fixed as you type after that.");
            var list = new Button { Text = "See or change that list...", AutoSize = true };
            list.Location = new Point(40, y);
            list.Click += delegate { app.EditAutoFixes(); };
            Controls.Add(list);
            y += 38;

            Heading("Hearing words");
            VoicePicker();
            Check("Tap Ctrl twice to hear the selected text (or the sentence I am typing)", Prefs.Get("ReadOnCtrl", true),
                delegate (bool v) { Prefs.Set("ReadOnCtrl", v); app.Apply(); });
            Check("Read the fixed word out loud", Prefs.Get("ReadAloud", false),
                delegate (bool v) { Prefs.Set("ReadAloud", v); app.Apply(); });
            Check("Read a match out loud when I point at it", Prefs.Get("HearOnPoint", true),
                delegate (bool v) { Prefs.Set("HearOnPoint", v); app.Apply(); });
            Choice("Voice speed:", new[] { "Slow", "Normal" }, Prefs.GetText("VoiceSpeed", "Slow"),
                delegate (string v) { Prefs.SetText("VoiceSpeed", v); app.Apply(); });
            Note("Ctrl+Shift+number reads a match out loud too.");

            Heading("How the list looks");
            Choice("Text size:", Theme.Sizes, Prefs.GetText("TextSize", Theme.Sizes[0]),
                delegate (string v) { Prefs.SetText("TextSize", v); Theme.Load(); preview.Invalidate(); });
            Choice("Colours:", Theme.Colours, Prefs.GetText("Colours", Theme.Colours[0]),
                delegate (string v) { Prefs.SetText("Colours", v); Theme.Load(); preview.Invalidate(); });
            Choice("Font:", Theme.InstalledFonts(), Prefs.GetText("Font", "Verdana"),
                delegate (string v) { Prefs.SetText("Font", v); Theme.Load(); preview.Invalidate(); });
            preview.SetBounds(20, y, ClientSize.Width - 40, 110);
            preview.Paint += PaintPreview;
            Controls.Add(preview);
            y += 120;

            Heading("Windows");
            Check("Start SoundSpell when I sign in", Prefs.StartsWithWindows,
                delegate (bool v) { Prefs.StartsWithWindows = v; });
            var admin = Check("Also work in programs running as administrator", AdminMode.IsOn, null);
            admin.CheckedChanged += delegate
            {
                if (loading) return;
                bool ok = admin.Checked ? AdminMode.TurnOn() : AdminMode.TurnOff();
                if (!ok)
                {
                    loading = true; admin.Checked = !admin.Checked; loading = false;
                    MessageBox.Show(this, "Windows did not allow the change.", "SoundSpell", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                MessageBox.Show(this, "SoundSpell will restart in a moment.", "SoundSpell", MessageBoxButtons.OK, MessageBoxIcon.Information);
                app.Quit();
            };
            Note("Windows asks you once to say Yes. Needed for some games and admin tools.");
            Check("Check for updates once a day", Prefs.Get("AutoUpdate", true),
                delegate (bool v) { Prefs.Set("AutoUpdate", v); });

            var check = new Button { Text = "Check for updates now", AutoSize = true };
            check.Location = new Point(20, y);
            check.Click += delegate { app.CheckForUpdates(true); };
            Controls.Add(check);
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
            close.Location = new Point(ClientSize.Width - 110, y);
            close.Click += delegate { Close(); };
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;
            y += 44;

            var version = new Label { Text = "Version " + Updater.Version, AutoSize = true, ForeColor = SystemColors.GrayText };
            version.Location = new Point(20, y);
            Controls.Add(version);
            y += 28;

            // Scrolls on small screens.
            AutoScroll = true;
            int room = Screen.FromPoint(Cursor.Position).WorkingArea.Height - 80;
            ClientSize = new Size(ClientSize.Width + (y > room ? SystemInformation.VerticalScrollBarWidth : 0), Math.Min(y, room));
            loading = false;
        }

        // The voice: Windows' own, or a natural one that downloads once.
        void VoicePicker()
        {
            var label = new Label { Text = "Voice:", AutoSize = true };
            label.Location = new Point(20, y + 4);
            Controls.Add(label);
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
            box.Location = new Point(ClientSize.Width - 20 - box.Width, y);
            Controls.Add(box);
            y += 34;
            var status = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Font = new Font("Verdana", 9f) };
            status.Location = new Point(40, y - 4);
            Controls.Add(status);
            var sample = new Button { Text = "Hear a sample", AutoSize = true };
            sample.Location = new Point(ClientSize.Width - 20 - 150, y - 6);
            sample.Click += delegate { Voice.Say("Hello! This is how I sound. See you on Wednesday, Niamh."); };
            Controls.Add(sample);
            y += 34;

            Action fill = delegate
            {
                box.Items.Clear();
                box.Items.Add("Windows voice (robotic, no download)");
                foreach (NaturalVoice v in Voice.Catalog)
                    box.Items.Add(v.Name + (Voice.IsInstalled(v) ? "  (natural)" : "  (natural, downloads about 60 MB)"));
                NaturalVoice now = Voice.Find(Prefs.GetText("Voice", Voice.WindowsVoice));
                box.SelectedIndex = now == null ? 0 : Array.IndexOf(Voice.Catalog, now) + 1;
            };
            fill();
            status.Text = "Natural voices run on this computer; nothing you read is sent anywhere.";

            box.SelectedIndexChanged += delegate
            {
                if (loading) return;
                if (box.SelectedIndex <= 0)
                {
                    Prefs.SetText("Voice", Voice.WindowsVoice);
                    Voice.Chosen = Voice.WindowsVoice;
                    return;
                }
                NaturalVoice v = Voice.Catalog[box.SelectedIndex - 1];
                if (Voice.IsInstalled(v))
                {
                    Prefs.SetText("Voice", v.Id); Voice.Chosen = v.Id;
                    Voice.Say("Hello, I'm " + v.Name.Split(' ')[0] + ".");
                    return;
                }
                box.Enabled = false;
                status.Text = "Downloading " + v.Name + "... 0%";
                var t = new System.Threading.Thread(delegate ()
                {
                    string err = Voice.Download(v, delegate (int pct)
                    {
                        BeginInvoke((Action)delegate { status.Text = "Downloading " + v.Name + "... " + pct + "%"; });
                    });
                    BeginInvoke((Action)delegate
                    {
                        box.Enabled = true;
                        loading = true;
                        if (err == null) { Prefs.SetText("Voice", v.Id); Voice.Chosen = v.Id; }
                        fill();
                        loading = false;
                        if (err == null) { status.Text = v.Name + " is ready."; Voice.Say("Hello, I'm " + v.Name.Split(' ')[0] + ". This is how I sound."); }
                        else status.Text = "Could not download it: " + err;
                    });
                });
                t.IsBackground = true;
                t.Start();
            };
        }

        void Heading(string text)
        {
            if (y > 12) y += 8;
            var l = new Label { Text = text, AutoSize = true, Font = new Font("Verdana", 11f, FontStyle.Bold) };
            l.Location = new Point(12, y);
            Controls.Add(l);
            y += 30;
        }

        void Note(string text)
        {
            var l = new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Font = new Font("Verdana", 9f) };
            l.Location = new Point(40, y - 4);
            Controls.Add(l);
            y += 22;
        }

        CheckBox Check(string text, bool value, Action<bool> changed)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Checked = value };
            c.Location = new Point(20, y);
            if (changed != null) c.CheckedChanged += delegate { if (!loading) changed(c.Checked); };
            Controls.Add(c);
            y += 30;
            return c;
        }

        void Slider(string label, int min, int max, int value, string unit, Action<int> changed)
        {
            var l = new Label { Text = label, AutoSize = true };
            l.Location = new Point(20, y + 6);
            Controls.Add(l);
            var shown = new Label { AutoSize = true, Text = value + unit };
            var bar = new TrackBar { Minimum = min, Maximum = max, TickFrequency = 10, SmallChange = 5, LargeChange = 10, Width = 160, Value = Math.Max(min, Math.Min(max, value)) };
            bar.Location = new Point(ClientSize.Width - 20 - 200 - 4, y);
            shown.Location = new Point(ClientSize.Width - 20 - 36, y + 6);
            bar.ValueChanged += delegate { shown.Text = bar.Value + unit; if (!loading) changed(bar.Value); };
            Controls.Add(bar);
            Controls.Add(shown);
            y += 44;
        }

        ComboBox Choice(string label, string[] options, string value, Action<string> changed)
        {
            var l = new Label { Text = label, AutoSize = true };
            l.Location = new Point(20, y + 4);
            Controls.Add(l);
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
            box.Items.AddRange(options);
            box.SelectedItem = Array.IndexOf(options, value) >= 0 ? value : options[0];
            box.Location = new Point(ClientSize.Width - 20 - box.Width, y);
            box.SelectedIndexChanged += delegate { if (!loading) changed((string)box.SelectedItem); };
            Controls.Add(box);
            y += 34;
            return box;
        }

        // A small copy of the popup, in the chosen colours, font and size.
        void PaintPreview(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Back);
            using (var font = Theme.Big())
            using (var small = Theme.Small())
            using (var text = new SolidBrush(Theme.Text))
            using (var dim = new SolidBrush(Theme.Dim))
            using (var hi = new SolidBrush(Theme.Highlight))
            using (var hiText = new SolidBrush(Theme.HighlightText))
            using (var border = new Pen(Theme.Border))
            {
                g.DrawRectangle(border, 0, 0, preview.Width - 1, preview.Height - 1);
                int row = (int)Math.Ceiling(g.MeasureString("Ag", font).Height) + 8;
                string[,] rows = { { "their", "belongs to them" }, { "there", "in or at that place" }, { "they're", "they are" } };
                int yy = 4;
                for (int i = 0; i < 3 && yy + row <= preview.Height; i++)
                {
                    if (i == 0) g.FillRectangle(hi, 3, yy, preview.Width - 6, row - 2);
                    g.DrawString("Ctrl+" + (i + 1), small, dim, 8, yy + (row - small.Height) / 2f);
                    SizeF w = g.MeasureString(rows[i, 0], font);
                    g.DrawString(rows[i, 0], font, i == 0 ? hiText : text, 70, yy + (row - font.Height) / 2f);
                    g.DrawString(rows[i, 1], small, dim, 84 + w.Width, yy + (row - small.Height) / 2f);
                    yy += row;
                }
            }
        }
    }
}
