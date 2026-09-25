// Updates: compares this version with the VERSION file on GitHub, and installs
// the newest one with update.ps1 (download, unzip, run the installer).
//
// Written for C# 5 so the csc.exe that ships inside Windows can compile it.

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows.Forms;

namespace SoundSpell
{
    static class Updater
    {
        public const string Version = "3.2.0";
        const string VersionUrl = "https://raw.githubusercontent.com/MadebyIcelandapps/wordvibe/main/VERSION";

        // The newest version on GitHub, or null if it could not be reached.
        public static string Latest()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                using (var wc = new WebClient())
                {
                    wc.Headers["User-Agent"] = "SoundSpell/" + Version;
                    return wc.DownloadString(VersionUrl).Trim();
                }
            }
            catch (Exception) { return null; }
        }

        const string NotesUrl = "https://raw.githubusercontent.com/MadebyIcelandapps/wordvibe/main/WHATSNEW.md";

        // The WHATSNEW.md text on GitHub, or null.
        public static string Notes()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                using (var wc = new WebClient())
                {
                    wc.Headers["User-Agent"] = "SoundSpell/" + Version;
                    wc.Encoding = System.Text.Encoding.UTF8;
                    return wc.DownloadString(NotesUrl);
                }
            }
            catch (Exception) { return null; }
        }

        // The notes of every version newer than this one, newest first: the lines
        // under each "## x.y.z" heading, without the "- ".
        public static System.Collections.Generic.List<string> NotesSince(string notes, string have)
        {
            var lines = new System.Collections.Generic.List<string>();
            if (notes == null) return lines;
            System.Version mine;
            System.Version.TryParse(have, out mine);
            bool take = false;
            foreach (string raw in notes.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("## "))
                {
                    System.Version v;
                    take = System.Version.TryParse(line.Substring(3).Trim(), out v) && (mine == null || v > mine);
                    continue;
                }
                if (take && line.StartsWith("- ")) lines.Add(line.Substring(2));
            }
            return lines;
        }

        public static bool IsNewer(string latest)
        {
            System.Version a, b;
            return latest != null && System.Version.TryParse(latest, out a) && System.Version.TryParse(Version, out b) && a > b;
        }

        // Runs update.ps1 next to the program; it closes this copy and starts the new one.
        public static bool Install()
        {
            string script = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "update.ps1");
            if (!File.Exists(script)) return false;
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"");
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
            return true;
        }
    }
}
