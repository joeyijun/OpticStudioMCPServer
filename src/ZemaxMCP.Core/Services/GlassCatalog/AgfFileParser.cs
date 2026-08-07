using System.Globalization;
using ZemaxMCP.Core.Models;

namespace ZemaxMCP.Core.Services.GlassCatalog;

public static class AgfFileParser
{
    public static Dictionary<string, string> DiscoverCatalogs(string glassCatDir)
    {
        var catalogs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(glassCatDir) || !Directory.Exists(glassCatDir)) return catalogs;

        foreach (var agfFile in Directory.GetFiles(glassCatDir, "*.agf"))
        {
            string catalogName = Path.GetFileNameWithoutExtension(agfFile);
            catalogs[catalogName] = agfFile;
        }
        return catalogs;
    }

    public static List<GlassEntry> ParseCatalog(string agfPath, string catalogName)
    {
        if (string.IsNullOrWhiteSpace(agfPath)) throw new ArgumentException("AGF path is required.", nameof(agfPath));
        if (!File.Exists(agfPath)) throw new FileNotFoundException("AGF catalog file was not found.", agfPath);
        if (string.IsNullOrWhiteSpace(catalogName)) throw new ArgumentException("Catalog name is required.", nameof(catalogName));

        var glasses = new List<GlassEntry>();
        string[] lines = File.ReadAllLines(agfPath);
        GlassEntry? current = null;
        int formula = 0;
        double vd = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            int lineNumber = i + 1;

            if (trimmed.StartsWith("NM ", StringComparison.Ordinal))
            {
                if (current != null) glasses.Add(current);
                var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 6 || string.IsNullOrWhiteSpace(parts[1]))
                    throw Malformed(agfPath, lineNumber, "NM record must contain at least name, formula, Nd, and Vd fields.");

                string name = parts[1];
                formula = ParseInt(parts[2], agfPath, lineNumber, "dispersion formula");
                double nd = ParseDouble(parts[4], agfPath, lineNumber, "Nd");
                vd = ParseDouble(parts[5], agfPath, lineNumber, "Vd");
                int status = parts.Length > 7 ? ParseInt(parts[7], agfPath, lineNumber, "status") : 0;
                int meltFreq = parts.Length > 8 ? ParseInt(parts[8], agfPath, lineNumber, "melt frequency") : -1;

                current = new GlassEntry
                {
                    Name = name,
                    DispersionFormula = formula,
                    Nd = nd,
                    Vd = vd,
                    Status = status,
                    MeltFrequency = meltFreq >= 1 && meltFreq <= 5 ? meltFreq : -1,
                    CatalogName = catalogName
                };
                current.RawLines.Add(line);
                continue;
            }

            if (current == null) continue;
            current.RawLines.Add(line);

            if (trimmed.StartsWith("CD ", StringComparison.Ordinal))
            {
                var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) throw Malformed(agfPath, lineNumber, "CD record contains no dispersion coefficients.");
                var coefficients = new double[parts.Length - 1];
                for (int j = 1; j < parts.Length; j++)
                    coefficients[j - 1] = ParseDouble(parts[j], agfPath, lineNumber, $"CD coefficient {j}");
                current.DispersionCoefficients = coefficients;
                if (formula > 0) current.DPgF = DispersionCalculator.ComputeDPgF(formula, coefficients, vd);
            }
            else if (trimmed.StartsWith("ED ", StringComparison.Ordinal))
            {
                var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1) current.TCE = ParseDouble(parts[1], agfPath, lineNumber, "TCE");
                if (parts.Length > 2) current.TCE2 = ParseDouble(parts[2], agfPath, lineNumber, "TCE2");
                if (parts.Length > 3) current.Density = ParseDouble(parts[3], agfPath, lineNumber, "density");
            }
            else if (trimmed.StartsWith("TD ", StringComparison.Ordinal))
            {
                var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                var thermalCoeffs = new double[Math.Min(parts.Length - 1, 7)];
                for (int j = 1; j < parts.Length && j <= 7; j++)
                    thermalCoeffs[j - 1] = ParseDouble(parts[j], agfPath, lineNumber, $"TD coefficient {j}");
                current.ThermalCoefficients = thermalCoeffs;
            }
            else if (trimmed.StartsWith("LD ", StringComparison.Ordinal))
            {
                var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) throw Malformed(agfPath, lineNumber, "LD record must contain minimum and maximum wavelength.");
                current.MinWavelength = ParseDouble(parts[1], agfPath, lineNumber, "minimum wavelength");
                current.MaxWavelength = ParseDouble(parts[2], agfPath, lineNumber, "maximum wavelength");
                if (current.MinWavelength > current.MaxWavelength)
                    throw Malformed(agfPath, lineNumber, "LD minimum wavelength exceeds maximum wavelength.");
            }
            else if (trimmed.StartsWith("OD ", StringComparison.Ordinal))
            {
                var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1) current.RelativeCost = ParseOdValue(parts[1], agfPath, lineNumber, "relative cost");
                if (parts.Length > 2) current.CR = ParseOdValue(parts[2], agfPath, lineNumber, "CR");
                if (parts.Length > 3) current.FR = ParseOdValue(parts[3], agfPath, lineNumber, "FR");
                if (parts.Length > 4) current.SR = ParseOdValue(parts[4], agfPath, lineNumber, "SR");
                if (parts.Length > 5) current.AR = ParseOdValue(parts[5], agfPath, lineNumber, "AR");
                if (parts.Length > 6) current.PR = ParseOdValue(parts[6], agfPath, lineNumber, "PR");
            }
            else if (trimmed.StartsWith("GC ", StringComparison.Ordinal))
            {
                current.Comment = trimmed.Length > 3 ? trimmed.Substring(3).Trim() : "";
            }
        }

        if (current != null) glasses.Add(current);
        return glasses;
    }

    private static double ParseOdValue(string token, string path, int lineNumber, string field)
    {
        if (token is "-" or "_") return -1;
        return ParseDouble(token, path, lineNumber, field);
    }

    private static double ParseDouble(string token, string path, int lineNumber, string field)
    {
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ||
            double.IsNaN(result) || double.IsInfinity(result))
            throw Malformed(path, lineNumber, $"Invalid {field} numeric token '{token}'.");
        return result;
    }

    private static int ParseInt(string token, string path, int lineNumber, string field)
    {
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
            throw Malformed(path, lineNumber, $"Invalid {field} integer token '{token}'.");
        return result;
    }

    private static FormatException Malformed(string path, int lineNumber, string message) =>
        new($"Malformed AGF catalog '{Path.GetFileName(path)}' at line {lineNumber}: {message}");
}
