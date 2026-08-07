using ZemaxMCP.Core.Models;

namespace ZemaxMCP.Core.Services.GlassCatalog;

public record GlassFilterCriteria
{
    public bool? PreferredOnly { get; init; }
    public double? DistanceRadius { get; init; }
    public double Wn { get; init; } = 1.0;
    public double Wa { get; init; } = 1E-04;
    public double Wp { get; init; } = 1E+02;
    public double NdTarget { get; init; } = 1.5168;
    public double VdTarget { get; init; } = 64.17;
    public double DPgFTarget { get; init; } = 0.0;
    public double? MaxCost { get; init; }
    public double? NdMin { get; init; }
    public double? NdMax { get; init; }
    public double? VdMin { get; init; }
    public double? VdMax { get; init; }
    public double? DPgFMin { get; init; }
    public double? DPgFMax { get; init; }
    public double? TCEMin { get; init; }
    public double? TCEMax { get; init; }
    public double? MinWavelengthCoverage { get; init; }
    public double? MaxWavelengthCoverage { get; init; }
    public int? MaxMeltFrequency { get; init; }
}

public static class GlassFilterService
{
    public static List<GlassEntry> Apply(IEnumerable<GlassEntry> glasses, GlassFilterCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(glasses);
        ArgumentNullException.ThrowIfNull(criteria);
        Validate(criteria);
        return glasses.Where(g => PassesAllFilters(g, criteria)).ToList();
    }

    public static void Validate(GlassFilterCriteria c)
    {
        ValidateFinite(c.DistanceRadius, nameof(c.DistanceRadius));
        ValidateFinite(c.Wn, nameof(c.Wn));
        ValidateFinite(c.Wa, nameof(c.Wa));
        ValidateFinite(c.Wp, nameof(c.Wp));
        ValidateFinite(c.NdTarget, nameof(c.NdTarget));
        ValidateFinite(c.VdTarget, nameof(c.VdTarget));
        ValidateFinite(c.DPgFTarget, nameof(c.DPgFTarget));
        ValidateFinite(c.MaxCost, nameof(c.MaxCost));
        ValidateFinite(c.NdMin, nameof(c.NdMin));
        ValidateFinite(c.NdMax, nameof(c.NdMax));
        ValidateFinite(c.VdMin, nameof(c.VdMin));
        ValidateFinite(c.VdMax, nameof(c.VdMax));
        ValidateFinite(c.DPgFMin, nameof(c.DPgFMin));
        ValidateFinite(c.DPgFMax, nameof(c.DPgFMax));
        ValidateFinite(c.TCEMin, nameof(c.TCEMin));
        ValidateFinite(c.TCEMax, nameof(c.TCEMax));
        ValidateFinite(c.MinWavelengthCoverage, nameof(c.MinWavelengthCoverage));
        ValidateFinite(c.MaxWavelengthCoverage, nameof(c.MaxWavelengthCoverage));

        if (c.DistanceRadius is < 0) throw new ArgumentOutOfRangeException(nameof(c.DistanceRadius), "DistanceRadius must be >= 0.");
        if (c.Wn < 0 || c.Wa < 0 || c.Wp < 0)
            throw new ArgumentOutOfRangeException(nameof(c.Wn), "Distance weights Wn, Wa, and Wp must be >= 0.");
        if (c.MaxCost is < 0) throw new ArgumentOutOfRangeException(nameof(c.MaxCost), "MaxCost must be >= 0.");
        ValidateRange(c.NdMin, c.NdMax, "Nd");
        ValidateRange(c.VdMin, c.VdMax, "Vd");
        ValidateRange(c.DPgFMin, c.DPgFMax, "dPgF");
        ValidateRange(c.TCEMin, c.TCEMax, "TCE");
        if (c.MinWavelengthCoverage is <= 0)
            throw new ArgumentOutOfRangeException(nameof(c.MinWavelengthCoverage), "Minimum wavelength coverage must be > 0 µm.");
        if (c.MaxWavelengthCoverage is <= 0)
            throw new ArgumentOutOfRangeException(nameof(c.MaxWavelengthCoverage), "Maximum wavelength coverage must be > 0 µm.");
        if (c.MinWavelengthCoverage.HasValue && c.MaxWavelengthCoverage.HasValue &&
            c.MinWavelengthCoverage.Value > c.MaxWavelengthCoverage.Value)
            throw new ArgumentException("Minimum wavelength coverage cannot exceed maximum wavelength coverage.");
        if (c.MaxMeltFrequency.HasValue && (c.MaxMeltFrequency.Value < 1 || c.MaxMeltFrequency.Value > 5))
            throw new ArgumentOutOfRangeException(nameof(c.MaxMeltFrequency), "MaxMeltFrequency must be in 1..5.");
    }

    private static void ValidateFinite(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite.");
    }

    private static void ValidateFinite(double? value, string name)
    {
        if (value.HasValue) ValidateFinite(value.Value, name);
    }

    private static void ValidateRange(double? min, double? max, string label)
    {
        if (min.HasValue && max.HasValue && min.Value > max.Value)
            throw new ArgumentException($"{label} minimum ({min.Value}) cannot exceed maximum ({max.Value}).");
    }

    private static bool PassesAllFilters(GlassEntry g, GlassFilterCriteria c)
    {
        if (c.PreferredOnly == true && g.Status != 1) return false;

        if (c.DistanceRadius.HasValue)
        {
            double dNd = g.Nd - c.NdTarget;
            double dVd = g.Vd - c.VdTarget;
            double dPgF = g.DPgF - c.DPgFTarget;
            double d = Math.Sqrt(c.Wn * dNd * dNd + c.Wa * dVd * dVd + c.Wp * dPgF * dPgF);
            if (d > c.DistanceRadius.Value) return false;
        }

        if (c.MaxCost.HasValue && (g.RelativeCost <= 0 || g.RelativeCost > c.MaxCost.Value)) return false;
        if (c.NdMin.HasValue && g.Nd < c.NdMin.Value) return false;
        if (c.NdMax.HasValue && g.Nd > c.NdMax.Value) return false;
        if (c.VdMin.HasValue && g.Vd < c.VdMin.Value) return false;
        if (c.VdMax.HasValue && g.Vd > c.VdMax.Value) return false;
        if (c.DPgFMin.HasValue && g.DPgF < c.DPgFMin.Value) return false;
        if (c.DPgFMax.HasValue && g.DPgF > c.DPgFMax.Value) return false;
        if (c.TCEMin.HasValue && (g.TCE < 0 || g.TCE < c.TCEMin.Value)) return false;
        if (c.TCEMax.HasValue && (g.TCE < 0 || g.TCE > c.TCEMax.Value)) return false;
        if (c.MinWavelengthCoverage.HasValue && (g.MinWavelength < 0 || g.MinWavelength > c.MinWavelengthCoverage.Value)) return false;
        if (c.MaxWavelengthCoverage.HasValue && (g.MaxWavelength < 0 || g.MaxWavelength < c.MaxWavelengthCoverage.Value)) return false;
        if (c.MaxMeltFrequency.HasValue && (g.MeltFrequency < 1 || g.MeltFrequency > c.MaxMeltFrequency.Value)) return false;
        return true;
    }
}
