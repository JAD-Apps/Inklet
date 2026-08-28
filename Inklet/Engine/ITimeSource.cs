using System;

namespace Inklet.Engine;

/// <summary>
/// Injectable clock so undo-coalescing windows are deterministic under test.
/// </summary>
internal interface ITimeSource
{
    DateTime UtcNow { get; }
}

internal sealed class SystemTimeSource : ITimeSource
{
    public static readonly SystemTimeSource Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
}
