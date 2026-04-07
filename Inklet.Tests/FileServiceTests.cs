using Inklet.Models;
using Inklet.Services;
using System.Text;

namespace Inklet.Tests;

[TestClass]
public class FileServiceTests
{
    private string _testDir = null!;

    [TestInitialize]
    public void Setup()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _testDir = Path.Combine(Path.GetTempPath(), $"InkletTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [TestMethod]
    public async Task WhenReadUtf8FileWithoutBomThenContentAndStateCorrect()
    {
        var filePath = Path.Combine(_testDir, "test_utf8.txt");
        await File.WriteAllTextAsync(filePath, "Hello World", new UTF8Encoding(false));

        var (content, state) = await FileService.ReadFileAsync(filePath);

        Assert.AreEqual("Hello World", content);
        Assert.AreEqual(65001, state.Encoding.CodePage);
        Assert.IsFalse(state.HasBom);
        Assert.AreEqual(filePath, state.FilePath);
    }

    [TestMethod]
    public async Task WhenReadUtf8FileWithBomThenDetectsBom()
    {
        var filePath = Path.Combine(_testDir, "test_utf8bom.txt");
        await File.WriteAllTextAsync(filePath, "Hello World", new UTF8Encoding(true));

        var (content, state) = await FileService.ReadFileAsync(filePath);

        Assert.AreEqual("Hello World", content);
        Assert.IsTrue(state.HasBom);
    }

    [TestMethod]
    public async Task WhenReadUtf16LeFileThenDetectsCorrectly()
    {
        var filePath = Path.Combine(_testDir, "test_utf16le.txt");
        await File.WriteAllTextAsync(filePath, "Hello", Encoding.Unicode);

        var (content, state) = await FileService.ReadFileAsync(filePath);

        Assert.AreEqual("Hello", content);
        Assert.AreEqual(1200, state.Encoding.CodePage);
        Assert.IsTrue(state.HasBom);
    }

    [TestMethod]
    public async Task WhenReadUtf16BeFileThenDetectsCorrectly()
    {
        var filePath = Path.Combine(_testDir, "test_utf16be.txt");
        await File.WriteAllTextAsync(filePath, "Hello", Encoding.BigEndianUnicode);

        var (content, state) = await FileService.ReadFileAsync(filePath);

        Assert.AreEqual("Hello", content);
        Assert.AreEqual(1201, state.Encoding.CodePage);
        Assert.IsTrue(state.HasBom);
    }

    [TestMethod]
    public async Task WhenReadFileWithCrLfThenLineEndingDetected()
    {
        var filePath = Path.Combine(_testDir, "test_crlf.txt");
        await File.WriteAllBytesAsync(filePath, "Line1\r\nLine2\r\nLine3"u8.ToArray());

        var (_, state) = await FileService.ReadFileAsync(filePath);

        Assert.AreEqual(LineEndingStyle.CrLf, state.LineEnding);
    }

    [TestMethod]
    public async Task WhenReadFileWithLfThenLineEndingDetected()
    {
        var filePath = Path.Combine(_testDir, "test_lf.txt");
        await File.WriteAllBytesAsync(filePath, "Line1\nLine2\nLine3"u8.ToArray());

        var (_, state) = await FileService.ReadFileAsync(filePath);

        Assert.AreEqual(LineEndingStyle.Lf, state.LineEnding);
    }

    [TestMethod]
    public async Task WhenWriteAndReadRoundTripThenContentPreserved()
    {
        var filePath = Path.Combine(_testDir, "roundtrip.txt");
        var originalContent = "Hello\r\nWorld\r\nTest";

        await FileService.WriteFileAsync(filePath, originalContent, Encoding.UTF8, false, LineEndingStyle.CrLf);
        var (content, _) = await FileService.ReadFileAsync(filePath);

        Assert.AreEqual(originalContent, content);
    }

    [TestMethod]
    public async Task WhenWriteWithBomThenBomPresent()
    {
        var filePath = Path.Combine(_testDir, "with_bom.txt");

        await FileService.WriteFileAsync(filePath, "Test", Encoding.UTF8, true, LineEndingStyle.CrLf);
        var bytes = await File.ReadAllBytesAsync(filePath);

        // Check for UTF-8 BOM
        Assert.IsTrue(bytes.Length >= 3);
        Assert.AreEqual(0xEF, bytes[0]);
        Assert.AreEqual(0xBB, bytes[1]);
        Assert.AreEqual(0xBF, bytes[2]);
    }

    [TestMethod]
    public async Task WhenWriteWithoutBomThenNoBomPresent()
    {
        var filePath = Path.Combine(_testDir, "without_bom.txt");

        await FileService.WriteFileAsync(filePath, "Test", Encoding.UTF8, false, LineEndingStyle.CrLf);
        var bytes = await File.ReadAllBytesAsync(filePath);

        // First byte should be 'T' (0x54), not BOM
        Assert.AreEqual(0x54, bytes[0]);
    }

    [TestMethod]
    public async Task WhenWriteWithLineEndingConversionThenConverted()
    {
        var filePath = Path.Combine(_testDir, "convert_le.txt");

        await FileService.WriteFileAsync(filePath, "Hello\r\nWorld", Encoding.UTF8, false, LineEndingStyle.Lf);
        var content = await File.ReadAllTextAsync(filePath);

        Assert.AreEqual("Hello\nWorld", content);
    }

    [TestMethod]
    public async Task WhenReadEmptyFileThenReturnsEmptyString()
    {
        var filePath = Path.Combine(_testDir, "empty.txt");
        await File.WriteAllTextAsync(filePath, "");

        var (content, state) = await FileService.ReadFileAsync(filePath);

        Assert.AreEqual("", content);
        Assert.AreEqual(65001, state.Encoding.CodePage);
    }

    [TestMethod]
    public async Task WhenReadNullPathThenThrowsArgumentException()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => FileService.ReadFileAsync(null!));
    }

    [TestMethod]
    public async Task WhenReadEmptyPathThenThrowsArgumentException()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => FileService.ReadFileAsync(""));
    }

    [TestMethod]
    public async Task WhenWriteNullPathThenThrowsArgumentException()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => FileService.WriteFileAsync(null!, "content", Encoding.UTF8, false, LineEndingStyle.CrLf));
    }

    [TestMethod]
    public async Task WhenWriteNullContentThenThrowsArgumentNullException()
    {
        var filePath = Path.Combine(_testDir, "null_content.txt");

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => FileService.WriteFileAsync(filePath, null!, Encoding.UTF8, false, LineEndingStyle.CrLf));
    }

    [TestMethod]
    public void WhenGetFileSizeForExistingFileThenReturnsSize()
    {
        var filePath = Path.Combine(_testDir, "size_test.txt");
        File.WriteAllText(filePath, "Hello");

        var size = FileService.GetFileSize(filePath);

        Assert.IsTrue(size > 0);
    }

    [TestMethod]
    public void WhenGetFileSizeForMissingFileThenReturnsNegativeOne()
    {
        var size = FileService.GetFileSize(Path.Combine(_testDir, "nonexistent.txt"));

        Assert.AreEqual(-1, size);
    }

    [TestMethod]
    public void WhenLargeFileThresholdThenEquals10MB()
    {
        Assert.AreEqual(10 * 1024 * 1024, FileService.LargeFileThreshold);
    }
}
