using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Services.GlassCatalog;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.GlassCatalog;

[ZemaxToolType]
public class ExportGlassCatalogTool
{
    private readonly IZemaxSession _session;

    public ExportGlassCatalogTool(IZemaxSession session) => _session = session;

    public record ExportResult(
        bool Success,
        string? Error,
        string? OutputPath,
        int GlassCount,
        List<string>? Duplicates
    );

    [ZemaxTool(Name = "zemax_export_glass_catalog")]
    [Description("Export filtered glasses to a new .agf catalog inside the active Zemax Glasscat directory. All requested source catalogs must exist; catalogName is a file name, not a path.")]
    public Task<ExportResult> ExecuteAsync(
        [Description("Name for the new catalog, without path or .agf extension.")] string catalogName,
        [Description("Source catalog name(s), comma-separated. Every requested catalog must exist.")] string sourceCatalogs,
        [Description("Overwrite if catalog already exists")] bool overwrite = false,
        [Description("Only include preferred glasses")] bool? preferredOnly = null,
        [Description("Max weighted distance from target")] double? distanceRadius = null,
        [Description("Distance filter: weight for Nd")] double wn = 1.0,
        [Description("Distance filter: weight for Vd")] double wa = 1E-04,
        [Description("Distance filter: weight for dPgF")] double wp = 1E+02,
        [Description("Distance filter: target Nd")] double ndTarget = 1.5168,
        [Description("Distance filter: target Vd")] double vdTarget = 64.17,
        [Description("Distance filter: target dPgF")] double dpgfTarget = 0.0,
        [Description("Max relative cost")] double? maxCost = null,
        [Description("Minimum Nd")] double? ndMin = null,
        [Description("Maximum Nd")] double? ndMax = null,
        [Description("Minimum Vd")] double? vdMin = null,
        [Description("Maximum Vd")] double? vdMax = null,
        [Description("Minimum dPgF")] double? dpgfMin = null,
        [Description("Maximum dPgF")] double? dpgfMax = null,
        [Description("Minimum TCE")] double? tceMin = null,
        [Description("Maximum TCE")] double? tceMax = null,
        [Description("Glass must transmit down to this wavelength (µm)")] double? minWavelengthCoverage = null,
        [Description("Glass must transmit up to this wavelength (µm)")] double? maxWavelengthCoverage = null,
        [Description("Maximum melt frequency (1-5)")] int? maxMeltFrequency = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CatalogExportService.ValidateCatalogName(catalogName);
            var requestedNames = ParseRequestedCatalogs(sourceCatalogs);
            var criteria = BuildCriteria(preferredOnly, distanceRadius, wn, wa, wp, ndTarget, vdTarget, dpgfTarget,
                maxCost, ndMin, ndMax, vdMin, vdMax, dpgfMin, dpgfMax, tceMin, tceMax,
                minWavelengthCoverage, maxWavelengthCoverage, maxMeltFrequency);
            GlassFilterService.Validate(criteria);

            var glassCatDir = GetGlassCatDir();
            if (glassCatDir == null)
                return Task.FromResult(new ExportResult(false, "Not connected to OpticStudio or Zemax data directory not available.", null, 0, null));

            string outputPath = CatalogExportService.GetCatalogPath(glassCatDir, catalogName);
            if (!overwrite && File.Exists(outputPath))
                return Task.FromResult(new ExportResult(false, $"Catalog '{catalogName}' already exists. Set overwrite=true to replace.", null, 0, null));

            var availableCatalogs = AgfFileParser.DiscoverCatalogs(glassCatDir);
            var missing = requestedNames.Where(name => !availableCatalogs.ContainsKey(name)).ToArray();
            if (missing.Length > 0)
                return Task.FromResult(new ExportResult(false, $"Source catalogs not found: {string.Join(", ", missing)}", null, 0, null));

            var allGlasses = new List<ZemaxMCP.Core.Models.GlassEntry>();
            foreach (var name in requestedNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                allGlasses.AddRange(AgfFileParser.ParseCatalog(availableCatalogs[name], name));
            }

            var filtered = GlassFilterService.Apply(allGlasses, criteria);
            if (filtered.Count == 0)
                return Task.FromResult(new ExportResult(false, "No glasses match the filter criteria.", null, 0, null));

            var duplicates = CatalogExportService.FindDuplicateNames(filtered);
            if (duplicates.Count > 0)
                return Task.FromResult(new ExportResult(false, "Duplicate glass names found across source catalogs.", null, 0, duplicates));

            cancellationToken.ThrowIfCancellationRequested();
            CatalogExportService.Export(filtered, outputPath, catalogName);
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new IOException($"Catalog export completed without a non-empty output file at '{outputPath}'.");

            return Task.FromResult(new ExportResult(true, null, outputPath, filtered.Count, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ExportResult(false, ex.Message, null, 0, null));
        }
    }

    private static string[] ParseRequestedCatalogs(string sourceCatalogs)
    {
        if (string.IsNullOrWhiteSpace(sourceCatalogs))
            throw new ArgumentException("At least one source catalog is required.", nameof(sourceCatalogs));
        var names = sourceCatalogs.Split(',')
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
            throw new ArgumentException("At least one source catalog is required.", nameof(sourceCatalogs));
        return names;
    }

    private static GlassFilterCriteria BuildCriteria(
        bool? preferredOnly, double? distanceRadius, double wn, double wa, double wp,
        double ndTarget, double vdTarget, double dpgfTarget, double? maxCost,
        double? ndMin, double? ndMax, double? vdMin, double? vdMax,
        double? dpgfMin, double? dpgfMax, double? tceMin, double? tceMax,
        double? minWavelengthCoverage, double? maxWavelengthCoverage, int? maxMeltFrequency)
    {
        return new GlassFilterCriteria
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
    }

    private string? GetGlassCatDir()
    {
        if (!_session.IsConnected || string.IsNullOrEmpty(_session.ZemaxDataDir)) return null;
        var dir = Path.Combine(_session.ZemaxDataDir, "Glasscat");
        return Directory.Exists(dir) ? dir : null;
    }
}
