// Admin mode: Windows does not let a normal program type into programs running
// as administrator. Turning this on starts SoundSpell as administrator at sign-in
// through a scheduled task (one "Yes" in the Windows prompt, once).
//
// Written for C# 5 so the csc.exe that ships inside Windows can compile it.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;

namespace SoundSpell
{
    static class AdminMode
    {
        const string Task = "SoundSpell";

        public static bool IsElevated
        {
            get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        }

        static bool? isOn;
        public static bool IsOn
        {
            get
            {
                if (isOn == null) isOn = Run("schtasks.exe", "/Query /TN " + Task, false) == 0;
                return isOn.Value;
            }
        }

        // Returns false if she said no to the Windows prompt or it failed.
        public static bool TurnOn()
        {
            string exe = Application.ExecutablePath;
            string args = "/Create /F /TN " + Task + " /SC ONLOGON /RL HIGHEST /IT /TR \"\\\"" + exe + "\\\"\"";
            if (Run("schtasks.exe", args, true) != 0) return false;
            isOn = true;
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                k.DeleteValue("SoundSpell", false);
            // Start the admin copy once this one has closed.
            Later("schtasks.exe /Run /TN " + Task);
            return true;
        }

        public static bool TurnOff()
        {
            if (Run("schtasks.exe", "/Delete /F /TN " + Task, !IsElevated) != 0) return false;
            isOn = false;
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                k.SetValue("SoundSpell", "\"" + Application.ExecutablePath + "\"");
            // explorer.exe starts it as a normal program again, even from an admin one.
            Later("explorer.exe \"" + Application.ExecutablePath + "\"");
            return true;
        }

        static void Later(string command)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c timeout /t 2 /nobreak >nul & " + command);
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);
        }

        static int Run(string file, string args, bool elevate)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args);
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                if (elevate) { psi.UseShellExecute = true; psi.Verb = "runas"; }
                else { psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; }
                using (var p = Process.Start(psi))
                {
                    if (!elevate) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); }
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Win32Exception) { return -1; } // "No" in the Windows prompt
        }
    }
}
