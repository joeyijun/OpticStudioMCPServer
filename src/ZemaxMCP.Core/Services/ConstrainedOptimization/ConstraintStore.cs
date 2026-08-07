using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ZemaxMCP.Core.Models;

namespace ZemaxMCP.Core.Services.ConstrainedOptimization;

public class ConstraintStore
{
    private readonly ConcurrentDictionary<string, StoredConstraint> _constraints = new();

    public void SetConstraint(string compositeKey, ConstraintType constraint, double min, double max)
    {
        ValidateStoredConstraint(compositeKey, constraint, min, max);
        _constraints[compositeKey] = new StoredConstraint(constraint, min, max);
    }

    public void ReplaceAll(IReadOnlyDictionary<string, StoredConstraint> constraints)
    {
        if (constraints == null)
            throw new ArgumentNullException(nameof(constraints));

        foreach (var entry in constraints)
            ValidateStoredConstraint(entry.Key, entry.Value.Constraint, entry.Value.Min, entry.Value.Max);

        _constraints.Clear();
        foreach (var entry in constraints)
            _constraints[entry.Key] = entry.Value;
    }

    public void ApplyConstraints(List<OptVariable> variables)
    {
        foreach (var v in variables)
        {
            if (_constraints.TryGetValue(v.CompositeKey, out var stored))
            {
                v.Constraint = stored.Constraint;
                v.Min = stored.Min;
                v.Max = stored.Max;
            }
        }
    }

    public StoredConstraint? GetConstraint(string compositeKey)
    {
        _constraints.TryGetValue(compositeKey, out var stored);
        return stored;
    }

    public Dictionary<string, StoredConstraint> GetAll()
    {
        return new Dictionary<string, StoredConstraint>(_constraints);
    }

    public void Clear()
    {
        _constraints.Clear();
    }

