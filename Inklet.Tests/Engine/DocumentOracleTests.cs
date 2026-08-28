using System.Text;
using Inklet.Engine;

namespace Inklet.Tests.Engine;

/// <summary>
/// Randomised property tests: every Document operation is mirrored on a plain
/// StringBuilder oracle and full state is compared - text, line geometry, and
/// undo/redo round-trips. Seeds are fixed so failures reproduce.
/// </summary>
[TestClass]
public sealed class DocumentOracleTests
{
    private static readonly string[] InsertSamples =
    [
        "a", "xyz", "\n", "\r\n", "\r", "hello world", "line1\nline2",
        "\r\r", "\n\n", "a\r\nb", "tab\tend", "😀", "mixed\rends\nhere\r\n",
    ];

    private static void AssertStateMatches(Document doc, StringBuilder oracle, string context)
    {
        string expected = oracle.ToString();
        Assert.AreEqual(expected.Length, doc.Length, $"{context}: length");
        Assert.AreEqual(expected, doc.GetText(0, doc.Length), $"{context}: text");

        var lines = SplitLinesNative(expected);
        Assert.AreEqual(lines.Count, doc.LineCount, $"{context}: line count");
        long offset = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            Assert.AreEqual(offset, doc.GetOffsetForLine(i), $"{context}: line {i} start");
            var slice = doc.GetLine(i);
            Assert.AreEqual(lines[i].Content, slice.Text.ToString(), $"{context}: line {i} content");
            Assert.AreEqual(lines[i].TermLen, slice.TerminatorLength, $"{context}: line {i} term");
            offset += lines[i].Content.Length + lines[i].TermLen;
        }
    }

    private readonly record struct OracleLine(string Content, byte TermLen);

    /// <summary>Splits text into lines by the engine's convention (CRLF=1 break, lone CR/LF each break).</summary>
    private static List<OracleLine> SplitLinesNative(string text)
    {
        var lines = new List<OracleLine>();
        int lineStart = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                bool crlf = i + 1 < text.Length && text[i + 1] == '\n';
                lines.Add(new OracleLine(text[lineStart..i], (byte)(crlf ? 2 : 1)));
                if (crlf) i++;
                lineStart = i + 1;
            }
            else if (c == '\n')
            {
                lines.Add(new OracleLine(text[lineStart..i], 1));
                lineStart = i + 1;
            }
        }
        lines.Add(new OracleLine(text[lineStart..], 0));
        return lines;
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(42)]
    [DataRow(1234)]
    public void WhenRandomEditScriptThenMatchesOracle(int seed)
    {
        var rng = new Random(seed);
        var clock = new FakeTimeSource();
        string initial = rng.Next(3) switch
        {
            0 => "",
            1 => "seed line one\r\nseed line two\r\nseed line three",
            _ => string.Join("\n", Enumerable.Range(0, 20).Select(i => $"line {i}")),
        };
        var doc = Document.FromText(initial, clock);
        var oracle = new StringBuilder(initial);

        for (int step = 0; step < 400; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(rng.Next(1200)));
            int op = rng.Next(10);
            if (op < 5)
            {
                // Insert (raw, so the oracle needs no EOL conversion modelling)
                string text = InsertSamples[rng.Next(InsertSamples.Length)];
                long at = rng.NextInt64(oracle.Length + 1);
                doc.InsertRaw(at, text);
                oracle.Insert((int)at, text);
            }
            else if (op < 8 && oracle.Length > 0)
            {
                long at = rng.NextInt64(oracle.Length);
                long len = Math.Min(rng.NextInt64(1, 12), oracle.Length - at);
                doc.Delete(at, len);
                oracle.Remove((int)at, (int)len);
            }
            else if (op == 8 && oracle.Length > 0)
            {
                long at = rng.NextInt64(oracle.Length);
                long len = Math.Min(rng.NextInt64(0, 8), oracle.Length - at);
                string text = InsertSamples[rng.Next(InsertSamples.Length)];
                // Replace converts EOLs; model that on the oracle side.
                string converted = ConvertEols(text, doc.NewLineString);
                doc.Replace(at, len, text);
                oracle.Remove((int)at, (int)len).Insert((int)at, converted);
            }

            if (step % 25 == 0) AssertStateMatches(doc, oracle, $"seed {seed} step {step}");
        }
        AssertStateMatches(doc, oracle, $"seed {seed} final");
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(99)]
    public void WhenRandomEditsThenFullUndoRestoresInitialAndRedoReplays(int seed)
    {
        var rng = new Random(seed);
        var clock = new FakeTimeSource();
        const string initial = "alpha\r\nbeta\ngamma\rdelta";
        var doc = Document.FromText(initial, clock);
        var oracle = new StringBuilder(initial);

        for (int step = 0; step < 150; step++)
        {
            clock.Advance(TimeSpan.FromSeconds(2)); // defeat coalescing: 1 op = 1 unit
            if (rng.Next(2) == 0 || oracle.Length == 0)
            {
                string text = InsertSamples[rng.Next(InsertSamples.Length)];
                long at = rng.NextInt64(oracle.Length + 1);
                doc.InsertRaw(at, text);
                oracle.Insert((int)at, text);
            }
            else
            {
                long at = rng.NextInt64(oracle.Length);
                long len = Math.Min(rng.NextInt64(1, 6), oracle.Length - at);
                doc.Delete(at, len);
                oracle.Remove((int)at, (int)len);
            }
        }
        string finalText = oracle.ToString();
        Assert.AreEqual(finalText, doc.GetText(0, doc.Length), "pre-undo");

        while (doc.CanUndo) doc.Undo();
        Assert.AreEqual(initial, doc.GetText(0, doc.Length), "after full undo");
        AssertStateMatches(doc, new StringBuilder(initial), "after full undo geometry");

        while (doc.CanRedo) doc.Redo();
        Assert.AreEqual(finalText, doc.GetText(0, doc.Length), "after full redo");
        AssertStateMatches(doc, oracle, "after full redo geometry");
    }

    [TestMethod]
    public void WhenGetLineColumnRandomOffsetsThenMatchesOracle()
    {
        var rng = new Random(77);
        var doc = Document.FromText("");
        var oracle = new StringBuilder();
        for (int i = 0; i < 60; i++)
        {
            string text = InsertSamples[rng.Next(InsertSamples.Length)];
            long at = rng.NextInt64(oracle.Length + 1);
            doc.InsertRaw(at, text);
            oracle.Insert((int)at, text);
        }
        string s = oracle.ToString();

        for (long off = 0; off <= s.Length; off++)
        {
            var (line, col) = doc.GetLineColumn(off);
            var (expLine, expCol) = OracleLineColumn(s, (int)off);
            Assert.AreEqual(expLine, line, $"line at {off}");
            Assert.AreEqual(expCol, col, $"col at {off}");
        }
    }

    private static (long Line, long Col) OracleLineColumn(string text, int offset)
    {
        long line = 0;
        int lineStart = 0;
        for (int i = 0; i < offset; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                // A CRLF break is counted at its LF; a CR with a following LF adds
                // nothing here (and an offset between CR and LF sees an incomplete
                // break - the engine defines the same).
                if (i + 1 < text.Length && text[i + 1] == '\n') continue;
                line++;
                lineStart = i + 1;
            }
            else if (c == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
        return (line, offset - lineStart);
    }

    private static string ConvertEols(string text, string target)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                sb.Append(target);
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (c == '\n')
            {
                sb.Append(target);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
