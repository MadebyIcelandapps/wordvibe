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
        var sp = new Speller(new StreamReader(args[0]));
        Console.WriteLine("loaded " + sp.Count + " words in " + sw.ElapsedMilliseconds + " ms");
        string[,] cases = {
            {"nesesary","necessary"},{"definatly","definitely"},{"becuz","because"},{"restront","restaurant"},
            {"sykology","psychology"},{"fenomenon","phenomenon"},{"tomoro","tomorrow"},{"seperate","separate"},
            {"enuf","enough"},{"nite","night"},{"thru","through"},{"wensday","Wednesday"},{"kwik","quick"},
            {"fone","phone"},{"shure","sure"},{"ocashun","occasion"},{"reseet","receipt"},{"cof","cough"},
            {"biznes","business"},{"choklit","chocolate"},{"lisen","listen"},{"naber","neighbor"},{"rithm","rhythm"},
            {"jiraf","giraffe"},{"kolij","college"},{"serprize","surprise"},{"beleev","believe"},{"frend","friend"},
            {"weerd","weird"},{"akomodate","accommodate"},{"enjineer","engineer"},{"eksperiens","experience"},
            {"orkestra","orchestra"},{"nolij","knowledge"},{"rong","wrong"},{"serkul","circle"},{"animul","animal"},
            {"peepul","people"},{"wot","what"},{"skool","school"},{"sertin","certain"},{"langwij","language"},
            {"sumthing","something"},{"probly","probably"},{"diffrent","different"},
            {"acshully","actually"},{"reely","really"},{"bilding","building"},{"gess","guess"},{"yoosual","usual"},
            {"hed","head"},{"minit","minute"},{"sientist","scientist"},{"nee","knee"},{"lafing","laughing"},
            {"wether","weather"},{"imajin","imagine"},{"favrit","favorite"},
            // dyslexic spellings: flipped letters, swapped letters, dropped letters
            {"freind","friend"},{"wierd","weird"},{"becuase","because"},{"hte","the"},{"wuz","was"},{"sed","said"},
            {"cud","could"},{"shud","should"},{"wud","would"},{"tawk","talk"},{"brother","brother"},{"dady","daddy"},
            {"bady","baby"},{"porblem","problem"},{"diffrint","different"},{"aftr","after"},{"libary","library"},
            {"gril","girl"},{"wnet","went"},{"sumtimes","sometimes"},{"evry","every"},{"ansur","answer"},{"Wensday","Wednesday"},{"THRU","THROUGH"},
        };
        int ok = 0, top3 = 0, n = cases.GetLength(0);
        sw.Restart();
        for (int i = 0; i < n; i++)
        {
            var s = sp.Suggest(cases[i, 0], 5);
            bool hit = s.Count > 0 && s[0] == cases[i, 1];
            bool in3 = s.IndexOf(cases[i, 1]) >= 0 && s.IndexOf(cases[i, 1]) < 3;
            if (hit) ok++; if (in3) top3++;
            if (!hit) Console.WriteLine((in3 ? "  ~ " : "  X ") + cases[i, 0] + " -> " + string.Join(", ", s) + "   (want " + cases[i, 1] + ")");
        }
        Console.WriteLine("first choice " + ok + "/" + n + ", top 3 " + top3 + "/" + n + ", " + (sw.ElapsedMilliseconds / n) + " ms per word");
        return top3 * 10 >= n * 9 ? 0 : 1;
    }
}
