using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Services.GlassCatalog;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.GlassCatalog;

[ZemaxToolType]
public class FilterGlassesTool
{
    private readonly IZemaxSession _session;

    public FilterGlassesTool(IZemaxSession session) => _session = session;

    public record FilteredGlassInfo(
        string Name,
        string Catalog,
        double Nd,
        double Vd,
        double DPgF,
        string Status,
        double TCE,
        double RelativeCost,
        double MinWavelength,
        double MaxWavelength
    );

    public record FilterGlassesResult(
        bool Success,
        string? Error,
        int TotalBeforeFilter,
        int TotalAfterFilter,
        List<FilteredGlassInfo>? Glasses
    );

    [ZemaxTool(Name = "zemax_filter_glasses")]
    [Description("Filter glasses from installed AGF catalogs. All requested catalogs and numeric criteria are validated before filtering; unknown catalogs are not silently skipped.")]
    public Task<FilterGlassesResult> ExecuteAsync(
        [Description("Catalog name(s), comma-separated (for example SCHOTT,OHARA)")] string catalogs,
        [Description("Only include preferred glasses (status=1)")] bool? preferredOnly = null,
        [Description("Max weighted distance from target Nd/Vd/dPgF point; must be >= 0")] double? distanceRadius = null,
        [Description("Distance filter: non-negative weight for Nd (default 1.0)")] double wn = 1.0,
        [Description("Distance filter: non-negative weight for Vd (default 1E-4)")] double wa = 1E-04,
        [Description("Distance filter: non-negative weight for dPgF (default 1E+2)")] double wp = 1E+02,
        [Description("Distance filter: target Nd (default 1.5168)")] double ndTarget = 1.5168,
        [Description("Distance filter: target Vd (default 64.17)")] double vdTarget = 64.17,
        [Description("Distance filter: target dPgF (default 0.0)")] double dpgfTarget = 0.0,
        [Description("Max relative cost (BK7 = 1.0); must be >= 0")] double? maxCost = null,
        [Description("Minimum Nd")] double? ndMin = null,
        [Description("Maximum Nd")] double? ndMax = null,
        [Description("Minimum Vd")] double? vdMin = null,
        [Description("Maximum Vd")] double? vdMax = null,
        [Description("Minimum dPgF")] double? dpgfMin = null,
        [Description("Maximum dPgF")] double? dpgfMax = null,
        [Description("Minimum TCE (x10^-6/K)")] double? tceMin = null,
        [Description("Maximum TCE (x10^-6/K)")] double? tceMax = null,
        [Description("Glass must transmit down to this positive wavelength (µm)")] double? minWavelengthCoverage = null,
        [Description("Glass must transmit up to this positive wavelength (µm)")] double? maxWavelengthCoverage = null,
        [Description("Maximum melt frequency (1-5)")] int? maxMeltFrequency = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestedNames = ParseRequestedCatalogs(catalogs);
            var criteria = new GlassFilterCriteria
            {
                PreferredOnly = preferredOnly,
                DistanceRadius = distanceRadius,
                Wn = wn,
                Wa = wa,
                Wp = wp,
                NdTarget = ndTarget,
                VdTarget = vdTarget,
                DPgFTarget = dpgfTarget,
                MaxCost = maxCost,
                NdMin = ndMin,
                NdMax = ndMax,
                VdMin = vdMin,
                VdMax = vdMax,
                DPgFMin = dpgfMin,
                DPgFMax = dpgfMax,
                TCEMin = tceMin,
                TCEMax = tceMax,
                MinWavelengthCoverage = minWavelengthCoverage,
                MaxWavelengthCoverage = maxWavelengthCoverage,
                MaxMeltFrequency = maxMeltFrequency
            };
            GlassFilterService.Validate(criteria);

            var glassCatDir = GetGlassCatDir();
            if (glassCatDir == null)
                return Task.FromResult(new FilterGlassesResult(false, "Not connected to OpticStudio or Zemax data directory not available.", 0, 0, null));

            var availableCatalogs = AgfFileParser.DiscoverCatalogs(glassCatDir);
            var missing = requestedNames.Where(name => !availableCatalogs.ContainsKey(name)).ToArray();
            if (missing.Length > 0)
                return Task.FromResult(new FilterGlassesResult(false, $"Catalogs not found: {string.Join(", ", missing)}", 0, 0, null));

            var allGlasses = new List<ZemaxMCP.Core.Models.GlassEntry>();
            foreach (var name in requestedNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                allGlasses.AddRange(AgfFileParser.ParseCatalog(availableCatalogs[name], name));
            }

            int totalBefore = allGlasses.Count;
            var filtered = GlassFilterService.Apply(allGlasses, criteria);
            var result = filtered.Select(g => new FilteredGlassInfo(
                g.Name, g.CatalogName, g.Nd, g.Vd,
                Math.Round(g.DPgF, 6), g.StatusText,
                g.TCE, g.RelativeCost,
                g.MinWavelength, g.MaxWavelength)).ToList();

            return Task.FromResult(new FilterGlassesResult(true, null, totalBefore, result.Count, result));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new FilterGlassesResult(false, ex.Message, 0, 0, null));
        }
    }

    private static string[] ParseRequestedCatalogs(string catalogs)
    {
        if (string.IsNullOrWhiteSpace(catalogs))
            throw new ArgumentException("At least one catalog name is required.", nameof(catalogs));
        var names = catalogs.Split(',')
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
            throw new ArgumentException("At least one catalog name is required.", nameof(catalogs));
        return names;
    }

    private string? GetGlassCatDir()
    {
        if (!_session.IsConnected || string.IsNullOrEmpty(_session.ZemaxDataDir)) return null;
        var dir = Path.Combine(_session.ZemaxDataDir, "Glasscat");
        return Directory.Exists(dir) ? dir : null;
    }
}
