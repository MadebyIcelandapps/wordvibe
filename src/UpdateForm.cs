// The "new version" window: a small card in the corner of the screen with what is
// new and three buttons. It does not take the focus, so it can never catch keys
// meant for what she is typing in.
//
// Written for C# 5 so the csc.exe that ships inside Windows can compile it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SoundSpell
{
    sealed class UpdateForm : Form
    {
        public UpdateForm(string latest, List<string> notes)
        {
            Text = "SoundSpell update";
            Icon = Art.MakeIcon(true);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = new Font(Theme.FontName, 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            int width = 420, pad = 16, y = pad;

            var title = new Label { Text = "A new SoundSpell is ready", AutoSize = true, Font = new Font(Theme.FontName, 13f, FontStyle.Bold) };
            title.Location = new Point(pad, y);
            Controls.Add(title);
            y += 34;

            var version = new Label
            {
                Text = "Version " + latest + "   (you have " + Updater.Version + ")",
                AutoSize = true, ForeColor = Theme.Dim, Font = new Font(Theme.FontName, 9f)
            };
            version.Location = new Point(pad, y);
            Controls.Add(version);
            y += 28;

            if (notes.Count > 0)
            {
                var heading = new Label { Text = "What's new:", AutoSize = true, Font = new Font(Theme.FontName, 10f, FontStyle.Bold) };
                heading.Location = new Point(pad, y);
                Controls.Add(heading);
                y += 26;
                var list = new Panel { AutoScroll = true, BackColor = Theme.Back };
                int ly = 0;
                foreach (string note in notes)
                {
                    var bullet = new Label { Text = "•", AutoSize = true };
                    bullet.Location = new Point(0, ly);
                    var l = new Label { Text = note, MaximumSize = new Size(width - 2 * pad - 40, 0), AutoSize = true };
                    l.Location = new Point(16, ly);
                    list.Controls.Add(bullet);
                    list.Controls.Add(l);
                    ly += l.PreferredHeight + 8;
                }
                list.SetBounds(pad, y, width - 2 * pad, Math.Min(ly, 260));
                Controls.Add(list);
                y += list.Height + 12;
            }

            var update = new Button { Text = "Update now", AutoSize = true, BackColor = Theme.Highlight, ForeColor = Theme.HighlightText, FlatStyle = FlatStyle.Flat };
            var later = new Button { Text = "Later", AutoSize = true, FlatStyle = FlatStyle.Flat };
            var skip = new Button { Text = "Skip this version", AutoSize = true, FlatStyle = FlatStyle.Flat };
            update.Location = new Point(pad, y);
            Controls.Add(update);
            later.Location = new Point(pad + 130, y);
            Controls.Add(later);
            skip.Location = new Point(pad + 210, y);
            Controls.Add(skip);
            var status = new Label { AutoSize = true, ForeColor = Theme.Dim, Font = new Font(Theme.FontName, 9f) };
            status.Location = new Point(pad, y + 40);
            Controls.Add(status);
            y += 72;

            update.Click += delegate
            {
                update.Enabled = later.Enabled = skip.Enabled = false;
                if (Updater.Install())
                    status.Text = "Updating... SoundSpell will start again in a few seconds.";
                else
                    status.Text = "The updater is missing. Download SoundSpell again and run Install.cmd.";
            };
            later.Click += delegate
            {
                Prefs.SetText("UpdateLater", DateTime.UtcNow.AddHours(20).ToString("o"));
                Close();
            };
            skip.Click += delegate
            {
                Prefs.SetText("UpdateSkip", latest);
                Close();
            };

            ClientSize = new Size(width, y);
            // Bottom-right corner, above the taskbar.
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        }

        // Shown without taking the focus from whatever she is typing in.
        protected override bool ShowWithoutActivation { get { return true; } }

        // Whether to bring the window up for `latest` without being asked:
        // not if she skipped that version, or said "Later" less than a day ago.
        public static bool ShouldOffer(string latest)
        {
            if (Prefs.GetText("UpdateSkip", "") == latest) return false;
            DateTime until;
            if (DateTime.TryParse(Prefs.GetText("UpdateLater", ""), null, System.Globalization.DateTimeStyles.RoundtripKind, out until)
                && DateTime.UtcNow < until) return false;
            return true;
        }
    }
}
