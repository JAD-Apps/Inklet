using Inklet.Models;
using System.Text;

namespace Inklet.Tests;

[TestClass]
public class DocumentStateTests
{
    [TestMethod]
    public void WhenNewDocumentStateThenDefaultsCorrect()
    {
        var state = new DocumentState();

        Assert.IsNull(state.FilePath);
        Assert.AreEqual(65001, state.Encoding.CodePage);
        Assert.IsFalse(state.HasBom);
        Assert.AreEqual(LineEndingStyle.CrLf, state.LineEnding);
    }

    [TestMethod]
    public void WhenFilePathNullThenDisplayFileNameIsUntitled()
    {
        var state = new DocumentState();

        Assert.AreEqual("Untitled", state.DisplayFileName);
    }

    [TestMethod]
    public void WhenFilePathSetThenDisplayFileNameIsFileName()
    {
        var state = new DocumentState { FilePath = @"C:\Users\Test\Documents\hello.txt" };

        Assert.AreEqual("hello.txt", state.DisplayFileName);
    }

    [TestMethod]
    public void WhenUtf8WithBomThenEncodingDisplayNameIsUtf8WithBom()
    {
        var state = new DocumentState
        {
            Encoding = new UTF8Encoding(true),
            HasBom = true
        };

        Assert.AreEqual("UTF-8 with BOM", state.EncodingDisplayName);
    }

    [TestMethod]
    public void WhenUtf8WithoutBomThenEncodingDisplayNameIsUtf8()
    {
        var state = new DocumentState
        {
            Encoding = new UTF8Encoding(false),
            HasBom = false
        };

        Assert.AreEqual("UTF-8", state.EncodingDisplayName);
    }

    [TestMethod]
    public void WhenUtf16LeThenEncodingDisplayNameIsUtf16Le()
    {
        var state = new DocumentState { Encoding = Encoding.Unicode };

        Assert.AreEqual("UTF-16 LE", state.EncodingDisplayName);
    }

    [TestMethod]
    public void WhenUtf16BeThenEncodingDisplayNameIsUtf16Be()
    {
        var state = new DocumentState { Encoding = Encoding.BigEndianUnicode };

        Assert.AreEqual("UTF-16 BE", state.EncodingDisplayName);
    }

    [TestMethod]
    public void WhenRecordWithThenCreatesNewInstanceWithChanges()
    {
        var original = new DocumentState { FilePath = "test.txt" };

        var modified = original with { FilePath = "other.txt" };

        Assert.AreEqual("test.txt", original.FilePath);
        Assert.AreEqual("other.txt", modified.FilePath);
    }

    [TestMethod]
    public void WhenTwoStatesWithSameValuesThenTheyAreEqual()
    {
        var state1 = new DocumentState { FilePath = "test.txt", HasBom = true };
        var state2 = new DocumentState { FilePath = "test.txt", HasBom = true };

        Assert.AreEqual(state1, state2);
    }
}
