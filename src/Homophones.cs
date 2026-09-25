// Homophones: words that sound the same (there/their/they're). When one of them
// is suggested, the others join the list with a short meaning each, and the word
// typed just before nudges which one comes first.
//
// Written for C# 5 so the csc.exe that ships inside Windows can compile it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SoundSpell
{
    public sealed class Suggestion
    {
        public string Word;
        public string Meaning;   // null when there is nothing to tell apart
        public bool AddToMyWords; // the "add this word to My words" row, not a replacement
        public Suggestion(string word, string meaning) { Word = word; Meaning = meaning; }
        public override string ToString() { return Meaning == null ? Word : Word + "  (" + Meaning + ")"; }
    }

    public sealed class Homophones
    {
        sealed class Member
        {
            public string Word, Plain, Meaning;
            public HashSet<string> After = new HashSet<string>(StringComparer.Ordinal);
        }

        // plain word -> its group
        readonly Dictionary<string, List<Member>> groups = new Dictionary<string, List<Member>>(StringComparer.Ordinal);

        static readonly Regex Part = new Regex(@"^\s*([A-Za-z']+):\s*([^\[]+?)\s*(?:\[after:([^\]]*)\])?\s*$");

        public Homophones() { }

        // Lines: word: meaning [after: a, b] | word: meaning | ...
        public Homophones(TextReader r)
        {
            string line;
            while ((line = r.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var group = new List<Member>();
                foreach (string part in line.Split('|'))
                {
                    Match m = Part.Match(part);
                    if (!m.Success) continue;
                    var mem = new Member { Word = m.Groups[1].Value, Meaning = m.Groups[2].Value.Trim() };
                    mem.Plain = Speller.Plain(mem.Word);
                    foreach (string a in m.Groups[3].Value.Split(','))
                    {
                        string p = Speller.Plain(a);
                        if (p.Length > 0) mem.After.Add(p);
                    }
                    group.Add(mem);
                }
                if (group.Count < 2) continue;
                foreach (Member mem in group)
                    if (!groups.ContainsKey(mem.Plain)) groups[mem.Plain] = group;
            }
        }

        public bool Knows(string word) { return groups.ContainsKey(Speller.Plain(word)); }

        // Adds the sound-alike words next to any suggestion that has them, with meanings.
        // `typed` and `previous` are plain (lower case, no apostrophes).
        public List<Suggestion> Expand(List<Suggestion> list, string typed, string previous, int max)
        {
            var output = new List<Suggestion>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Suggestion s in list)
            {
                string plain = Speller.Plain(s.Word);
                if (seen.Contains(plain)) continue;
                List<Member> group;
                if (!groups.TryGetValue(plain, out group))
                {
                    seen.Add(plain);
                    output.Add(s);
                    continue;
                }
                // The whole group goes here, in the best order for what came before.
                var ordered = new List<Member>(group);
                ordered.Sort(delegate (Member x, Member y)
                {
                    int c = Rank(y, typed, previous, plain).CompareTo(Rank(x, typed, previous, plain));
                    return c != 0 ? c : group.IndexOf(x).CompareTo(group.IndexOf(y));
                });
                foreach (Member mem in ordered)
                {
                    if (seen.Contains(mem.Plain)) continue;
                    seen.Add(mem.Plain);
                    output.Add(new Suggestion(mem.Word, mem.Meaning));
                }
            }
            if (output.Count > max) output.RemoveRange(max, output.Count - max);
            return output;
        }

        static int Rank(Member m, string typed, string previous, string suggested)
        {
            int r = 0;
            if (previous.Length > 0 && m.After.Contains(previous)) r += 4;  // fits after the word before
            if (m.Plain == typed) r += 2;                                  // typed exactly this (youre -> you're)
            if (m.Plain == suggested) r += 1;                              // the one the speller liked best
            return r;
        }
    }
}
