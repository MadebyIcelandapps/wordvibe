// Speller: finds the word someone meant when they spelled it the way it sounds.
//
// Every dictionary word gets two sound keys (a primary and an alternate reading
// for letters that can go either way, like "ch" in chair/choir or "gh" in
// night/enough). A typed word is scored against each dictionary word by how far
// apart the sound keys are, how far apart the spellings are, and how common the
// word is. The lowest score wins.
//
// Written for C# 5 so the csc.exe that ships inside Windows can compile it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SoundSpell
{
    public sealed class Speller
    {
        private readonly string[] words;     // lower case, fadas and accents removed, most common first
        private readonly string[] display;   // as written in the list (Wednesday, Siobhán)
        private readonly string[][] keys;    // the distinct sound keys of each word
        private readonly string[] saidKey;   // for words with a said-as spelling, the key of how they are said
        private readonly string[] saidAs;    // ...and that spelling itself
        private readonly Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);

        // What she picked before for a typed word: typed -> (word -> times).
        private readonly Dictionary<string, Dictionary<string, int>> picks =
            new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        public Homophones Homophones = new Homophones();

        // Words whose spelling hides how they sound, written the way they are said.
        private static readonly string[] SaidAs = {
            "wednesday=wensday", "receipt=reseet", "cough=kof", "colonel=kernel", "choir=kwire",
            "island=iland", "yacht=yot", "sword=sord", "answer=anser", "subtle=suttle",
            "debt=det", "doubt=dowt", "honest=onest", "hour=our", "honour=onner", "honor=onner",
            "one=wun", "once=wunce", "two=too", "women=wimmin", "busy=bizzy", "business=bizness",
            "enough=enuf", "tough=tuf", "rough=ruf", "laugh=laf", "through=throo", "though=tho",
            "thought=thot", "bought=bot", "caught=cot", "daughter=dotter", "light=lite", "night=nite",
            "right=rite", "tonight=tonite", "friend=frend", "people=peepul", "says=sez", "said=sed",
            "of=ov", "was=woz", "what=wot", "sure=shure", "sugar=shugar", "ocean=oshun",
            "queue=kyoo", "view=vyoo", "beautiful=byootiful", "because=becuz", "does=duz",
            "leopard=lepperd", "restaurant=restront", "chocolate=choklit", "vegetable=vejtabul",
            "comfortable=kumftabul", "probably=probly", "interesting=intresting", "different=diffrent",
            "every=evry", "family=famly", "camera=camra", "separate=seprit", "favorite=favrit",
            "favourite=favrit", "temperature=temprachur", "library=libary", "february=febuary",
            "government=guvment", "environment=enviroment", "psychology=sykology", "knowledge=nolij",
            "rhythm=rithum", "tomb=toom", "wrap=rap", "calm=kahm", "walk=wok", "talk=tok", "half=haf",
            "listen=lissen", "castle=kassel", "whistle=wissel", "science=syence", "scissors=sizzors",
            "phlegm=flem", "pneumonia=noomonia", "aisle=ile", "height=hite", "weird=weerd",
            "ache=ake", "stomach=stumak", "character=karakter", "christmas=krismas", "schedule=skejool",
            "technique=tekneek", "unique=yooneek", "antique=anteek", "guitar=gitar", "guess=gess",
            "guest=gest", "tongue=tung", "league=leeg", "juice=joos", "fruit=froot", "build=bild",
            "minute=minit", "lettuce=lettis", "machine=masheen", "genre=zhonra", "iron=iyern",
            "eye=i", "buy=bi", "bye=bi", "quay=kee", "gauge=gayj", "bureau=byooro", "sergeant=sarjent",
            "anemone=anemony", "epitome=epitomy", "recipe=ressipy", "catastrophe=katastrofy",
        };

        // Each line: a word, optionally followed by "=how it sounds" (Niamh=neev).
        // Lines starting with # are skipped.
        public Speller(TextReader wordList)
        {
            var list = new List<string>();
            var shown = new List<string>();
            var extra = new Dictionary<int, string>();
            string line;
            while ((line = wordList.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string said = null;
                int eq = line.IndexOf('=');
                if (eq > 0) { said = Plain(line.Substring(eq + 1)); line = line.Substring(0, eq).Trim(); }
                string plain = Plain(line);
                if (plain.Length == 0) continue;
                int at;
                if (!index.TryGetValue(plain, out at))
                {
                    at = list.Count;
                    index[plain] = at;
                    list.Add(plain);
                    shown.Add(line);
                }
                if (!string.IsNullOrEmpty(said) && !extra.ContainsKey(at)) extra[at] = said;
            }
            words = list.ToArray();
            display = shown.ToArray();

            foreach (string pair in SaidAs)
            {
                int eq = pair.IndexOf('=');
                int at;
                if (index.TryGetValue(pair.Substring(0, eq), out at) && !extra.ContainsKey(at)) extra[at] = pair.Substring(eq + 1);
            }

            keys = new string[words.Length][];
            saidKey = new string[words.Length];
            saidAs = new string[words.Length];
            var ks = new List<string>(4);
            for (int i = 0; i < words.Length; i++)
            {
                ks.Clear();
                AddKey(ks, SoundKey(words[i], false));
                AddKey(ks, SoundKey(words[i], true));
                string said;
                if (extra.TryGetValue(i, out said))
                {
                    saidAs[i] = said;
                    saidKey[i] = SoundKey(said, false);
                    AddKey(ks, saidKey[i]);
                    AddKey(ks, SoundKey(said, true));
                }
                keys[i] = ks.ToArray();
            }
        }

        // The full dictionary: her own words first, then everyday words, with Irish
        // names and places slotted in after the most common 8000.
        public static Speller Build(TextReader everyday, TextReader irish, TextReader mine, TextReader homophones)
        {
            var all = new StringWriter();
            if (mine != null) all.Write(mine.ReadToEnd() + "\n");
            int n = 0;
            string line;
            bool irishDone = irish == null;
            while ((line = everyday.ReadLine()) != null)
            {
                all.Write(line + "\n");
                if (++n == 8000 && !irishDone) { all.Write(irish.ReadToEnd() + "\n"); irishDone = true; }
            }
            if (!irishDone) all.Write(irish.ReadToEnd() + "\n");
            var sp = new Speller(new StringReader(all.ToString()));
            if (homophones != null) sp.Homophones = new Homophones(homophones);
            return sp;
        }

        private static void AddKey(List<string> ks, string k) { if (k.Length > 0 && !ks.Contains(k)) ks.Add(k); }

        public int Count { get { return words.Length; } }

        // Lower case, without fadas or other accents, without apostrophes: "Siobhán" -> "siobhan".
        public static string Plain(string s)
        {
            string d = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char c in d)
            {
                if (c == '\'' || c == '\u2019') continue;
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ---- learning from her picks -----------------------------------------

        // She picked `chosen` for `typed` (chosen == typed means "keep my spelling").
        public void Learn(string typed, string chosen)
        {
            string t = Plain(typed);
            if (t.Length == 0 || chosen.Length == 0) return;
            Dictionary<string, int> m;
            if (!picks.TryGetValue(t, out m)) { m = new Dictionary<string, int>(StringComparer.Ordinal); picks[t] = m; }
            int n;
            m.TryGetValue(chosen, out n);
            m[chosen] = n + 1;
        }

        // Lines: typed<TAB>chosen<TAB>times
        public void LoadPicks(TextReader r)
        {
            string line;
            while ((line = r.ReadLine()) != null)
            {
                string[] f = line.Split('\t');
                int n;
                if (f.Length != 3 || !int.TryParse(f[2], out n)) continue;
                for (int i = 0; i < n && i < 20; i++) Learn(f[0], f[1]);
            }
        }

        public void SavePicks(TextWriter w)
        {
            foreach (var t in picks)
                foreach (var c in t.Value)
                    w.WriteLine(t.Key + "\t" + c.Key + "\t" + c.Value);
        }

        // ---- suggestions ------------------------------------------------------

        // Best matches first, with the typed word's capitalisation carried over.
        public List<string> Suggest(string typed, int max)
        {
            var result = new List<string>();
            foreach (Suggestion s in SuggestFull(typed, max, null)) result.Add(s.Word);
            return result;
        }

        // Matches with meanings for words that sound alike. `previous` is the word
        // typed just before, used to guess between there/their/they're and friends.
        public List<Suggestion> SuggestFull(string typed, int max, string previous)
        {
            var result = new List<Suggestion>();
            if (string.IsNullOrEmpty(typed)) return result;
            string w = Plain(typed);
            if (w.Length == 0) return result;

            string ta = SoundKey(w, false), tb = SoundKey(w, true);
            if (tb == ta) tb = null;
            var best = new List<KeyValuePair<double, int>>();
            double worstKept = double.MaxValue;
            int lenSlack = Math.Max(3, w.Length / 2 + 1);

            Dictionary<string, int> before;
            picks.TryGetValue(w, out before);

            for (int i = 0; i < words.Length; i++)
            {
                string cand = words[i];
                int picked = 0;
                if (before != null) before.TryGetValue(display[i], out picked);
                if (picked == 0 && Math.Abs(cand.Length - w.Length) > lenSlack + 2) continue;

                double dk = double.MaxValue;
                foreach (string k in keys[i])
                {
                    if (Math.Abs(k.Length - ta.Length) <= 3) dk = Math.Min(dk, KeyDistance(ta, k));
                    if (tb != null && Math.Abs(k.Length - tb.Length) <= 3) dk = Math.Min(dk, KeyDistance(tb, k));
                }
                if (picked == 0 && dk > 2.5) continue;
                if (dk == double.MaxValue) dk = 3;
                double freq = Math.Log(i + 2) * 0.2;
                double pickBonus = 1.5 * Math.Min(picked, 3);
                if (dk * 2.5 + freq - pickBonus >= worstKept) continue;

                double ds = SpellDistance(w, cand) / Math.Max(w.Length, cand.Length);
                double firstLetter = w[0] == cand[0] ? 0 : 0.3;
                double score = dk * 2.5 + ds * 1.5 + freq + firstLetter - pickBonus;
                // Spelled (nearly) exactly as the word is said: wensday, neev, teeshock.
                if (saidAs[i] != null && SpellDistance(w, saidAs[i]) <= (w.Length >= 3 ? 1 : 0)) score -= 1.0;
                if (cand == w) score -= 0.5; // a real but rare word may still not be the one meant

                if (score < worstKept)
                {
                    best.Add(new KeyValuePair<double, int>(score, i));
                    best.Sort((x, y) => x.Key.CompareTo(y.Key));
                    if (best.Count > max) best.RemoveAt(best.Count - 1);
                    worstKept = best.Count >= max ? best[best.Count - 1].Key : double.MaxValue;
                }
            }

            foreach (var kv in best) result.Add(new Suggestion(display[kv.Value], null));

            // Her own spelling, if she kept it twice, goes first.
            int kept;
            if (before != null && before.TryGetValue(typed, out kept) && kept >= 2 && !Has(result, typed))
                result.Insert(0, new Suggestion(typed, "your spelling"));
            else if (before != null && before.TryGetValue(typed, out kept) && kept == 1 && !Has(result, typed))
                result.Add(new Suggestion(typed, "your spelling"));

            result = Homophones.Expand(result, w, Plain(previous ?? ""), max + 2);

            foreach (Suggestion s in result) s.Word = MatchCase(typed, s.Word);
            return result;
        }

        private static bool Has(List<Suggestion> list, string word)
        {
            foreach (Suggestion s in list) if (string.Equals(s.Word, word, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string MatchCase(string typed, string word)
        {
            int letters = 0, upper = 0;
            foreach (char c in typed) if (char.IsLetter(c)) { letters++; if (char.IsUpper(c)) upper++; }
            if (letters > 1 && upper == letters) return word.ToUpperInvariant();
            if (typed.Length > 0 && char.IsUpper(typed[0]) && word.Length > 0) return char.ToUpperInvariant(word[0]) + word.Substring(1);
            return word;
        }

        // ---- sound key -----------------------------------------------------
        // Letters become sound codes: A = any vowel sound, X = "sh", 0 = "th",
        // J = soft g / "dge", K = hard c/k/q, F = f/ph, V = v, S = s/z/soft c, T = t/d.

        private static bool IsVowel(char c) { return c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u' || c == 'y'; }

        public static string SoundKey(string w, bool alt)
        {
            w = Plain(w);
            if (w.Length == 0) return "";
            // Silent letters at the start.
            if (w.StartsWith("kn", StringComparison.Ordinal) || w.StartsWith("gn", StringComparison.Ordinal) || w.StartsWith("pn", StringComparison.Ordinal) || w.StartsWith("wr", StringComparison.Ordinal) || w.StartsWith("ps", StringComparison.Ordinal))
                w = w.Substring(1);
            else if (w.StartsWith("wh", StringComparison.Ordinal)) w = "w" + w.Substring(2);
            else if (w.StartsWith("x", StringComparison.Ordinal)) w = "s" + w.Substring(1);
            else if (w.StartsWith("rh", StringComparison.Ordinal)) w = "r" + w.Substring(2);
            if (w.EndsWith("mb", StringComparison.Ordinal)) w = w.Substring(0, w.Length - 1);

            var k = new StringBuilder();
            int n = w.Length;
            for (int i = 0; i < n; i++)
            {
                char c = w[i];
                char next = i + 1 < n ? w[i + 1] : '\0';
                char next2 = i + 2 < n ? w[i + 2] : '\0';
                char prev = i > 0 ? w[i - 1] : '\0';

                // Double letters sound like one ("cc" before e/i is "ks", as in accent).
                if (c == prev && c != 'c') continue;

                switch (c)
                {
                    case 'e':
                        if (i == n - 1 && n > 3 && !IsVowel(prev)) break; // silent e: make, have
                        Add(k, 'A'); break;
                    case 'a': case 'i': case 'o': case 'u':
                        Add(k, 'A'); break;
                    case 'y':
                        if (IsVowel(next) && i == 0) Add(k, 'Y'); else Add(k, 'A');
                        break;
                    case 'b': Add(k, 'B'); break;
                    case 'c':
                        if (next == 'h')
                        {
                            if (prev == 's') { Add(k, 'K'); } // school
                            else if (next2 == 'r' || next2 == 'l') Add(k, 'K'); // chrome, chlorine
                            else Add(k, alt ? 'K' : 'X'); // chair / choir
                            i++;
                        }
                        else if (next == 'i' && (next2 == 'a' || next2 == 'o')) { Add(k, 'X'); } // special, precious
                        else if (next == 'e' || next == 'i' || next == 'y')
                        {
                            if (prev == 's') break;       // science: the s already made the sound
                            if (prev == 'c') { Add(k, 'K'); } // accent: first c is k (handled here as k then s)
                            Add(k, 'S');
                        }
                        else if (next == 'k' || next == 'q') { Add(k, 'K'); i++; }
                        else if (prev == 'c') { } // "cc" before a/o/u: one k
                        else Add(k, 'K');
                        break;
                    case 'd':
                        if (next == 'g' && (next2 == 'e' || next2 == 'i' || next2 == 'y')) { Add(k, 'J'); i++; }
                        else Add(k, 'T');
                        break;
                    case 'f': Add(k, 'F'); break;
                    case 'g':
                        if (next == 'h')
                        {
                            if (i == 0) { Add(k, 'K'); i++; }        // ghost
                            else if (IsVowel(prev)) { if (alt) Add(k, 'F'); i++; } // night / enough
                            else { Add(k, 'K'); i++; }
                        }
                        else if (next == 'n' && (i + 2 == n || (next2 == 'e' && i + 3 == n))) { } // sign, campaign
                        else if (next == 'e' || next == 'i' || next == 'y') Add(k, alt ? 'K' : 'J');
                        else Add(k, 'K');
                        break;
                    case 'h':
                        if (IsVowel(next) && !IsVowel(prev) || i == 0) Add(k, 'H');
                        break;
                    case 'j': Add(k, 'J'); break;
                    case 'k': Add(k, 'K'); break;
                    case 'l':
                        // could, should, would; talk, walk; calm, palm; folk
                        if (prev == 'u' && i >= 2 && w[i - 2] == 'o' && next == 'd') break;
                        if ((prev == 'a' || prev == 'o') && (next == 'k' || next == 'm') && i + 2 >= n - 1) break;
                        Add(k, 'L'); break;
                    case 'm': Add(k, 'M'); break;
                    case 'n': Add(k, 'N'); break;
                    case 'p':
                        if (next == 'h') { Add(k, 'F'); i++; } else Add(k, 'P');
                        break;
                    case 'q':
                        Add(k, 'K');
                        if (next == 'u') { Add(k, 'W'); i++; }
                        break;
                    case 'r':
                        if (next == 'e' && i + 2 == n && i > 0 && !IsVowel(prev)) { Add(k, 'A'); Add(k, 'R'); i++; break; } // centre, theatre
                        Add(k, 'R'); break;
                    case 's':
                        if (next == 'h') { Add(k, 'X'); i++; }
                        else if (next == 'i' && (next2 == 'o' || next2 == 'a')) Add(k, 'X'); // mission, asia
                        else if (next == 'u' && (next2 == 'r' || next2 == 'g')) Add(k, alt ? 'X' : 'S'); // sure, measure
                        else if (next == 'c' && next2 == 'h') { Add(k, 'S'); Add(k, 'K'); i += 2; }
                        else Add(k, 'S');
                        break;
                    case 't':
                        if (next == 'i' && (next2 == 'o' || next2 == 'a')) Add(k, 'X'); // nation
                        else if (next == 'h') { Add(k, '0'); i++; }
                        else if ((prev == 's' || prev == 'f') && (next == 'e' && next2 == 'n' || next == 'l' && next2 == 'e') && i + 3 == n) { } // listen, often, castle
                        else if (next == 'c' && next2 == 'h') { Add(k, 'X'); i += 2; }
                        else if (next == 'u' && next2 == 'r' && i > 0) Add(k, alt ? 'X' : 'T'); // picture
                        else Add(k, 'T');
                        break;
                    case 'v': Add(k, 'V'); break;
                    case 'w':
                        if (IsVowel(next) || next == 'h') Add(k, 'W');
                        else if (!IsVowel(prev)) Add(k, 'W');
                        break;
                    case 'x':
                        Add(k, 'K'); Add(k, 'S');
                        break;
                    case 'z': Add(k, 'S'); break;
                }
            }
            return k.ToString();
        }

        private static void Add(StringBuilder k, char code)
        {
            if (k.Length > 0 && k[k.Length - 1] == code) return;
            k.Append(code);
        }

        // Edit distance between sound keys. Vowels are cheap to add or drop,
        // and sounds people mix up (s/sh, k/g, t/th, f/p) are cheap to swap.
        [ThreadStatic] private static double[] r0, r1, r2;

        private static double KeyDistance(string a, string b)
        {
            int n = a.Length, m = b.Length;
            if (r0 == null || r0.Length < m + 1) { r0 = new double[m + 16]; r1 = new double[m + 16]; r2 = new double[m + 16]; }
            double[] twoBack = r0, prevRow = r1, row = r2;
            prevRow[0] = 0;
            for (int j = 1; j <= m; j++) prevRow[j] = prevRow[j - 1] + InsCost(b[j - 1]);
            for (int i = 1; i <= n; i++)
            {
                char ca = a[i - 1];
                row[0] = prevRow[0] + InsCost(ca);
                for (int j = 1; j <= m; j++)
                {
                    char cb = b[j - 1];
                    double v = prevRow[j - 1] + SubCost(ca, cb);
                    double del = prevRow[j] + InsCost(ca);
                    if (del < v) v = del;
                    double ins = row[j - 1] + InsCost(cb);
                    if (ins < v) v = ins;
                    if (i > 1 && j > 1 && ca == b[j - 2] && a[i - 2] == cb && twoBack[j - 2] + 0.3 < v)
                        v = twoBack[j - 2] + 0.3;
                    row[j] = v;
                }
                double[] t = twoBack; twoBack = prevRow; prevRow = row; row = t;
            }
            return prevRow[m];
        }

        private static readonly double[,] SubTable = BuildSubTable();

        private static double[,] BuildSubTable()
        {
            var t = new double[128, 128];
            for (int a = 0; a < 128; a++) for (int b = 0; b < 128; b++) t[a, b] = SlowSubCost((char)a, (char)b);
            return t;
        }

        private static double SubCost(char a, char b) { return SubTable[a & 127, b & 127]; }

        private static double InsCost(char c)
        {
            if (c == 'A' || c == 'H' || c == 'W' || c == 'Y') return 0.4;
            return 1.0;
        }

        private static double SlowSubCost(char a, char b)
        {
            if (a == b) return 0;
            if (Pair(a, b, 'T', '0')) return 0.2; // "th" said as "t", as in Irish English: tink, tree
            if (Pair(a, b, 'S', 'X') || Pair(a, b, 'K', 'G') || Pair(a, b, 'F', 'P') || Pair(a, b, 'F', 'V')
                || Pair(a, b, 'J', 'X') || Pair(a, b, 'X', 'K') || Pair(a, b, 'J', 'K') || Pair(a, b, 'M', 'N')
                || Pair(a, b, 'S', 'J') || Pair(a, b, 'B', 'P') || Pair(a, b, 'B', 'T') || Pair(a, b, 'W', 'A') || Pair(a, b, 'Y', 'A'))
                return 0.5;
            return 1.0;
        }

        private static bool Pair(char a, char b, char x, char y) { return (a == x && b == y) || (a == y && b == x); }

        // Edit distance on letters, used to break ties between words that sound
        // alike. Swapped neighbours (freind) and letters that are easy to flip
        // when reading or writing with dyslexia (b/d, p/q, m/w, n/u) cost less.
        private static double SpellDistance(string a, string b)
        {
            int n = a.Length, m = b.Length;
            var d = new double[n + 1, m + 1];
            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;
            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    char x = a[i - 1], y = b[j - 1];
                    double cost = x == y ? 0 : Mirrored(x, y) ? 0.4 : 1;
                    double v = Math.Min(d[i - 1, j - 1] + cost, Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1));
                    if (i > 1 && j > 1 && x == b[j - 2] && a[i - 2] == y)
                        v = Math.Min(v, d[i - 2, j - 2] + 0.5);
                    d[i, j] = v;
                }
            }
            return d[n, m];
        }

        private static bool Mirrored(char x, char y)
        {
            return Pair(x, y, 'b', 'd') || Pair(x, y, 'p', 'q') || Pair(x, y, 'b', 'p') || Pair(x, y, 'd', 'q')
                || Pair(x, y, 'm', 'w') || Pair(x, y, 'n', 'u');
        }
    }
}
