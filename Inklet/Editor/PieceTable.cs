using System;
using System.Collections.Generic;
using System.Text;

namespace Inklet.Editor;

/// <summary>
/// A piece table — an immutable-original / append-only-add data structure that backs
/// editors capable of opening multi-gigabyte files without re-allocating the whole
/// buffer on every edit. Inserts and deletes are O(log pieces) (linear here for
/// clarity; a balanced tree variant can be slotted in later without changing the API).
///
/// <para>
/// Two backing buffers:
/// <list type="bullet">
///   <item><b>Original</b> — the file contents at load time. Never modified.</item>
///   <item><b>Add</b> — an append-only buffer that grows as the user types.</item>
/// </list>
/// The logical document is a sequence of <see cref="Piece"/>s, each pointing into
/// one of the two buffers with an offset and a length. An insert appends the new
/// text to the add buffer, splits the piece containing the insertion point, and
/// inserts a new piece between the halves. A delete shrinks or removes pieces.
/// </para>
/// <para>
/// Compared with the current "string per edit" approach, this avoids:
/// <list type="bullet">
///   <item>O(N) allocations on every keystroke for large documents</item>
///   <item>The full-file UTF-16 string materialisation on load</item>
///   <item>Loss of edit history granularity needed for undo / coalescing</item>
/// </list>
/// </para>
/// </summary>
public sealed class PieceTable
{
    private readonly string _original;
    private readonly StringBuilder _add = new();

    // Linked-list-style flat list. Each Piece carries which buffer it points into
    // (Add vs Original), the offset in that buffer, and the length. The logical
    // document is the in-order concatenation of pieces.
    private readonly List<Piece> _pieces = [];

    private int _length;

    /// <summary>
    /// Creates a new piece table seeded with <paramref name="original"/> as the
    /// immutable original buffer.
    /// </summary>
    public PieceTable(string original = "")
    {
        _original = original ?? string.Empty;
        if (_original.Length > 0)
        {
            _pieces.Add(new Piece(BufferKind.Original, 0, _original.Length));
            _length = _original.Length;
        }
    }

    /// <summary>Total number of characters in the document.</summary>
    public int Length => _length;

    /// <summary>Number of pieces currently used. Exposed for diagnostics / tests.</summary>
    internal int PieceCount => _pieces.Count;

    /// <summary>
    /// Inserts <paramref name="text"/> at <paramref name="offset"/>. Out-of-range
    /// offsets are clamped to <c>[0, Length]</c>. Inserts at the very end fast-path:
    /// if the previous piece is in the add buffer and contiguous with the append
    /// position, the existing piece is grown rather than a new one being created.
    /// </summary>
    public void Insert(int offset, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        offset = Math.Clamp(offset, 0, _length);

        int addStart = _add.Length;
        _add.Append(text);
        var newPiece = new Piece(BufferKind.Add, addStart, text.Length);
        _length += text.Length;

        // Locate the piece containing the insertion point. AccumulatedOffset is the
        // running total of piece lengths before the current piece.
        int accumulated = 0;
        for (int i = 0; i < _pieces.Count; i++)
        {
            int pieceLen = _pieces[i].Length;

            if (offset == accumulated)
            {
                // Coalesce with the trailing piece if it's also Add and contiguous —
                // this keeps the piece count from blowing up under sustained typing.
                if (i > 0 && CanCoalesce(_pieces[i - 1], newPiece))
                    _pieces[i - 1] = _pieces[i - 1].WithLength(_pieces[i - 1].Length + newPiece.Length);
                else
                    _pieces.Insert(i, newPiece);
                return;
            }

            if (offset < accumulated + pieceLen)
            {
                // Insertion lands inside this piece — split it.
                int splitAt = offset - accumulated;
                var head = _pieces[i].WithLength(splitAt);
                var tail = new Piece(_pieces[i].Buffer, _pieces[i].Start + splitAt, pieceLen - splitAt);

                _pieces[i] = head;
                _pieces.Insert(i + 1, newPiece);
                _pieces.Insert(i + 2, tail);
                return;
            }

            accumulated += pieceLen;
        }

        // Past the last piece — append.
        if (_pieces.Count > 0 && CanCoalesce(_pieces[^1], newPiece))
            _pieces[^1] = _pieces[^1].WithLength(_pieces[^1].Length + newPiece.Length);
        else
            _pieces.Add(newPiece);
    }

