namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>
/// Validates bounded, absolute LDE windows without relying on a ZOS-API
/// installation. Adjacent windows can overlap to preserve transfer evidence.
/// </summary>
internal static class SequentialEnergySurfaceRange
{
    internal const int MaxSurfacesPerWindow = 24;

    internal static (int Start, int Last, int Count) Resolve(
        int startSurface, int finalSurface, int lastLde)
    {
        if (lastLde < 1)
            throw new ArgumentException("The sequential lens has no target surfaces.", nameof(lastLde));
        if (startSurface < 1 || startSurface > lastLde)
            throw new ArgumentOutOfRangeException(nameof(startSurface), "Start must be an existing LDE surface >= 1.");
        if (finalSurface < 0 || finalSurface > lastLde)
            throw new ArgumentOutOfRangeException(nameof(finalSurface), "Final must be 0 (image) or an existing LDE surface.");

        var last = finalSurface == 0 ? lastLde : finalSurface;
        if (last < startSurface)
            throw new ArgumentException("Final surface cannot precede startSurface.");
        var count = last - startSurface + 1;
        if (count > MaxSurfacesPerWindow)
            throw new ArgumentException(
                "Energy budget supports up to 24 surfaces per window. " +
                "For a longer LDE use overlapping windows (1..24, 24..47).");
        return (startSurface, last, count);
    }
}
