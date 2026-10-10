using System.Text.Json;

namespace ZemaxMCP.Server.Tools.Catalog;

/// <summary>Pure metadata comparison of two bounded summary results.
/// No Zemax model is opened or changed and no optical merit is inferred.</summary>
public static class SystemSummaryComparer
{
    public sealed record Change(string Path,string Baseline,string Current);
    public sealed record Comparison(bool Comparable,int ChangedProperties,
        IReadOnlyList<Change> Differences,IReadOnlyList<string> Warnings,
        string Interpretation);

    private static readonly string[] Header={
        "mode","lensUnit","fieldType","apertureType","apertureValue",
        "numberOfSurfaces","numberOfNscObjects","numberOfFields",
        "numberOfWavelengths","configurations","currentConfiguration","needsSave"
    };
    private static readonly (string List,string[] Properties)[] Tables={
        ("keySurfaces",new[]{"type","material","isStop","isMirror","isCoordinateBreak",
            "radius","thickness","semiDiameter"}),
        ("fields",new[]{"x","y","weight","isActive"}),
        ("wavelengths",new[]{"micrometers","weight","isPrimary","isActive"})
    };

    internal static Comparison Compare(string baselineJson,string currentJson)
    {
        if(string.IsNullOrWhiteSpace(baselineJson) || baselineJson.Length>96000 ||
            currentJson.Length>96000) throw new ArgumentException("Summary payload exceeds 96 KB.");
        using var baseline=JsonDocument.Parse(baselineJson);
        using var current=JsonDocument.Parse(currentJson);
        var a=baseline.RootElement; var b=current.RootElement;
        if(a.ValueKind!=JsonValueKind.Object || b.ValueKind!=JsonValueKind.Object ||
           !a.TryGetProperty("success",out var ok) || ok.ValueKind!=JsonValueKind.True ||
           !b.TryGetProperty("success",out ok) || ok.ValueKind!=JsonValueKind.True)
            throw new ArgumentException("Both summaries must be successful zemax_system_summary results.");
        var changes=new List<Change>();
        var warnings=new List<string>();
        foreach(var field in Header) Diff(a,b,field,field,changes);
        foreach(var (list,properties) in Tables)
        {
            if(!a.TryGetProperty(list,out var aa) || aa.ValueKind!=JsonValueKind.Array ||
               !b.TryGetProperty(list,out var bb) || bb.ValueKind!=JsonValueKind.Array)
            {
                warnings.Add(list+" is missing or malformed; comparison is incomplete.");
                continue;
            }
            // A bounded summary shows selected records, not a complete editor
            // snapshot. Missing IDs are not evidence of an added/deleted row.
            var left=aa.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.Object &&
                x.TryGetProperty("number",out var n) && n.TryGetInt32(out _))
                .ToDictionary(x=>x.GetProperty("number").GetInt32());
            var right=bb.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.Object &&
                x.TryGetProperty("number",out var n) && n.TryGetInt32(out _))
                .ToDictionary(x=>x.GetProperty("number").GetInt32());
            foreach(var number in left.Keys.Intersect(right.Keys).OrderBy(x=>x))
                foreach(var prop in properties)
                    Diff(left[number],right[number],prop,list+"["+number+"]."+prop,changes);
            if(!left.Keys.OrderBy(x=>x).SequenceEqual(right.Keys.OrderBy(x=>x)))
                warnings.Add(list+" contains different sampled IDs. Missing sampled rows are UNKNOWN, not proven additions/deletions.");
        }
        foreach(var side in new[]{("baseline",a),("current",b)})
        {
            foreach(var omitted in new[]{"omittedSurfaces","omittedFields","omittedWavelengths"})
                if(side.Item2.TryGetProperty(omitted,out var n) && n.ValueKind==JsonValueKind.Number &&
                   n.GetInt32()>0)
                    warnings.Add(side.Item1+" omitted "+n.GetInt32()+" "+omitted+
                        "; unsampled records cannot be verified by metadata diff.");
        }
        var total=changes.Count;
        if(total>64) warnings.Add("Difference list truncated to 64 entries.");
        return new Comparison(true,total,changes.Take(64).ToArray(),warnings,
            "Metadata diff only. Geometry offsets, coordinate frames, physical optical performance and full LDE/NCE changes require dedicated verified analysis.");
    }

    private static void Diff(JsonElement a,JsonElement b,string key,string path,List<Change> changes)
    {
        var x=a.TryGetProperty(key,out var av)?Value(av):"<missing>";
        var y=b.TryGetProperty(key,out var bv)?Value(bv):"<missing>";
        if(!string.Equals(x,y,StringComparison.Ordinal))
            changes.Add(new Change(path,x,y));
    }
    private static string Value(JsonElement v)=>v.ValueKind==JsonValueKind.String?
        v.GetString()??"" : v.GetRawText();
}
