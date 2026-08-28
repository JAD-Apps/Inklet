using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Inklet.Engine;

/// <summary>Options for <see cref="Document.SaveAsync"/>.</summary>
internal sealed class SaveOptions
{
    /// <summary>Target path; null saves over the document's own file.</summary>
    public string? TargetPath { get; init; }

    /// <summary>Re-encode to this encoding (Save As with conversion); null keeps the document's.</summary>
    public Encoding? EncodingOverride { get; init; }

    /// <summary>Write/omit a BOM; null keeps the document's current preference.</summary>
    public bool? BomOverride { get; init; }

    public IProgress<double>? Progress { get; init; }
}

internal sealed partial class Document
{
    /// <summary>
    /// Streams the document to disk atomically (temp file + replace). Unedited
    /// regions of a streamed original are copied as raw bytes, so they stay
    /// byte-identical - mixed line endings and all. The tree keeps referencing
    /// the pre-save buffers afterwards (the old mapping pins its data), which is
    /// what lets undo history survive a save with no copying.
    ///
    /// Call on the mutation thread; the write itself runs on the thread pool
    /// against an immutable snapshot, so the UI stays live. Concurrent edits
    /// during the write are safe but are NOT part of what lands on disk;
    /// MarkSaved fires only when the written state is still the current state.
    /// </summary>
    public async Task SaveAsync(SaveOptions? options = null, CancellationToken ct = default)
    {
        options ??= new SaveOptions();
        string? target = options.TargetPath ?? FilePath;
        if (string.IsNullOrEmpty(target))
            throw new InvalidOperationException("No target path: pass SaveOptions.TargetPath for an untitled document.");

        // Snapshot everything the writer needs (mutation thread).
        var root = Root;
        int revisionAtSave = Revision;
        var index = _index;
        int absorbedSegments = _absorbedSegments;
        var encoding = options.EncodingOverride ?? Encoding;
        bool writeBom = options.BomOverride ?? HasBom;
        bool rawCopyAllowed = options.EncodingOverride is null;

        string tmp = target + ".tmp";
        await Task.Run(() =>
        {
            using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.SequentialScan);
            if (writeBom)
            {
                var preamble = encoding.GetPreamble();
                fs.Write(preamble, 0, preamble.Length);
            }

            long totalChars = PieceTreeOps.CharLen(root);
            long writtenChars = 0;
            var pieces = new List<Piece>();
            PieceTreeOps.CollectPieces(root, pieces);
            foreach (var piece in pieces)
            {
                ct.ThrowIfCancellationRequested();
                WritePiece(fs, piece, encoding, rawCopyAllowed);
                writtenChars += piece.Length;
                options.Progress?.Report(totalChars == 0 ? 1 : (double)writtenChars / Math.Max(1, totalChars));
            }

            // While indexing, the un-absorbed tail is still raw original bytes.
            if (index is not null && absorbedSegments < index.SegmentCount)
            {
                if (!rawCopyAllowed)
                    throw new InvalidOperationException("Cannot re-encode before indexing completes.");
                long tailStart = index.SegmentStartByte(absorbedSegments);
                CopyRawBytes(fs, _source!, tailStart, _source!.Length - tailStart);
            }
        }, ct); // no ConfigureAwait: MarkSaved below must resume on the mutation thread

        // Atomic swap. Replace requires an existing target; Move covers new files.
        if (File.Exists(target))
            File.Replace(tmp, target, destinationBackupFileName: null);
        else
            File.Move(tmp, target);

        // Only mark clean if nothing changed while the bytes were being written.
        if (Revision == revisionAtSave)
        {
            MarkSaved();
        }
        FilePath = target;
        if (options.EncodingOverride is not null) Encoding = options.EncodingOverride;
        if (options.BomOverride is not null) HasBom = options.BomOverride.Value;
    }

    private void WritePiece(FileStream fs, in Piece piece, Encoding encoding, bool rawCopyAllowed)
    {
        if (piece.Kind == PieceBufferKind.Original && rawCopyAllowed && _originalBuf is MappedCharBuffer mapped)
        {
            // Byte-identical fast path: map the char range back to its bytes.
            long byteStart = mapped.ContentByteOfUnit(piece.Start);
            long byteEnd = mapped.ContentByteOfUnit(piece.Start + piece.Length);
            CopyRawBytes(fs, _source!, _codec!.PreambleLength + byteStart, byteEnd - byteStart);
            return;
        }
        // Encode chars (add-buffer pieces, in-memory originals, or re-encode saves).
        EncodeCharRange(fs, piece, encoding);
    }

    private void EncodeCharRange(FileStream fs, in Piece piece, Encoding encoding)
    {
        const int ChunkChars = 64 * 1024;
        var buffer = BufferFor(piece.Kind);
        var encoder = encoding.GetEncoder();
        var charBuf = new char[Math.Min(ChunkChars, (int)Math.Min(piece.Length, int.MaxValue))];
        var byteBuf = new byte[encoding.GetMaxByteCount(charBuf.Length)];
        long remaining = piece.Length;
        long at = piece.Start;
        while (remaining > 0)
        {
            int take = (int)Math.Min(remaining, charBuf.Length);
            buffer.CopyTo(at, take, charBuf);
            bool last = remaining == take;
            int bytes = encoder.GetBytes(charBuf, 0, take, byteBuf, 0, flush: last);
            fs.Write(byteBuf, 0, bytes);
            at += take;
            remaining -= take;
        }
    }

    private static void CopyRawBytes(FileStream fs, IByteSource source, long offset, long length)
    {
        const int ChunkBytes = 1024 * 1024;
        var buf = new byte[(int)Math.Min(ChunkBytes, Math.Max(1, length))];
        long remaining = length;
        while (remaining > 0)
        {
            int take = (int)Math.Min(remaining, buf.Length);
            source.CopyTo(offset, buf.AsSpan(0, take));
            fs.Write(buf, 0, take);
            offset += take;
            remaining -= take;
        }
    }
}
