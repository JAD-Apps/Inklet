using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace Inklet.Engine;

/// <summary>
/// Read-only random-access bytes of an opened file. Implementations must be
/// safe for concurrent reads from any thread.
/// </summary>
internal interface IByteSource : IDisposable
{
    long Length { get; }
    /// <summary>Zero-copy view of [offset, offset+length). length is bounded by callers.</summary>
    ReadOnlySpan<byte> GetSpan(long offset, int length);
    void CopyTo(long offset, Span<byte> destination);
}

/// <summary>
/// Whole-file memory-mapped view (x64/ARM64: address space is free). The file is
/// opened with FileShare.ReadWrite | Delete so external writers, deletes and our
/// own save-over-self replace keep working; the mapping pins the old file data.
/// </summary>
internal sealed unsafe class MemoryMappedByteSource : IByteSource
{
    private readonly FileStream _stream;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _base;
    private bool _disposed;

    public long Length { get; }

    public MemoryMappedByteSource(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.RandomAccess);
        Length = _stream.Length;
        if (Length == 0)
        {
            // Zero-length files cannot be mapped; model as empty.
            _mmf = null!;
            _view = null!;
            _base = null;
            return;
        }
        _mmf = MemoryMappedFile.CreateFromFile(_stream, mapName: null, capacity: 0,
            MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        _view = _mmf.CreateViewAccessor(0, Length, MemoryMappedFileAccess.Read);
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _base);
        _base += _view.PointerOffset;
    }

    public ReadOnlySpan<byte> GetSpan(long offset, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((ulong)offset > (ulong)Length || (uint)length > (ulong)(Length - offset))
            throw new ArgumentOutOfRangeException(nameof(offset));
        return new ReadOnlySpan<byte>(_base + offset, length);
    }

    public void CopyTo(long offset, Span<byte> destination)
        => GetSpan(offset, destination.Length).CopyTo(destination);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_base is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
        }
        _view?.Dispose();
        _mmf?.Dispose();
        _stream.Dispose();
    }
}

/// <summary>In-memory byte source (untitled documents, session restore, tests).</summary>
internal sealed class ArrayByteSource : IByteSource
{
    private readonly byte[] _bytes;

    public ArrayByteSource(byte[] bytes) => _bytes = bytes;

    public long Length => _bytes.Length;

    public ReadOnlySpan<byte> GetSpan(long offset, int length)
        => _bytes.AsSpan((int)offset, length);

    public void CopyTo(long offset, Span<byte> destination)
        => _bytes.AsSpan((int)offset, destination.Length).CopyTo(destination);

    public void Dispose() { }
}
