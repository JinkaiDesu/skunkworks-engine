// The host's input journal without Unity: the Unity project's own InputProgram.cs, compiled here and fed
// the way InputEvents feeds it in the player.
//
//   SkunkworksHost selftest     check the journal's behavior
using System;
using System.Collections.Generic;

internal static class Host
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "selftest") return SelfTest();
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.Message); return 1; }
        Console.Error.WriteLine("usage: SkunkworksHost selftest");
        return 2;
    }

    private static int passed;
    private static void Test(string name, Action body) { body(); passed++; Console.Out.WriteLine("PASS " + name); }
    private static void Require(bool okay, string message) { if (!okay) throw new InvalidOperationException(message); }

    private static void Press(InputProgram journal, string key) => journal.Post(new InputSample(InputKind.KeyDown, 0, 1, key));
    private static List<InputSample> After(InputProgram journal, long sequence, int maximum = 4096)
    {
        var samples = new List<InputSample>();
        int count = journal.Read(sequence, samples, maximum);
        Require(count == samples.Count, "Read reported " + count + " samples and gave " + samples.Count);
        return samples;
    }
    private static string Keys(List<InputSample> samples)
    {
        var names = new List<string>();
        foreach (InputSample sample in samples) names.Add(sample.Sequence + ":" + sample.Control);
        return string.Join(" ", names);
    }

    private static int SelfTest()
    {
        Test("samples are numbered in the order they happened", () =>
        {
            var journal = new InputProgram();
            Require(journal.Sequence == 0 && After(journal, 0).Count == 0, "a new journal is not empty");
            foreach (string key in new[] { "a", "b", "c" }) Press(journal, key);
            Require(Keys(After(journal, 0)) == "1:a 2:b 3:c", "read back: " + Keys(After(journal, 0)));
            Require(journal.Sequence == 3, "sequence: " + journal.Sequence);
        });

        Test("a reader gets only what came after the sequence it names", () =>
        {
            var journal = new InputProgram();
            foreach (string key in new[] { "a", "b", "c" }) Press(journal, key);
            Require(Keys(After(journal, 2)) == "3:c", "after 2: " + Keys(After(journal, 2)));
            Require(After(journal, 3).Count == 0, "a reader that is up to date was given samples");
            Press(journal, "d");
            Require(Keys(After(journal, 3)) == "4:d", "after 3: " + Keys(After(journal, 3)));
        });

        Test("a read stops at the count the reader asks for and resumes from there", () =>
        {
            var journal = new InputProgram();
            foreach (string key in new[] { "a", "b", "c", "d", "e" }) Press(journal, key);
            List<InputSample> first = After(journal, 0, 2);
            Require(Keys(first) == "1:a 2:b", "first two: " + Keys(first));
            List<InputSample> rest = After(journal, first[first.Count - 1].Sequence, 2);
            Require(Keys(rest) == "3:c 4:d", "next two: " + Keys(rest));
        });

        Test("reading takes nothing from another reader", () =>
        {
            var journal = new InputProgram();
            foreach (string key in new[] { "a", "b" }) Press(journal, key);
            string one = Keys(After(journal, 0)), two = Keys(After(journal, 0));
            Require(one == "1:a 2:b" && two == one, "readers saw '" + one + "' and '" + two + "'");
        });

        Test("the journal is bounded and a reader can tell it fell behind", () =>
        {
            var journal = new InputProgram(8);
            for (int n = 1; n <= 5; n++) Press(journal, "k" + n);
            long seen = journal.Sequence;
            Require(journal.OldestSequence == 1 && journal.HistoryEvictions == 0, "evicted before the journal was full");
            for (int n = 6; n <= 20; n++) Press(journal, "k" + n);
            List<InputSample> kept = After(journal, 0);
            Require(kept.Count == 8 && kept[0].Sequence == 13 && kept[7].Sequence == 20, "kept: " + Keys(kept));
            Require(journal.OldestSequence == 13 && journal.HistoryEvictions == 12,
                "oldest " + journal.OldestSequence + ", evicted " + journal.HistoryEvictions);
            Require(journal.OldestSequence > seen + 1, "a reader at " + seen + " cannot see that it lost samples");
            Require(Keys(After(journal, seen)) == Keys(kept), "a reader that fell behind is not given what remains");
        });

        Test("text arrives as whole code points", () =>
        {
            var text = new InputTextAccumulator();
            Require(text.Push('a') == "a", "a plain character changed");
            Require(text.Push('\uD83D') == "" && text.Push('\uDE00') == "😀", "a surrogate pair was split");
            Require(text.Push('\uDE00') == "�", "a lone low surrogate passed through");
            Require(text.Push('\uD83D') == "" && text.Push('x') == "�x", "a lone high surrogate passed through");
            Require(text.Push('\uD83D') == "" && text.Push('\uD83D') == "�" && text.Push('\uDE00') == "😀",
                "a repeated high surrogate was not replaced");
            Require(text.Push('\uD83D') == "", "a high surrogate was emitted early");
            text.Clear();
            Require(text.Push('\uDE00') == "�", "a cleared decoder kept half a pair");
        });

        Test("focus and resets are facts in the stream", () =>
        {
            var journal = new InputProgram();
            journal.Post(new InputSample(InputKind.Focus, 1, focused: true));
            Require(journal.Focused, "the journal did not take focus");
            Press(journal, "a");
            journal.Reset(2, "focus lost");
            journal.Post(new InputSample(InputKind.Focus, 2, focused: false));
            Require(!journal.Focused, "the journal kept focus");
            List<InputSample> samples = After(journal, 0);
            Require(samples.Count == 4, "samples: " + samples.Count);
            Require(samples[2].Kind == InputKind.Reset && samples[2].Text == "focus lost" && samples[2].Sequence == 3,
                "the reset is not in the stream where it happened");
            Require(samples[3].Kind == InputKind.Focus && !samples[3].Focused, "the focus loss is not in the stream");
        });

        Console.Out.WriteLine("SKUNKWORKS_HOST_OK tests=" + passed);
        return 0;
    }
}
