using Inklet.Engine;

namespace Inklet.Tests.Engine;

/// <summary>
/// The 32-bit size refusal. A 32-bit process cannot map a large file into its
/// ~2 GB address space, and the guard has to run BEFORE the mapping is attempted:
/// when it sat after, the map failed first and the user got a raw OS error
/// ("Not enough memory resources are available to process this command") instead
/// of the message pointing at the 64-bit build that the store listing promises.
///
/// The guard takes the process width as an argument so both sides can be checked
/// from the 64-bit test host.
/// </summary>
[TestClass]
public sealed class DocumentThirtyTwoBitGuardTests
{
    private const long Cap = 256L * 1024 * 1024;

    [TestMethod]
    public void WhenThirtyTwoBitAndFileOverCapThenRefusedWithTheSixtyFourBitMessage()
    {
        var ex = Assert.ThrowsExactly<NotSupportedException>(
            () => Document.GuardThirtyTwoBitFileSize(Cap + 1, is64BitProcess: false));
        Assert.Contains("256 MB", ex.Message);
        Assert.Contains("64-bit", ex.Message);
    }

    [TestMethod]
    public void WhenThirtyTwoBitAndFileAtCapThenAllowed()
    {
        Document.GuardThirtyTwoBitFileSize(Cap, is64BitProcess: false);
    }

    [TestMethod]
    public void WhenThirtyTwoBitAndSmallFileThenAllowed()
    {
        Document.GuardThirtyTwoBitFileSize(0, is64BitProcess: false);
        Document.GuardThirtyTwoBitFileSize(4L * 1024 * 1024, is64BitProcess: false);
    }

    [TestMethod]
    public void WhenSixtyFourBitThenNoSizeIsRefused()
    {
        Document.GuardThirtyTwoBitFileSize(Cap + 1, is64BitProcess: true);
        Document.GuardThirtyTwoBitFileSize(10L * 1024 * 1024 * 1024, is64BitProcess: true);
        Document.GuardThirtyTwoBitFileSize(long.MaxValue, is64BitProcess: true);
    }

    [TestMethod]
    public void WhenSixtyFourBitHostThenRealOpensAreUnaffected()
    {
        // Sanity: the guard sits on the open path, so opening a normal file on this
        // (64-bit) host must still work.
        var path = Path.Combine(Path.GetTempPath(), $"inklet-guard-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "alpha\r\nbeta\r\n");
        try
        {
            using var doc = Document.OpenAsync(path).GetAwaiter().GetResult();
            Assert.AreEqual(13, doc.Length);
        }
        finally { File.Delete(path); }
    }
}
