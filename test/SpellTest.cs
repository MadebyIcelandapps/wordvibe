// Checks the speller against sound-it-out spellings. Run: test/run.sh
using System;
using System.Diagnostics;
using System.IO;
using SoundSpell;

static class SpellTest
{
    static int Main(string[] args)
    {
        var sw = Stopwatch.StartNew();
        string dir = Path.GetDirectoryName(Path.GetFullPath(args[0]));
        var sp = Speller.Build(new StreamReader(args[0]), new StreamReader(Path.Combine(dir, "irish.txt")), null,
                               new StreamReader(Path.Combine(dir, "homophones.txt")));
        Console.WriteLine("loaded " + sp.Count + " words in " + sw.ElapsedMilliseconds + " ms");
        string[,] cases = {
            {"nesesary","necessary"},{"definatly","definitely"},{"becuz","because"},{"restront","restaurant"},
            {"sykology","psychology"},{"fenomenon","phenomenon"},{"tomoro","tomorrow"},{"seperate","separate"},
            {"enuf","enough"},{"nite","night"},{"thru","through"},{"wensday","Wednesday"},{"kwik","quick"},
            {"fone","phone"},{"shure","sure"},{"ocashun","occasion"},{"reseet","receipt"},{"cof","cough"},
            {"biznes","business"},{"choklit","chocolate"},{"lisen","listen"},{"naber","neighbour|neighbor"},{"rithm","rhythm"},
            {"jiraf","giraffe"},{"kolij","college"},{"serprize","surprise"},{"beleev","believe"},{"frend","friend"},
            {"weerd","weird"},{"akomodate","accommodate"},{"enjineer","engineer"},{"eksperiens","experience"},
            {"orkestra","orchestra"},{"nolij","knowledge"},{"rong","wrong"},{"serkul","circle"},{"animul","animal"},
            {"peepul","people"},{"wot","what"},{"skool","school"},{"sertin","certain"},{"langwij","language"},
            {"sumthing","something"},{"probly","probably"},{"diffrent","different"},
            {"acshully","actually"},{"reely","really"},{"bilding","building"},{"gess","guess"},{"yoosual","usual"},
            {"hed","head"},{"minit","minute"},{"sientist","scientist"},{"nee","knee"},{"lafing","laughing"},
            {"wether","weather"},{"imajin","imagine"},
            // dyslexic spellings: flipped letters, swapped letters, dropped letters
            {"freind","friend"},{"wierd","weird"},{"becuase","because"},{"hte","the"},{"wuz","was"},{"sed","said"},
            {"cud","could"},{"shud","should"},{"wud","would"},{"tawk","talk"},{"brother","brother"},{"dady","daddy"},
            {"bady","baby"},{"porblem","problem"},{"diffrint","different"},{"aftr","after"},{"libary","library"},
            {"gril","girl"},{"wnet","went"},{"sumtimes","sometimes"},{"evry","every"},{"ansur","answer"},
            // Irish and British spellings, Irish names and places, the Irish "th"
            {"culer","colour|color"},{"favrit","favourite|favorite"},{"realyse","realise|realize"},{"senter","centre|center"},{"theeater","theatre|theater"},
            {"neev","Niamh"},{"shivawn","Siobhán"},{"eefa","Aoife"},{"keeva","Caoimhe"},{"seersha","Saoirse"},
            {"osheen","Oisín"},{"keeran","Ciarán"},{"shawn","Seán"},{"rosheen","Róisín"},{"teeshock","Taoiseach"},
            {"droheda","Drogheda"},{"leesh","Laois"},{"gardee","Gardaí"},{"slawncha","Sláinte"},{"tink","think"},
            {"tanks","thanks"},{"Wensday","Wednesday"},{"THRU","THROUGH"},
        };
        int ok = 0, top3 = 0, n = cases.GetLength(0);
        sw.Restart();
        for (int i = 0; i < n; i++)
        {
            var s = sp.Suggest(cases[i, 0], 5);
            string[] want = cases[i, 1].Split('|');
            bool hit = s.Count > 0 && Array.IndexOf(want, s[0]) >= 0;
            bool in3 = false;
            for (int j = 0; j < s.Count && j < 3; j++) if (Array.IndexOf(want, s[j]) >= 0) in3 = true;
            if (hit) ok++; if (in3) top3++;
            if (!hit) Console.WriteLine((in3 ? "  ~ " : "  X ") + cases[i, 0] + " -> " + string.Join(", ", s) + "   (want " + cases[i, 1] + ")");
        }
        Console.WriteLine("first choice " + ok + "/" + n + ", top 3 " + top3 + "/" + n + ", " + (sw.ElapsedMilliseconds / n) + " ms per word");
        bool good = top3 * 10 >= n * 9;

        // Words that sound alike: the word before decides the order, meanings come along.
        good &= Expect("there after 'of'", First(sp.SuggestFull("there", 5, "of")), "their");
        good &= Expect("there after 'over'", First(sp.SuggestFull("there", 5, "over")), "there");
        good &= Expect("thair after 'love'", First(sp.SuggestFull("thair", 5, "love")), "their");
        good &= Expect("youre", First(sp.SuggestFull("youre", 5, null)), "you're");
        // Contractions typed without the apostrophe.
        good &= Expect("im", First(sp.SuggestFull("im", 5, null)), "I'm");
        good &= Expect("Im", First(sp.SuggestFull("Im", 5, null)), "I'm");
        good &= Expect("dont", First(sp.SuggestFull("dont", 5, null)), "don't");
        good &= Expect("cant", First(sp.SuggestFull("cant", 5, null)), "can't");
        good &= Expect("ive", First(sp.SuggestFull("ive", 5, null)), "I've");
        good &= Expect("ill offers I'll second", sp.SuggestFull("ill", 5, null)[1].Word, "I'll");
        good &= Expect("its offers it's second", sp.SuggestFull("its", 5, null)[1].Word, "it's");
        foreach (Suggestion s in sp.SuggestFull("cant", 8, null))
            good &= Expect("no slur for cant", s.Word == "cunt" ? "slur" : "ok", "ok");
        good &= Expect("wether after 'the'", First(sp.SuggestFull("wether", 5, "the")), "weather");
        good &= Expect("meaning shown", sp.SuggestFull("there", 5, null)[0].Meaning != null ? "yes" : "no", "yes");

        // Right or wrong: both US and UK spellings count, and contractions.
        foreach (string real in new[] { "colour", "color", "favourite", "favorite", "realise", "realize", "don't", "they're", "Niamh", "Siobhán", "Wednesday", "Ireland's" })
            good &= Expect("is a word: " + real, sp.IsWord(real) ? "yes" : "no", "yes");
        foreach (string bad in new[] { "nesesary", "wensday", "freind", "dont'", "teh" })
            good &= Expect("not a word: " + bad, sp.IsWord(bad) ? "yes" : "no", "no");

        // Learning from picks: what she chose before comes first next time.
        good &= Expect("gril before learning", sp.Suggest("gril", 5)[0], "grill");
        sp.Learn("gril", "girl"); sp.Learn("gril", "girl");
        good &= Expect("gril after picking girl twice", sp.Suggest("gril", 5)[0], "girl");
        sp.Learn("frend", "frend"); sp.Learn("frend", "frend");
        good &= Expect("keeping her own spelling twice", sp.Suggest("frend", 5)[0], "frend");
        var saved = new StringWriter(); sp.SavePicks(saved);
        var sp2 = Speller.Build(new StreamReader(args[0]), new StreamReader(Path.Combine(dir, "irish.txt")), null, null);
        sp2.LoadPicks(new StringReader(saved.ToString()));
        good &= Expect("picks survive a restart", sp2.Suggest("gril", 5)[0], "girl");

        return good ? 0 : 1;
    }

    static string First(System.Collections.Generic.List<Suggestion> l) { return l.Count > 0 ? l[0].Word : "(none)"; }

    static bool Expect(string what, string got, string want)
    {
        bool ok = got == want;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + ": " + got + (ok ? "" : "   (want " + want + ")"));
        return ok;
    }
}
