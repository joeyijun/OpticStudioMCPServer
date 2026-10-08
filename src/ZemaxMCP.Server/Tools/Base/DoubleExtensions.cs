namespace ZemaxMCP.Server.Tools.Base;

/// <summary>
/// Extension methods for preserving truthful floating-point values at the JSON
/// boundary. Unexpected NaN/Infinity values are rejected instead of being
/// rewritten into plausible finite measurements.
/// </summary>
public static class DoubleExtensions
{
    public static double? OpticalDimension(this double value)
    {
        if (double.IsNaN(value))
            throw new InvalidDataException("ZOS-API returned NaN for an optical dimension.");
        return double.IsInfinity(value) ? null : value;
    }

    public static string OpticalDimensionState(this double value) =>
        double.IsPositiveInfinity(value) ? "PositiveInfinity" :
        double.IsNegativeInfinity(value) ? "NegativeInfinity" :
        double.IsNaN(value) ? throw new InvalidDataException("ZOS-API returned NaN for an optical dimension.") : "Finite";

    /// <summary>
    /// Returns a finite value unchanged. Non-finite ZOS-API results are data
    /// integrity failures and must be surfaced to the caller rather than
    /// fabricated as zero or a large finite number.
    /// </summary>
    /// <param name="value">The value to validate.</param>
    /// <param name="infinityReplacement">
    /// Retained for source compatibility with older internal callers; no
    /// replacement is performed.
    /// </param>
    public static double Sanitize(this double value, double infinityReplacement = 1e18)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidDataException($"ZOS-API returned a non-finite numeric value: {value}.");
        return value;
    }

    /// <summary>
    /// Converts an infinite optical radius to the established API convention
    /// of zero for a plane surface. NaN is never a valid plane-radius marker
    /// and is rejected explicitly.
    /// </summary>
    public static double SanitizeRadius(this double radius)
    {
        if (double.IsNaN(radius))
            throw new InvalidDataException("ZOS-API returned NaN for an optical surface radius.");
        if (double.IsInfinity(radius))
            return 0;
        return radius;
    }
}
