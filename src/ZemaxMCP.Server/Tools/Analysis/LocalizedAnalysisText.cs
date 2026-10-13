using System.Text.RegularExpressions;

namespace ZemaxMCP.Server.Tools.Analysis;

// Normalize only recognized report headings. Numeric data is never translated,
// reordered or guessed. Unknown locales continue to fail the strict parsers.
internal static class LocalizedAnalysisText
{
    internal static string[] ReadLines(string path) => File.ReadAllLines(path).Select(Normalize).ToArray();

    internal static string Normalize(string raw)
    {
        string line = raw.Trim().Replace('：', ':').Replace('，', ',');
        var headings = new (string Source, string Target)[]
        {
            ("畸变类型", "Distortion Type"), ("位移单位是", "Shift units are "),
            ("高度单位是", "Height units are "), ("畸变单位是", "Distortion units are "),
            ("数据对于波长", "Data for wavelength"), ("畸变焦距", "Distortion focal length"),
            ("最大 视场是", "Maximum Field "), ("最大视场", "Maximum Field"),
            ("最大畸变", "Maximum distortion"), ("Y 角度", "Y Angle"),
            ("光瞳区域", "Pupil Zone"), ("最大焦移变化", "Maximum Focal Shift"),
            ("衍射极限变化", "Diffraction Limited"), ("短波长", "Short Wavelength"),
            ("长波长", "Long Wavelength"), ("面:", "Surface:"), ("单位是", "Units are "),
            ("视场值以角度表示", "Field values are in Degrees"),
            ("子午光扇,视场号", "Tangential fan, field number "),
            ("弧矢光扇,视场号", "Sagittal fan, field number ")
        };
        bool recognized = false;
        foreach (var heading in headings)
            if (line.StartsWith(heading.Source, StringComparison.Ordinal))
            { line = heading.Target + line.Substring(heading.Source.Length); recognized = true; break; }
        if (line.StartsWith("波长:", StringComparison.Ordinal))
        {
            var value = line.Substring(3).Trim();
            line = Regex.IsMatch(value, @"^[+-]?\d") ? "Wavelength: " + value : "Wavelength units are " + value;
            recognized = true;
        }
        if (Regex.IsMatch(line, @"^波长\s+位移$")) { line = "Wavelength Shift"; recognized = true; }
        if (line.StartsWith("Rel. 视场垂轴色差", StringComparison.Ordinal)) { line = "Rel. Field Lateral Color"; recognized = true; }
        if (line.Contains("Y 视场") && line.Contains("Rel. Ill")) { line = line.Replace("Y 视场", "Y Field"); recognized = true; }
        if (Regex.IsMatch(line, @"^光瞳\s+")) { line = Regex.Replace(line, @"^光瞳", "Pupil"); recognized = true; }
        if (recognized)
        {
            line = line.Replace("毫米", "Millimeters").Replace("百分比", "Percent");
            // Preserve a number/unit boundary in Chinese reports: "20.000度".
            line = line.Replace("度", " Degrees");
            if (line.StartsWith("Units are 波", StringComparison.Ordinal)) line = line.Replace("Units are 波", "Units are Waves");
        }
        return line;
    }
}
