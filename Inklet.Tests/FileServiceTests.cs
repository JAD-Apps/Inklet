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

    // -----------------------------------------------------------------------
    // IsBinaryFile — extension-based detection
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WhenFileHasExeExtensionThenIsBinaryTrue()
    {
        var filePath = Path.Combine(_testDir, "app.exe");
        File.WriteAllText(filePath, "MZ");

        Assert.IsTrue(FileService.IsBinaryFile(filePath));
    }

    [TestMethod]
    public void WhenFileHasDllExtensionThenIsBinaryTrue()
    {
        var filePath = Path.Combine(_testDir, "lib.dll");
        File.WriteAllText(filePath, "MZ");

        Assert.IsTrue(FileService.IsBinaryFile(filePath));
    }

    [TestMethod]
    public void WhenFileHasZipExtensionThenIsBinaryTrue()
    {
        var filePath = Path.Combine(_testDir, "archive.zip");
        File.WriteAllText(filePath, "PK");

        Assert.IsTrue(FileService.IsBinaryFile(filePath));
    }

    [TestMethod]
    public void WhenFileHasPngExtensionThenIsBinaryTrue()
    {
        var filePath = Path.Combine(_testDir, "image.png");
        File.WriteAllBytes(filePath, [0x89, 0x50, 0x4E, 0x47]);

        Assert.IsTrue(FileService.IsBinaryFile(filePath));
    }

    [TestMethod]
    public void WhenFileHasPdfExtensionThenIsBinaryTrue()
    {
        var filePath = Path.Combine(_testDir, "document.pdf");
        File.WriteAllText(filePath, "%PDF-1.4");

        Assert.IsTrue(FileService.IsBinaryFile(filePath));
    }

    [TestMethod]
    public void WhenFileHasMsixExtensionThenIsBinaryTrue()
    {
        var filePath = Path.Combine(_testDir, "package.msix");
        File.WriteAllText(filePath, "PK");

        Assert.IsTrue(FileService.IsBinaryFile(filePath));
    }

    [TestMethod]
    public void WhenFileHasTxtExtensionThenIsBinaryFalse()
    {
        var filePath = Path.Combine(_testDir, "readme.txt");
        File.WriteAllText(filePath, "Hello World");

        Assert.IsFalse(FileService.IsBinaryFile(filePath));
    }

    [TestMethod]
    public void WhenFileHasCsExtensionThenIsBinaryFalse()
    {
        var filePath = Path.Combine(_testDir, "Program.cs");
        File.WriteAllText(filePath, "using System;");

        Assert.IsFalse(FileService.IsBinaryFile(filePath));
    }

    // -----------------------------------------------------------------------
    // IsBinaryFile — NUL-byte sniffing
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WhenFileContainsNulBytesThenIsBinaryTrue()
    {
        var filePath = Path.Combine(_testDir, "data.unknown");
        File.WriteAllBytes(filePath, [0x48, 0x65, 0x6C, 0x00, 0x6C, 0x6F]);

        Assert.IsTrue(FileService.IsBinaryFile(filePath));
    }

    [TestMethod]
    public void WhenFileContainsOnlyTextThenIsBinaryFalse()
    {
        var filePath = Path.Combine(_testDir, "plain.unknown");
        File.WriteAllText(filePath, "Just plain text content here.\nSecond line.");

        Assert.IsFalse(FileService.IsBinaryFile(filePath));
    }

    [TestMethod]
    public void WhenFileIsEmptyWithUnknownExtensionThenIsBinaryFalse()
    {
        var filePath = Path.Combine(_testDir, "empty.dat2");
        File.WriteAllBytes(filePath, []);

        Assert.IsFalse(FileService.IsBinaryFile(filePath));
    }

    [TestMethod]
    public void WhenExtensionCheckIsCaseInsensitiveThenIsBinaryTrue()
    {
        var filePath = Path.Combine(_testDir, "app.EXE");
        File.WriteAllText(filePath, "MZ");

        Assert.IsTrue(FileService.IsBinaryFile(filePath));
    }

    [TestMethod]
    public void WhenNullPathThenThrowsArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => FileService.IsBinaryFile(null!));
    }

    // -----------------------------------------------------------------------
    // Round-trip — non-UTF-8 encoded files preserve content exactly
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task WhenWriteAndReadWindows1252RoundTripThenContentPreserved()
    {
        // U+201C "LEFT DOUBLE QUOTATION MARK" lives at 0x93 in Windows-1252 but is a
        // multi-byte sequence in UTF-8. A round-trip via FileService must keep it intact.
        var filePath = Path.Combine(_testDir, "win1252.txt");
        var w1252 = Encoding.GetEncoding(1252);
        var original = "He said “hello”";

        await FileService.WriteFileAsync(filePath, original, w1252, false, LineEndingStyle.CrLf);
        var (content, state) = await FileService.ReadFileAsync(filePath);

        Assert.AreEqual(original, content);
        // The detector may report the file as Windows-1252 or some other ANSI page
        // depending on the system locale; the key contract is content fidelity.
        Assert.AreNotEqual(0, state.Encoding.CodePage);
    }
}
