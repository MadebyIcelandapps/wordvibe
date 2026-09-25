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
        public const string Version = "3.0.0";
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
