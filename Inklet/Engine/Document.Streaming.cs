using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Inklet.Models;
using Inklet.Services;

namespace Inklet.Engine;

/// <summary>
/// The streaming half of <see cref="Document"/>: memory-mapped open, background
/// indexing, segment absorption, and the estimated tail. The file's indexed
/// prefix lives in the piece tree (exact); the un-indexed tail is a pending byte
/// range whose char/line counts are density estimates that refine with progress.
/// Edits are only accepted inside the absorbed region while a tail is pending.
/// </summary>
internal sealed partial class Document : IDisposable
{
    /// <summary>Files at or below this size are fully decoded on open (simple, fast).</summary>
    internal const long SmallFileThresholdBytes = 4 * 1024 * 1024;

    /// <summary>Unstreamable encodings are fully decoded up to this size, refused beyond.</summary>
    internal const long UnstreamableMaxBytes = 256 * 1024 * 1024;

    private IByteSource? _source;
    private TextCodec? _codec;
    private OriginalIndex? _index;
    private CancellationTokenSource? _indexCts;
    private Task? _indexTask;
    private int _absorbedSegments;
    private long _estimatedTailChars;   // volatile via Interlocked/Volatile
    private long _estimatedTailBreaks;
    private bool _disposed;

    public string? FilePath { get; private set; }
    public Encoding Encoding { get; private set; } = Encoding.UTF8;
    public bool HasBom { get; private set; }

    /// <summary>Fraction of the file indexed, 1.0 when complete (or non-streamed).</summary>
    public double IndexProgress => _index is null ? 1.0
        : _index.SegmentCount == 0 ? 1.0
        : (double)_index.CompletedSegments / _index.SegmentCount;

    public bool IsFullyIndexed => _index is null || _index.IsComplete;

    /// <summary>Raised on the indexer thread; marshal before touching UI.</summary>
    public event Action<double>? IndexProgressChanged;

    /// <summary>Raised once, on the indexer thread, when the whole file is indexed.</summary>
    public event Action? IndexCompleted;

    public bool IsLengthExact => IsFullyIndexed;
    public bool IsLineCountExact => IsFullyIndexed;

    /// <summary>Chars currently absorbed into the piece tree (== Length once complete).</summary>
    public long AbsorbedLength => PieceTreeOps.CharLen(Root);

    /// <summary>Fully terminated lines available for exact geometry queries.</summary>
    public long IndexedLineCountFloor => PieceTreeOps.Breaks(Root);

    /// <summary>True when an edit at [offset, offset+length) is currently allowed.</summary>
    public bool IsEditableRange(long offset, long length)
        => offset >= 0 && length >= 0 && offset + length <= AbsorbedLength;