    /// <summary>
    /// Save all constraints to a sidecar JSON file next to the given Zemax file.
    /// The final replace/move is atomic so a failed write cannot leave a truncated sidecar.
    /// </summary>
    public void SaveToFile(string zemaxFilePath)
    {
        if (string.IsNullOrWhiteSpace(zemaxFilePath))
            throw new ArgumentException("A Zemax system file path is required.", nameof(zemaxFilePath));

        var sidecarPath = GetSidecarPath(zemaxFilePath);
        var snapshot = GetAll();

        if (snapshot.Count == 0)
        {
            if (File.Exists(sidecarPath))
                File.Delete(sidecarPath);
            return;
        }

        foreach (var entry in snapshot)
            ValidateStoredConstraint(entry.Key, entry.Value.Constraint, entry.Value.Min, entry.Value.Max);

        var sb = new StringBuilder();
        sb.AppendLine("[");
        int i = 0;
        foreach (var kvp in snapshot.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            if (i > 0) sb.AppendLine(",");
            sb.AppendLine("  {");
            sb.AppendLine($"    \"CompositeKey\": \"{EscapeJson(kvp.Key)}\",");
            sb.AppendLine($"    \"Constraint\": \"{kvp.Value.Constraint}\",");
            sb.AppendLine($"    \"Min\": {kvp.Value.Min.ToString("R", CultureInfo.InvariantCulture)},");
            sb.AppendLine($"    \"Max\": {kvp.Value.Max.ToString("R", CultureInfo.InvariantCulture)}");
            sb.Append("  }");
            i++;
        }
        sb.AppendLine();
        sb.AppendLine("]");

        var directory = Path.GetDirectoryName(Path.GetFullPath(sidecarPath));
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Unable to determine the constraint sidecar directory.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(sidecarPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(sidecarPath))
                File.Replace(tempPath, sidecarPath, null);
            else
                File.Move(tempPath, sidecarPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Load constraints from a sidecar JSON file if it exists. Replaces current constraints
    /// only after the entire sidecar has been parsed and validated successfully.
    /// </summary>
    /// <returns>Number of constraints loaded, or 0 if no sidecar file is present.</returns>
    public int LoadFromFile(string zemaxFilePath)
    {
        if (string.IsNullOrWhiteSpace(zemaxFilePath))
            throw new ArgumentException("A Zemax system file path is required.", nameof(zemaxFilePath));

        var sidecarPath = GetSidecarPath(zemaxFilePath);
        if (!File.Exists(sidecarPath))
            return 0;

        var json = File.ReadAllText(sidecarPath);
        var entries = ParseEntries(json, sidecarPath);
        var replacement = new Dictionary<string, StoredConstraint>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (replacement.ContainsKey(entry.CompositeKey))
                throw new FormatException($"Constraint sidecar '{sidecarPath}' contains duplicate key '{entry.CompositeKey}'.");
            replacement[entry.CompositeKey] = new StoredConstraint(entry.Constraint, entry.Min, entry.Max);
        }

        ReplaceAll(replacement);
        return replacement.Count;
    }

    public static string GetSidecarPath(string zemaxFilePath)
    {
        return zemaxFilePath + ".constraints.json";
    }

    public record StoredConstraint(ConstraintType Constraint, double Min, double Max);

    private static void ValidateStoredConstraint(string compositeKey, ConstraintType constraint, double min, double max)
    {
        if (string.IsNullOrWhiteSpace(compositeKey))
            throw new ArgumentException("Constraint composite key cannot be empty.", nameof(compositeKey));
        if (!Enum.IsDefined(typeof(ConstraintType), constraint))
            throw new ArgumentOutOfRangeException(nameof(constraint), constraint, "Unknown constraint type.");
        if (double.IsNaN(min) || double.IsInfinity(min) || double.IsNaN(max) || double.IsInfinity(max))
            throw new ArgumentOutOfRangeException(nameof(min), "Constraint bounds must be finite numbers.");
        if (constraint == ConstraintType.MinAndMax && min >= max)
            throw new ArgumentException($"Constraint minimum {min} must be less than maximum {max}.");
    }

    private static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static List<ParsedEntry> ParseEntries(string json, string sourcePath)
    {
        if (json == null)
            throw new ArgumentNullException(nameof(json));
        if (!Regex.IsMatch(json, @"^\s*\[.*\]\s*$", RegexOptions.Singleline))
            throw new FormatException($"Constraint sidecar '{sourcePath}' is not a JSON array.");

        var results = new List<ParsedEntry>();
        var objectPattern = new Regex(@"\{[^{}]*\}", RegexOptions.Singleline);
        var matches = objectPattern.Matches(json);
        var nonObjectContent = objectPattern.Replace(json, string.Empty);
        nonObjectContent = Regex.Replace(nonObjectContent, @"[\s\[\],]", string.Empty);
        if (nonObjectContent.Length != 0)
            throw new FormatException($"Constraint sidecar '{sourcePath}' contains malformed or nested content.");

        foreach (Match match in matches)
        {
            var obj = match.Value;
            var key = ExtractRequiredStringValue(obj, "CompositeKey", sourcePath);
            var constraintText = ExtractRequiredStringValue(obj, "Constraint", sourcePath);
            var min = ExtractRequiredDoubleValue(obj, "Min", sourcePath);
            var max = ExtractRequiredDoubleValue(obj, "Max", sourcePath);

            if (!Enum.TryParse<ConstraintType>(constraintText, ignoreCase: true, out var constraint) ||
                !Enum.IsDefined(typeof(ConstraintType), constraint))
            {
                throw new FormatException($"Constraint sidecar '{sourcePath}' contains unknown constraint type '{constraintText}'.");
            }

            ValidateStoredConstraint(key, constraint, min, max);
            results.Add(new ParsedEntry(key, constraint, min, max));
        }

        if (matches.Count == 0 && !Regex.IsMatch(json, @"^\s*\[\s*\]\s*$", RegexOptions.Singleline))
            throw new FormatException($"Constraint sidecar '{sourcePath}' contains no valid constraint objects.");

        return results;
    }

    private static string ExtractRequiredStringValue(string json, string property, string sourcePath)
    {
        var pattern = new Regex($"\\\"{Regex.Escape(property)}\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"");
        var match = pattern.Match(json);
        if (!match.Success)
            throw new FormatException($"Constraint sidecar '{sourcePath}' is missing string property '{property}'.");

        return UnescapeJsonString(match.Groups[1].Value);
    }

    private static double ExtractRequiredDoubleValue(string json, string property, string sourcePath)
    {
        var pattern = new Regex($"\\\"{Regex.Escape(property)}\\\"\\s*:\\s*([^,}}\\s]+)");
        var match = pattern.Match(json);
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new FormatException($"Constraint sidecar '{sourcePath}' contains an invalid finite numeric value for '{property}'.");
        }
        return value;
    }

    private static string UnescapeJsonString(string value)
    {
        return value.Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    private record ParsedEntry(string CompositeKey, ConstraintType Constraint, double Min, double Max);
}
