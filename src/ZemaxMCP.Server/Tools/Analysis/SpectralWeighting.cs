namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>
/// Pure, bounded spectral aggregation. The normalized input weights represent
/// the user's relative source power at the SELECTED discrete wavelengths,
/// never a substitute for a physical source spectrum or detector QE.
/// </summary>
internal static class SpectralWeighting
{
    internal static double[] Normalize(double[] weights, int wavelengthCount)
    {
        if (weights == null || wavelengthCount is < 1 or > 6 ||
            weights.Length != wavelengthCount ||
            weights.Any(x => !double.IsFinite(x) || x < 0))
            throw new ArgumentException("Source spectral weights must match selected wavelengths and be finite nonnegative values.");
        var maximum = weights.Max();
        if (maximum <= 0)
            throw new ArgumentException("At least one source spectral weight must be positive.");
        // Scale before summing to avoid overflow when users supply large
        // finite relative spectral weights.
        var scaled = weights.Select(value => value / maximum).ToArray();
        var sum = scaled.Sum();
        return scaled.Select(value => value / sum).ToArray();
    }

    internal static double WeightedMean(double[] values, double[] normalizedWeights)
    {
        if (values.Length != normalizedWeights.Length ||
            values.Any(x => !double.IsFinite(x)) ||
            normalizedWeights.Any(x => !double.IsFinite(x) || x < 0))
            throw new InvalidDataException("Non-finite spectral ray proxy or mismatched weights.");
        var sum = 0d;
        for (var i = 0; i < values.Length; i++)
            sum += values[i] * normalizedWeights[i];
        if (!double.IsFinite(sum))
            throw new InvalidDataException("Spectral weighted result is non-finite.");
        return sum;
    }
}