    // ── Open ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a file for editing. Small files and unstreamable encodings decode
    /// fully; everything else memory-maps, scans the first segment synchronously
    /// (instant first screen) and indexes the rest on a background thread. The
    /// caller must Dispose the document.
    /// </summary>
    public static async Task<Document> OpenAsync(string path, CancellationToken ct = default,
        int segmentBytes = OriginalIndex.DefaultSegmentBytes)
    {
        return await Task.Run(() => OpenCore(path, ct, segmentBytes, startIndexer: true, forceStreaming: false), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Deterministic open for tests: always streams (no small-file shortcut) and
    /// the index only advances via <see cref="AdvanceIndexForTest"/>.
    /// </summary>
    internal static Document OpenForTest(string path, int segmentBytes)
        => OpenCore(path, CancellationToken.None, segmentBytes, startIndexer: false, forceStreaming: true);

    /// <summary>Scans one more segment on the calling thread. False when already complete.</summary>
    internal bool AdvanceIndexForTest()
    {
        var index = _index;
        if (index is null || index.IsComplete) return false;
        index.ScanNextSegment(ref _pendingCarry);
        RefreshTailEstimate();
        if (index.IsComplete) FinishIndexing();
        else IndexProgressChanged?.Invoke(IndexProgress);
        return true;
    }

    private ScanCarry _pendingCarry;

    private static Document OpenCore(string path, CancellationToken ct, int segmentBytes, bool startIndexer,
        bool forceStreaming)
    {
        var source = new MemoryMappedByteSource(path);
        try
        {
            // Detect from the head sample, exactly like the old FileService path.
            int headLen = (int)Math.Min(64 * 1024, source.Length);
            var head = new byte[headLen];
            if (headLen > 0) source.CopyTo(0, head);
            var (encoding, hasBom) = EncodingDetector.Detect(head);
            var codec = TextCodec.Create(encoding, hasBom);
            long contentBytes = source.Length - codec.PreambleLength;

            if (codec.Class == CodecClass.Unstreamable || (!forceStreaming && source.Length <= SmallFileThresholdBytes))
            {
                if (codec.Class == CodecClass.Unstreamable && source.Length > UnstreamableMaxBytes)
                    throw new NotSupportedException(
                        $"Files in encoding '{encoding.WebName}' are limited to {UnstreamableMaxBytes / (1024 * 1024)} MB.");
                // Full decode (small or unstreamable): reuse the proven in-memory path.
                var all = new byte[contentBytes];
                if (contentBytes > 0) source.CopyTo(codec.PreambleLength, all);
                string text = encoding.GetString(all);
                source.Dispose();
                var doc = new Document(text, SystemTimeSource.Instance)
                {
                    FilePath = path,
                    Encoding = encoding,
                    HasBom = hasBom,
                };
                return doc;
            }

            // Streaming path.
            var index = new OriginalIndex(source, codec, segmentBytes);
            var carry = new ScanCarry();
            var firstScan = index.ScanNextSegment(ref carry);   // instant first screen
            ct.ThrowIfCancellationRequested();

            var eol = DominantEol(firstScan);
            var streamed = new Document(source, codec, index, eol)
            {
                FilePath = path,
                Encoding = encoding,
                HasBom = hasBom,
            };
            streamed.AbsorbIndexedSegments();
            if (startIndexer) streamed.StartIndexer(carry);
            else streamed._pendingCarry = carry;
            return streamed;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static LineEndingStyle DominantEol(in SegmentScan scan)
    {
        long crlf = scan.CrLf, lf = scan.LoneLf, cr = scan.LoneCr;
        if (crlf == 0 && lf == 0 && cr == 0) return LineEndingStyle.CrLf;
        if (crlf >= lf && crlf >= cr) return LineEndingStyle.CrLf;
        return lf >= cr ? LineEndingStyle.Lf : LineEndingStyle.Cr;
    }

    private void StartIndexer(ScanCarry carry)
    {
        var index = _index!;
        if (index.IsComplete)
        {
            FinishIndexing();
            return;
        }
        _indexCts = new CancellationTokenSource();
        var token = _indexCts.Token;
        var tcs = new TaskCompletionSource();
        _indexTask = tcs.Task;
        // A dedicated below-normal thread: the scan saturates memory bandwidth for
        // seconds on huge files, and it must lose every contest with the UI thread.
        var thread = new Thread(() =>
        {
            try
            {
                var localCarry = carry;
                var lastReport = Environment.TickCount64;
                while (!index.IsComplete)
                {
                    if (token.IsCancellationRequested) { tcs.TrySetCanceled(token); return; }
                    index.ScanNextSegment(ref localCarry);
                    RefreshTailEstimate();
                    long now = Environment.TickCount64;
                    if (now - lastReport >= 250 || index.IsComplete)
                    {
                        lastReport = now;
                        IndexProgressChanged?.Invoke(IndexProgress);
                    }
                }
                FinishIndexing();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Inklet.Indexer",
        };
        thread.Start();
    }

    private void FinishIndexing()
    {
        RefreshTailEstimate();
        IndexCompleted?.Invoke();
    }

    /// <summary>
    /// Pulls newly indexed segments into the piece tree. MUST run on the
    /// mutation thread (the facade calls it when progress events arrive; the
    /// final call comes via the IndexCompleted handler).
    /// </summary>
    public void AbsorbIndexedSegments()
    {
        var index = _index;
        var buf = _originalBuf as MappedCharBuffer;
        if (index is null || buf is null) return;

        // Absorb ALL newly indexed segments as ONE contiguous piece append - a
        // single O(log p) tree operation per call, however many segments the
        // background scan completed since the last marshal.
        int completed = index.CompletedSegments;
        if (_absorbedSegments < completed)
        {
            long startUnit = index.UnitsBeforeSegment(_absorbedSegments);
            long endUnit = index.UnitsBeforeSegment(completed);
            if (endUnit > startUnit)
            {
                var piece = Piece.Create(PieceBufferKind.Original, buf, startUnit, endUnit - startUnit);
                AppendOriginalPiece(piece);
            }
            _absorbedSegments = completed;
        }
        RefreshTailEstimate();
    }

    /// <summary>Appends an original piece at the end of the tree, extending the last piece when contiguous.</summary>
    private void AppendOriginalPiece(Piece piece)
    {
        var root = Root;
        if (root is not null)
        {
            var max = PieceTreeOps.MaxPiece(root)!.Value;
            if (max.Kind == PieceBufferKind.Original && max.Start + max.Length == piece.Start)
            {
                var rest = PieceTreeOps.RemoveMaxPiece(root, out _);
                var merged = Piece.Create(PieceBufferKind.Original, _originalBuf, max.Start, max.Length + piece.Length);
                Volatile.Write(ref _root, PieceTreeOps.Concat3(rest, merged, null));
                return;
            }
        }
        Volatile.Write(ref _root, root is null
            ? new PieceTreeNode(null, piece, null)
            : PieceTreeOps.Concat3(root, piece, null));
    }

    private void RefreshTailEstimate()
    {
        var index = _index;
        if (index is null) return;
        long indexedBytes = index.IndexedBytes;
        long remainingBytes = index.ContentLength - indexedBytes;
        if (remainingBytes <= 0 || indexedBytes == 0)
        {
            Volatile.Write(ref _estimatedTailChars, 0);
            Volatile.Write(ref _estimatedTailBreaks, 0);
            return;
        }
        double unitsPerByte = (double)index.IndexedUnits / indexedBytes;
        double breaksPerByte = (double)index.IndexedBreaks / indexedBytes;
        Volatile.Write(ref _estimatedTailChars, (long)(remainingBytes * unitsPerByte));
        Volatile.Write(ref _estimatedTailBreaks, (long)(remainingBytes * breaksPerByte));
    }

    /// <summary>
    /// Chars beyond the absorbed tree: the exactly-indexed-but-not-yet-absorbed
    /// span plus (while indexing) the density estimate for the un-scanned rest.
    /// </summary>
    private long PendingTailChars
    {
        get
        {
            var index = _index;
            if (index is null) return 0;
            long absorbed = index.UnitsBeforeSegment(Math.Min(_absorbedSegments, index.SegmentCount));
            long unabsorbedExact = index.IndexedUnits - absorbed;
            return unabsorbedExact + (index.IsComplete ? 0 : Volatile.Read(ref _estimatedTailChars));
        }
    }

    private long PendingTailBreaks
    {
        get
        {
            var index = _index;
            if (index is null) return 0;
            long absorbed = index.BreaksBeforeSegment(Math.Min(_absorbedSegments, index.SegmentCount));
            long unabsorbedExact = index.IndexedBreaks - absorbed;
            return unabsorbedExact + (index.IsComplete ? 0 : Volatile.Read(ref _estimatedTailBreaks));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _indexCts?.Cancel();
        try { _indexTask?.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancellation */ }
        _indexCts?.Dispose();
        _source?.Dispose();
    }
}
