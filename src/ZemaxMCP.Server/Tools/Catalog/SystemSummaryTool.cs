using System.ComponentModel;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;
using ZOSAPI;

namespace ZemaxMCP.Server.Tools.Catalog;

/// <summary>
/// Single read-only, bounded model inventory for AI preflight. One serialized
/// ZOS-API operation guarantees internally consistent metadata, without
/// loading every LDE/NCE row into a potentially enormous MCP response.
/// </summary>
[ZemaxToolType]
public sealed class SystemSummaryTool
{
    private readonly IZemaxSession _session;
    public SystemSummaryTool(IZemaxSession session) => _session = session;

    public sealed record SurfaceSummary(int Number, string Type, string? Comment,
        string? Material, bool IsStop, bool IsMirror, bool IsCoordinateBreak,
        double? Radius, double? Thickness, double? SemiDiameter);
    public sealed record FieldSummary(int Number, double? X, double? Y,
        double? Weight, bool IsActive);
    public sealed record WavelengthSummary(int Number, double? Micrometers,
        double? Weight, bool IsPrimary, bool IsActive);
    public sealed record Result(bool Success, string? Error,
        string Mode, string? FilePath, string? Title, bool NeedsSave,
        string LensUnit, string? FieldType, string? ApertureType, double? ApertureValue,
        int NumberOfSurfaces, int NumberOfNscObjects,
        int NumberOfFields, int NumberOfWavelengths,
        int Configurations, int CurrentConfiguration,
        IReadOnlyList<SurfaceSummary> KeySurfaces,
        IReadOnlyList<FieldSummary> Fields,
        IReadOnlyList<WavelengthSummary> Wavelengths,
        int OmittedSurfaces, int OmittedFields, int OmittedWavelengths,
        IReadOnlyList<string> Warnings, string Interpretation);