    /// <summary>
    /// Deletes <paramref name="length"/> characters starting at <paramref name="offset"/>.
    /// Out-of-range deletions are clamped to the document bounds.
    /// </summary>
    public void Delete(int offset, int length)
    {
        if (length <= 0 || _length == 0) return;
        offset = Math.Clamp(offset, 0, _length);
        length = Math.Min(length, _length - offset);
        if (length == 0) return;

        _length -= length;

        int accumulated = 0;
        int i = 0;
        while (i < _pieces.Count && length > 0)
        {
            int pieceLen = _pieces[i].Length;
            int pieceStart = accumulated;
            int pieceEnd = accumulated + pieceLen;

            if (offset >= pieceEnd)
            {
                // Deletion starts after this piece — skip.
                accumulated += pieceLen;
                i++;
                continue;
            }

            int delStartInPiece = Math.Max(0, offset - pieceStart);
            int delEndInPiece = Math.Min(pieceLen, offset + length - pieceStart);
            int delLen = delEndInPiece - delStartInPiece;

            if (delStartInPiece == 0 && delEndInPiece == pieceLen)
            {
                // Delete entire piece.
                _pieces.RemoveAt(i);
                length -= delLen;
                // Don't advance i or accumulated — the next piece is now at index i.
                continue;
            }
            if (delStartInPiece == 0)
            {
                // Delete from the start of this piece.
                _pieces[i] = new Piece(_pieces[i].Buffer, _pieces[i].Start + delLen, pieceLen - delLen);
                length -= delLen;
                accumulated += pieceLen - delLen;
                i++;
                continue;
            }
            if (delEndInPiece == pieceLen)
            {
                // Delete from the end of this piece.
                _pieces[i] = _pieces[i].WithLength(pieceLen - delLen);
                length -= delLen;
                accumulated += pieceLen - delLen;
                i++;
                continue;
            }

            // Delete a span in the middle — split into two pieces.
            var head = _pieces[i].WithLength(delStartInPiece);
            var tail = new Piece(_pieces[i].Buffer, _pieces[i].Start + delEndInPiece, pieceLen - delEndInPiece);
            _pieces[i] = head;
            _pieces.Insert(i + 1, tail);
            length -= delLen;
            accumulated += head.Length + tail.Length;
            i += 2;
        }
    }

    /// <summary>
    /// Returns the full document text. O(N) materialisation — use sparingly; callers
    /// that need only a window should use <see cref="GetText(int, int)"/>.
    /// </summary>
    public string GetText()
    {
        if (_pieces.Count == 0) return string.Empty;
        var sb = new StringBuilder(_length);
        foreach (var piece in _pieces)
            sb.Append(SpanForPiece(piece));
        return sb.ToString();
    }

    /// <summary>
    /// Returns a slice of the document. Out-of-range arguments are clamped to bounds.
    /// </summary>
    public string GetText(int offset, int length)
    {
        offset = Math.Clamp(offset, 0, _length);
        length = Math.Clamp(length, 0, _length - offset);
        if (length == 0) return string.Empty;

        var sb = new StringBuilder(length);
        int accumulated = 0;
        foreach (var piece in _pieces)
        {
            int pieceStart = accumulated;
            int pieceEnd = accumulated + piece.Length;
            accumulated = pieceEnd;

            if (pieceEnd <= offset) continue;
            if (pieceStart >= offset + length) break;

            int sliceStart = Math.Max(0, offset - pieceStart);
            int sliceEnd = Math.Min(piece.Length, offset + length - pieceStart);
            sb.Append(BufferFor(piece.Buffer), piece.Start + sliceStart, sliceEnd - sliceStart);
        }
        return sb.ToString();
    }

    /// <summary>Returns the character at the given offset, or '\0' if out of range.</summary>
    public char CharAt(int offset)
    {
        if (offset < 0 || offset >= _length) return '\0';
        int accumulated = 0;
        foreach (var piece in _pieces)
        {
            int pieceEnd = accumulated + piece.Length;
            if (offset < pieceEnd)
            {
                return BufferFor(piece.Buffer)[piece.Start + (offset - accumulated)];
            }
            accumulated = pieceEnd;
        }
        return '\0';
    }

    private string BufferFor(BufferKind kind) => kind == BufferKind.Original ? _original : _add.ToString();

    private ReadOnlySpan<char> SpanForPiece(Piece piece)
    {
        var buf = piece.Buffer == BufferKind.Original ? _original.AsSpan() : _add.ToString().AsSpan();
        return buf.Slice(piece.Start, piece.Length);
    }

    private bool CanCoalesce(Piece prev, Piece next)
        => prev.Buffer == BufferKind.Add
        && next.Buffer == BufferKind.Add
        && prev.Start + prev.Length == next.Start;

    internal enum BufferKind { Original, Add }

    internal readonly record struct Piece(BufferKind Buffer, int Start, int Length)
    {
        public Piece WithLength(int length) => this with { Length = length };
    }
}
