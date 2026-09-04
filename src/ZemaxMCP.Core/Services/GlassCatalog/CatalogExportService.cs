using System.Text;
using ZemaxMCP.Core.Models;

namespace ZemaxMCP.Core.Services.GlassCatalog;

public static class CatalogExportService
{
    public static List<string> FindDuplicateNames(IEnumerable<GlassEntry> glasses)
    {
        return glasses
            .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Where(grp => grp.Count() > 1)
            .Select(grp => $"{grp.Key} ({string.Join(", ", grp.Select(g => g.CatalogName))})")
            .ToList();
    }

    public static bool CatalogExists(string glassCatDir, string catalogName)
    {
        if (string.IsNullOrWhiteSpace(glassCatDir) || !Directory.Exists(glassCatDir))
            return false;
        return File.Exists(GetCatalogPath(glassCatDir, catalogName));
    }

    public static string GetCatalogPath(string glassCatDir, string catalogName)
    {
        if (string.IsNullOrWhiteSpace(glassCatDir))
            throw new ArgumentException("Glass catalog directory is required.", nameof(glassCatDir));
        ValidateCatalogName(catalogName);

        string root = Path.GetFullPath(glassCatDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string target = Path.GetFullPath(Path.Combine(root, catalogName + ".agf"));
        string prefix = root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Catalog output path escapes the Zemax Glasscat directory.");
        return target;
    }

    public static void ValidateCatalogName(string catalogName)
    {
        if (string.IsNullOrWhiteSpace(catalogName))
            throw new ArgumentException("Catalog name is required.", nameof(catalogName));
        if (!string.Equals(catalogName, catalogName.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Catalog name cannot have leading or trailing whitespace.", nameof(catalogName));
        if (catalogName.EndsWith(".", StringComparison.Ordinal))
            throw new ArgumentException("Catalog name cannot end with a period.", nameof(catalogName));
        if (catalogName.Equals(".", StringComparison.Ordinal) || catalogName.Equals("..", StringComparison.Ordinal))
            throw new ArgumentException("Catalog name is invalid.", nameof(catalogName));
        if (catalogName.EndsWith(".agf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Catalog name must not include the .agf extension.", nameof(catalogName));

        const string forbidden = "<>:\"/\\|?*";
        if (catalogName.Any(ch => char.IsControl(ch) || forbidden.IndexOf(ch) >= 0))
            throw new ArgumentException("Catalog name contains characters that are not valid in a Windows file name.", nameof(catalogName));

        string deviceStem = catalogName.Split('.')[0].ToUpperInvariant();
        if (deviceStem is "CON" or "PRN" or "AUX" or "NUL" ||
            (deviceStem.Length == 4 &&
             (deviceStem.StartsWith("COM", StringComparison.Ordinal) || deviceStem.StartsWith("LPT", StringComparison.Ordinal)) &&
             deviceStem[3] >= '1' && deviceStem[3] <= '9'))
        {
            throw new ArgumentException($"Catalog name '{catalogName}' is a reserved Windows device name.", nameof(catalogName));
        }
    }

    public static void Export(IEnumerable<GlassEntry> glasses, string outputPath, string catalogName, bool overwrite)
    {
        if (glasses == null) throw new ArgumentNullException(nameof(glasses));
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Output path is required.", nameof(outputPath));
        ValidateCatalogName(catalogName);

        string fullOutputPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullOutputPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Catalog output directory does not exist: {directory}");

        string tempPath = Path.Combine(directory, $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var writer = new StreamWriter(tempPath, false, Encoding.ASCII))
            {
                writer.WriteLine($"CC {catalogName} - Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                foreach (var glass in glasses)
                {
                    foreach (var line in glass.RawLines)
                        writer.WriteLine(line);
                }
            }

            if (overwrite)
            {
                if (File.Exists(fullOutputPath))
                    File.Replace(tempPath, fullOutputPath, null);
                else
                    File.Move(tempPath, fullOutputPath);
            }
            else
            {
                // File.Move is the final no-clobber gate. If another process creates
                // the target after the earlier friendly existence check, this fails
                // rather than silently replacing that file.
                File.Move(tempPath, fullOutputPath);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch { }
        }
    }
}
