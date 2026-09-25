// Voice: reads words and sentences out loud. Uses a natural-sounding Piper voice
// (free, runs on this computer, nothing is sent anywhere) once it has been
// downloaded, and the built-in Windows voice until then or if Piper fails.
//
// Written for C# 5 so the csc.exe that ships inside Windows can compile it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Media;
using System.Net;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SoundSpell
{
    sealed class NaturalVoice
    {
        public string Id, Name, Path;   // Path under piper-voices, e.g. en/en_GB/cori/medium
        public NaturalVoice(string id, string name, string path) { Id = id; Name = name; Path = path; }
        public string ModelUrl { get { return Voice.VoicesBase + Path + "/" + Id + ".onnx"; } }
        public string ConfigUrl { get { return ModelUrl + ".json"; } }
        public override string ToString() { return Name; }
    }

    static class Voice
    {
        public const string PiperUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
        public const string VoicesBase = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/";
        public const string WindowsVoice = "windows";

        // British and Irish-friendly voices first. There is no Irish-accent Piper voice yet.
        public static readonly NaturalVoice[] Catalog = {
            new NaturalVoice("en_GB-cori-medium", "Cori (English woman)", "en/en_GB/cori/medium"),
            new NaturalVoice("en_GB-jenny_dioco-medium", "Jenny (English woman)", "en/en_GB/jenny_dioco/medium"),
            new NaturalVoice("en_GB-alba-medium", "Alba (Scottish woman)", "en/en_GB/alba/medium"),
            new NaturalVoice("en_GB-alan-medium", "Alan (English man)", "en/en_GB/alan/medium"),
            new NaturalVoice("en_GB-northern_english_male-medium", "Northern English man", "en/en_GB/northern_english_male/medium"),
            new NaturalVoice("en_US-amy-medium", "Amy (American woman)", "en/en_US/amy/medium"),
            new NaturalVoice("en_US-ryan-medium", "Ryan (American man)", "en/en_US/ryan/medium"),
        };

        static string Home
        {
            get
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundSpell");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
        static string PiperExe { get { return System.IO.Path.Combine(Home, "piper", "piper.exe"); } }
        static string ModelFile(NaturalVoice v) { return System.IO.Path.Combine(Home, "voices", v.Id + ".onnx"); }

        public static NaturalVoice Find(string id)
        {
            foreach (NaturalVoice v in Catalog) if (v.Id == id) return v;
            return null;
        }

        public static bool IsInstalled(NaturalVoice v)
        {
            return v != null && File.Exists(PiperExe) && File.Exists(ModelFile(v)) && File.Exists(ModelFile(v) + ".json");
        }

        // ---- settings, set by the app ----------------------------------------

        public static string Chosen = WindowsVoice;   // voice id, or "windows"
        public static bool Slow = true;

        // ---- downloading -------------------------------------------------------

        // Downloads Piper (once) and the voice. `progress` gets 0..100. Returns null or an error.
        public static string Download(NaturalVoice v, Action<int> progress)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                if (!File.Exists(PiperExe))
                {
                    string zip = System.IO.Path.Combine(Home, "piper.zip");
                    Fetch(PiperUrl, zip, delegate (int p) { progress(p / 4); });
                    string piperDir = System.IO.Path.Combine(Home, "piper");
                    if (Directory.Exists(piperDir)) Directory.Delete(piperDir, true);
                    ZipFile.ExtractToDirectory(zip, Home); // the zip holds a "piper" folder
                    File.Delete(zip);
                    if (!File.Exists(PiperExe)) return "piper.exe was not in the download";
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ModelFile(v)));
                Fetch(v.ConfigUrl, ModelFile(v) + ".json", null);
                Fetch(v.ModelUrl, ModelFile(v), delegate (int p) { progress(25 + p * 3 / 4); });
                progress(100);
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        static void Fetch(string url, string file, Action<int> progress)
        {
            string part = file + ".part";
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "SoundSpell/" + Updater.Version;
            req.AllowAutoRedirect = true;
            using (var resp = req.GetResponse())
            using (var from = resp.GetResponseStream())
            using (var to = File.Create(part))
            {
                long total = resp.ContentLength, got = 0;
                var buf = new byte[81920];
                int n, last = -1;
                while ((n = from.Read(buf, 0, buf.Length)) > 0)
                {
                    to.Write(buf, 0, n);
                    got += n;
                    if (progress != null && total > 0)
                    {
                        int p = (int)(got * 100 / total);
                        if (p != last) { last = p; progress(p); }
                    }
                }
            }
            if (File.Exists(file)) File.Delete(file);
            File.Move(part, file);
        }

        // ---- speaking -----------------------------------------------------------

        static readonly object gate = new object();
        static int generation;            // bumped by every Say/Stop; older speech gives up
        static SpeechSynthesizer sapi;
        static Prompt lastPrompt;
        static SoundPlayer player;
        static Process piper;
        static string piperVoice;
        static bool piperSlow;
        static readonly object piperLock = new object();
        static bool keepOpenWorks = true;   // false once the kept-open process failed to answer

        public static bool Speaking { get; private set; }

        // Says `text`, stopping anything said before. Long text is read sentence by sentence.
        public static void Say(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;
            if (Log.On) Log.Write("speak: " + text);
            int gen;
            lock (gate) { gen = ++generation; }
            StopPlaying();
            var t = new Thread(delegate () { Speak(text.Trim(), gen); });
            t.IsBackground = true;
            t.Start();
        }

        public static void Stop()
        {
            lock (gate) { generation++; }
            StopPlaying();
        }

        static void StopPlaying()
        {
            try { if (player != null) player.Stop(); } catch (Exception) { }
            try { if (sapi != null) sapi.SpeakAsyncCancelAll(); } catch (Exception) { }
            Speaking = false;
        }

        static bool Current(int gen) { lock (gate) return gen == generation; }

        static void Speak(string text, int gen)
        {
            NaturalVoice v = Find(Chosen);
            if (IsInstalled(v))
            {
                try
                {
                    Speaking = true;
                    foreach (string part in Sentences(text))
                    {
                        if (!Current(gen)) return;
                        string wav = Synthesize(v, part);
                        if (wav == null) throw new IOException("no audio");
                        if (!Current(gen)) return;
                        using (var p = new SoundPlayer(wav))
                        {
                            lock (gate) { if (!Current(gen)) return; player = p; }
                            p.PlaySync(); // Stop() from another thread ends this early
                        }
                        try { File.Delete(wav); } catch (Exception) { }
                    }
                    return;
                }
                catch (Exception e) { if (Log.On) Log.Write("piper failed, using the Windows voice: " + e.Message); }
                finally { if (Current(gen)) Speaking = false; }
            }
            SayWithWindows(text, gen);
        }

        static void SayWithWindows(string text, int gen)
        {
            try
            {
                lock (gate)
                {
                    if (!Current(gen)) return;
                    if (sapi == null)
                    {
                        sapi = new SpeechSynthesizer();
                        sapi.SpeakCompleted += delegate (object o, SpeakCompletedEventArgs e) { if (e.Prompt == lastPrompt) Speaking = false; };
                        // An Irish or British voice if one is installed.
                        foreach (string culture in new[] { "en-IE", "en-GB" })
                        {
                            bool found = false;
                            foreach (InstalledVoice iv in sapi.GetInstalledVoices())
                                if (iv.Enabled && iv.VoiceInfo.Culture.Name == culture) { sapi.SelectVoice(iv.VoiceInfo.Name); found = true; break; }
                            if (found) break;
                        }
                    }
                    sapi.Rate = Slow ? -3 : 0;
                    sapi.SpeakAsyncCancelAll();
                    Speaking = true;
                    lastPrompt = sapi.SpeakAsync(text);
                }
            }
            catch (Exception) { } // no voice installed
        }

        static IEnumerable<string> Sentences(string text)
        {
            text = Regex.Replace(text, @"\s+", " ");
            foreach (Match m in Regex.Matches(text, @"[^.!?]+[.!?]*"))
            {
                string s = m.Value.Trim();
                if (s.Length > 0) yield return s;
            }
        }

        // One Piper process stays open per voice; each line of text in gives a WAV file
        // path out. Falls back to a one-off run if that does not answer.
        static string Synthesize(NaturalVoice v, string text)
        {
            string outDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SoundSpell-speech");
            Directory.CreateDirectory(outDir);
            string line = text.Replace('\r', ' ').Replace('\n', ' ');
            lock (piperLock)
            {
                if (keepOpenWorks) try
                {
                    if (piper == null || piper.HasExited || piperVoice != v.Id || piperSlow != Slow)
                    {
                        if (piper != null && !piper.HasExited) try { piper.Kill(); } catch (Exception) { }
                        piper = Start(v, "--output_dir \"" + outDir + "\"");
                        piperVoice = v.Id;
                        piperSlow = Slow;
                    }
                    piper.StandardInput.WriteLine(line);
                    piper.StandardInput.Flush();
                    var read = piper.StandardOutput.ReadLineAsync();
                    if (read.Wait(15000) && read.Result != null && File.Exists(read.Result.Trim())) return read.Result.Trim();
                    try { piper.Kill(); } catch (Exception) { }
                    piper = null;
                    keepOpenWorks = false;
                }
                catch (Exception) { piper = null; keepOpenWorks = false; }
            }
            // One-off: text in, one file out.
            string file = System.IO.Path.Combine(outDir, Guid.NewGuid().ToString("N") + ".wav");
            using (Process p = Start(v, "--output_file \"" + file + "\""))
            {
                p.StandardInput.WriteLine(line);
                p.StandardInput.Close();
                p.WaitForExit(30000);
            }
            return File.Exists(file) ? file : null;
        }

        static Process Start(NaturalVoice v, string output)
        {
            var psi = new ProcessStartInfo(PiperExe,
                "--model \"" + ModelFile(v) + "\" " + output + (Slow ? " --length_scale 1.2" : ""));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.WorkingDirectory = System.IO.Path.GetDirectoryName(PiperExe);
            var p = Process.Start(psi);
            p.ErrorDataReceived += delegate { }; // drain its log so it never blocks
            p.BeginErrorReadLine();
            return p;
        }

        // For the automatic checks: write `text` said by voice `id` to `wav`, downloading
        // the voice first if needed. Returns 0 when the file was made.
        public static int SelfTest(string id, string text, string wav)
        {
            NaturalVoice v = Find(id);
            if (v == null) { Console.Error.WriteLine("no such voice " + id); return 2; }
            if (!IsInstalled(v))
            {
                string err = Download(v, delegate (int p) { });
                if (err != null) { Console.Error.WriteLine("download failed: " + err); return 3; }
            }
            Slow = false;
            string made = Synthesize(v, text);
            if (made == null) { Console.Error.WriteLine("no audio"); return 4; }
            File.Copy(made, wav, true);
            lock (piperLock) { if (piper != null && !piper.HasExited) try { piper.Kill(); } catch (Exception) { } }
            return 0;
        }
    }
}