    private static double? Finite(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? null : value;

    [ZemaxTool(Name = "zemax_system_summary")]
    [Description("One-call compact read-only active-model preflight: mode, units, aperture, field/wavelength samples, stop/mirror/coordinate-break surfaces, configuration and save state. Bounded output; no ray tracing, lens changes, or assumption that semi-diameter is an actual mechanical aperture.")]
    public async Task<Result> ExecuteAsync(
        [Description("Maximum reported key LDE surfaces, from 4 to 24 (default 12).")] int maxSurfaces = 12,
        [Description("Maximum field records, from 1 to 16 (default 8).")] int maxFields = 8,
        [Description("Maximum wavelength records, from 1 to 16 (default 8).")] int maxWavelengths = 8,
        CancellationToken cancellationToken = default)
    {
        const string meaning = "This is a snapshot of active-model metadata, NOT an optical analysis or a mechanical clipping test. Coordinates/lengths use native lens units; wavelength values are micrometers and field coordinates use the declared field type. Local Coordinate Breaks affect per-surface ray coordinates. Semidiameter is informational, not necessarily an enforced clear aperture.";
        if (maxSurfaces is < 4 or > 24 || maxFields is < 1 or > 16 ||
            maxWavelengths is < 1 or > 16)
            return new Result(false, "maxSurfaces must be 4..24; maxFields and maxWavelengths must be 1..16.",
                "unknown", null, null, false, "", null, null, null, 0, 0,
                0, 0, 0, 0, Array.Empty<SurfaceSummary>(), Array.Empty<FieldSummary>(),
                Array.Empty<WavelengthSummary>(), 0, 0, 0, new[] { "Invalid bounded output parameters." }, meaning);

        try
        {
            return await _session.ExecuteAsync("SystemSummary", new Dictionary<string, object?>
            {
                ["maxSurfaces"] = maxSurfaces,
                ["maxFields"] = maxFields,
                ["maxWavelengths"] = maxWavelengths
            }, system =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sys = system.SystemData;
                var sequential = system.Mode == SystemType.Sequential;
                var totalSurfaces = sequential ? system.LDE.NumberOfSurfaces : 0;
                var nscObjects = sequential ? 0 : system.NCE.NumberOfObjects;
                var fieldCount = sequential ? sys.Fields.NumberOfFields : 0;
                var wavelengthCount = sys.Wavelengths.NumberOfWavelengths;
                var warnings = new List<string>();
                var surfaces = new List<SurfaceSummary>();
                if (sequential)
                {
                    // A full LDE can have thousands of rows. Scan a bounded
                    // prefix, and ALWAYS inspect the image plane separately.
                    // Keep stop/mirror/coordinate-break rows ahead of ordinary
                    // intermediate surfaces without pretending we inspected
                    // the unscanned portion.
                    const int scanCap = 512;
                    var scanned = Math.Min(totalSurfaces, scanCap);
                    var candidates = new List<SurfaceSummary>();
                    for (var i = 0; i < scanned; i++)
                    {
                        if ((i & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                        var row = system.LDE.GetSurfaceAt(i);
                        var type = row.Type.ToString();
                        candidates.Add(new SurfaceSummary(i, type, row.Comment,
                            row.Material, row.IsStop,
                            string.Equals(row.Material?.Trim(), "MIRROR", StringComparison.OrdinalIgnoreCase),
                            type.IndexOf("CoordinateBreak", StringComparison.OrdinalIgnoreCase) >= 0,
                            Finite(row.Radius), Finite(row.Thickness), Finite(row.SemiDiameter)));
                    }
                    if (totalSurfaces > scanned)
                    {
                        var last = totalSurfaces - 1;
                        var row = system.LDE.GetSurfaceAt(last);
                        var type = row.Type.ToString();
                        candidates.Add(new SurfaceSummary(last, type, row.Comment,
                            row.Material, row.IsStop,
                            string.Equals(row.Material?.Trim(), "MIRROR", StringComparison.OrdinalIgnoreCase),
                            type.IndexOf("CoordinateBreak", StringComparison.OrdinalIgnoreCase) >= 0,
                            Finite(row.Radius), Finite(row.Thickness), Finite(row.SemiDiameter)));
                        warnings.Add("Only the first 512 LDE rows and final image row were inspected for key geometry; later intermediate stops/mirrors/coordinate breaks are not ruled out.");
                    }
                    var image = totalSurfaces - 1;
                    surfaces = candidates
                        .OrderBy(x => x.Number == 0 ? 0 : x.Number == image ? 1 :
                            x.IsStop ? 2 : x.IsMirror ? 3 : x.IsCoordinateBreak ? 4 : 5)
                        .ThenBy(x => x.Number)
                        .Take(maxSurfaces).OrderBy(x => x.Number).ToList();
                    if (candidates.Count > surfaces.Count)
                        warnings.Add("Key surfaces are a priority sample, not an exhaustive LDE export. Use zemax_get_surface for a specific row.");
                    if (!candidates.Any(x => x.IsStop))
                        warnings.Add("No stop surface was identified in the inspected rows; do not assume the model has no stop.");
                    if (candidates.Any(x => x.IsCoordinateBreak))
                        warnings.Add("Coordinate Break surfaces are present; surface intercepts use changing LOCAL coordinates.");
                }
                else
                    warnings.Add("NSC geometry is represented by object count only. Use zemax_nsc_scene_summary / zemax_get_nsc_objects to inspect sources, detectors, and geometry.");

                var fields = new List<FieldSummary>();
                if (sequential)
                {
                    for (int i = 1; i <= Math.Min(fieldCount, maxFields); i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var fld = sys.Fields.GetField(i);
                        fields.Add(new FieldSummary(i, Finite(fld.X), Finite(fld.Y),
                            Finite(fld.Weight), fld.IsActive));
                    }
                }
                var wavelengths = new List<WavelengthSummary>();
                for (int i = 1; i <= Math.Min(wavelengthCount, maxWavelengths); i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var wl = sys.Wavelengths.GetWavelength(i);
                    wavelengths.Add(new WavelengthSummary(i, Finite(wl.Wavelength),
                        Finite(wl.Weight), wl.IsPrimary, wl.IsActive));
                }
                if (fieldCount > fields.Count || wavelengthCount > wavelengths.Count)
                    warnings.Add("Field/wavelength lists are truncated; index-dependent calculations must use the full configuration readback.");
                if (!wavelengths.Any(x => x.IsPrimary) && wavelengthCount > wavelengths.Count)
                    warnings.Add("The primary wavelength may lie beyond the sampled wavelength list.");
                if (!system.NeedsSave && string.IsNullOrWhiteSpace(system.SystemFile))
                    warnings.Add("Active system has no reported saved file path; an in-memory design is not evidence of a durable snapshot.");

                return new Result(true, null, system.Mode.ToString(),
                    system.SystemFile, sys.TitleNotes.Title, system.NeedsSave,
                    sys.Units.LensUnits.ToString(),
                    sequential ? sys.Fields.GetFieldType().ToString() : null,
                    sequential ? sys.Aperture.ApertureType.ToString() : null,
                    sequential ? Finite(sys.Aperture.ApertureValue) : null,
                    totalSurfaces, nscObjects, fieldCount, wavelengthCount,
                    system.MCE.NumberOfConfigurations, system.MCE.CurrentConfiguration,
                    surfaces, fields, wavelengths, Math.Max(0, totalSurfaces - surfaces.Count),
                    Math.Max(0, fieldCount - fields.Count),
                    Math.Max(0, wavelengthCount - wavelengths.Count), warnings, meaning);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, "unavailable", null, null, false,
                "", null, null, null, 0, 0, 0, 0, 0, 0,
                Array.Empty<SurfaceSummary>(), Array.Empty<FieldSummary>(),
                Array.Empty<WavelengthSummary>(), 0, 0, 0,
                new[] { "Model summary failed; do not infer geometry or wavelength defaults." }, meaning);
        }
    }
}
