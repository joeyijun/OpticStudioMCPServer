using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Services.GlassCatalog;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.GlassCatalog;

[ZemaxToolType]
public class GetGlassesTool
{
    private readonly IZemaxSession _session;

    public GetGlassesTool(IZemaxSession session) => _session = session;

    public record GlassInfo(
        string Name,
        string Catalog,
        double Nd,
        double Vd,
        double DPgF,
        string Status,
        double TCE,
        double RelativeCost,
        double MinWavelength,
        double MaxWavelength,
        double Density,
        int MeltFrequency,
        string? Comment
    );

    public record GetGlassesResult(bool Success, string? Error, int TotalCount, List<GlassInfo>? Glasses);

    [ZemaxTool(Name = "zemax_get_glasses")]
    [Description("List glasses in one or more installed AGF catalogs. Every requested catalog must exist; unknown names are not silently skipped.")]
    public Task<GetGlassesResult> ExecuteAsync(
        [Description("Catalog name(s), comma-separated (for example SCHOTT or SCHOTT,OHARA)")] string catalogs,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestedNames = ParseRequestedCatalogs(catalogs);
            var glassCatDir = GetGlassCatDir();
            if (glassCatDir == null)
                return Task.FromResult(new GetGlassesResult(false, "Not connected to OpticStudio or Zemax data directory not available.", 0, null));

            var availableCatalogs = AgfFileParser.DiscoverCatalogs(glassCatDir);
            var missing = requestedNames.Where(name => !availableCatalogs.ContainsKey(name)).ToArray();
            if (missing.Length > 0)
                return Task.FromResult(new GetGlassesResult(false, $"Catalogs not found: {string.Join(", ", missing)}", 0, null));

            var allGlasses = new List<GlassInfo>();
            foreach (var name in requestedNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var glasses = AgfFileParser.ParseCatalog(availableCatalogs[name], name);
                allGlasses.AddRange(glasses.Select(g => new GlassInfo(
                    g.Name, g.CatalogName, g.Nd, g.Vd,
                    Math.Round(g.DPgF, 6), g.StatusText,
                    g.TCE, g.RelativeCost,
                    g.MinWavelength, g.MaxWavelength,
                    g.Density, g.MeltFrequency, g.Comment)));
            }

            return Task.FromResult(new GetGlassesResult(true, null, allGlasses.Count, allGlasses));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new GetGlassesResult(false, ex.Message, 0, null));
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
