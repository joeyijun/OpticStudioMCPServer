namespace ZemaxMCP.Server.Tools.NonSequential;

/// <summary>
/// Strict arithmetic attribution for an ALREADY VALIDATED set of unique ray
/// branches. This engine does not obtain physical events from OpticStudio;
/// only trace-native per-interaction deposits may be fed to it after future
/// ZRD/segment-reader verification. Detector *incident* flux and geometrical
/// pupil proxies MUST NOT be entered as absorbed/terminal power.
/// </summary>
public static class ConservativeRayEnergyTree
{
    public sealed record Node(
        string Id, string? ParentId, double Incoming,
        double CoatingAbsorbed = 0, double MaterialAbsorbed = 0,
        double MechanicalStopped = 0, double DetectorAbsorbed = 0,
        double Escaped = 0, double OtherVerifiedLoss = 0);

    public sealed record Report(
        int NodeCount, int SourceRootCount, int SplitRayNodes,
        double Launched, double CoatingAbsorbed, double MaterialAbsorbed,
        double MechanicalStopped, double DetectorAbsorbed,
        double Escaped, double OtherVerifiedLoss,
        double Unresolved, bool ArithmeticClosure,
        string EvidenceKind, string Interpretation);

    internal static Report Reconcile(IReadOnlyList<Node> nodes)
    {
        if(nodes is not {Count:>=1 and <=10000})
            throw new ArgumentException("Ray tree must contain 1..10000 unique trace-native nodes.");
        var byId=new Dictionary<string,Node>(StringComparer.Ordinal);
        foreach(var node in nodes)
        {
            if(node is null || string.IsNullOrWhiteSpace(node.Id) ||
               node.Id.Length>128 || node.ParentId?.Length>128 ||
               !byId.TryAdd(node.Id,node))
                throw new ArgumentException("Trace ray IDs must be nonempty and unique.");
            var values=new[]{node.Incoming,node.CoatingAbsorbed,
                node.MaterialAbsorbed,node.MechanicalStopped,
                node.DetectorAbsorbed,node.Escaped,node.OtherVerifiedLoss};
            if(values.Any(x=>!double.IsFinite(x)||x<0))
                throw new ArgumentException("Ray incoming and deposited powers must be finite and nonnegative.");
        }
        var children=new Dictionary<string,List<Node>>(StringComparer.Ordinal);
        var roots=0;
        foreach(var node in nodes)
        {
            if(node.ParentId==null){roots++;continue;}
            if(!byId.ContainsKey(node.ParentId))
                throw new ArgumentException("Missing ray parent: "+node.ParentId);
            if(!children.TryGetValue(node.ParentId,out var list))
                children[node.ParentId]=list=new List<Node>();
            list.Add(node);
            // Reject self-loops and longer cycles explicitly. They would make
            // source energy appear more than once.
            var visited=new HashSet<string>(StringComparer.Ordinal){node.Id};
            var ancestor=node.ParentId;
            while(ancestor!=null)
            {
                if(!visited.Add(ancestor))
                    throw new ArgumentException("A cyclic ray graph cannot represent energy flow.");
                ancestor=byId[ancestor].ParentId;
            }
        }
        if(roots<1)throw new ArgumentException("At least one source-root ray is required.");
        var totalRoots=0d;var totalUnknown=0d;var splitNodes=0;
        double coating=0,material=0,mechanical=0,detector=0,escaped=0,other=0;
        foreach(var node in nodes)
        {
            if(node.ParentId==null)totalRoots+=node.Incoming;
            var outgoing=children.TryGetValue(node.Id,out var childs)
                ? childs.Sum(c=>c.Incoming) : 0d;
            if(childs is {Count:>1})splitNodes++;
            var consumed=node.CoatingAbsorbed+node.MaterialAbsorbed+
                node.MechanicalStopped+node.DetectorAbsorbed+
                node.Escaped+node.OtherVerifiedLoss;
            if(!double.IsFinite(outgoing)||!double.IsFinite(consumed))
                throw new InvalidDataException("Energy tree accumulated nonfinite child/absorption power.");
            var residual=node.Incoming-consumed-outgoing;
            var tolerance=1e-9*Math.Max(1d,node.Incoming);
            if(residual < -tolerance)
                throw new InvalidDataException("Ray "+node.Id+
                    " consumes/branches more power than its parent supplied. A detector may have been double-counted.");
            if(residual > tolerance)
                totalUnknown+=residual; // explicitly UNKNOWN, not absorption.
            coating+=node.CoatingAbsorbed;
            material+=node.MaterialAbsorbed;
            mechanical+=node.MechanicalStopped;
            detector+=node.DetectorAbsorbed;
            escaped+=node.Escaped;
            other+=node.OtherVerifiedLoss;
        }
        var sum=coating+material+mechanical+detector+escaped+other+totalUnknown;
        if(!double.IsFinite(totalRoots)||!double.IsFinite(sum))
            throw new InvalidDataException("Energy tree summation overflowed finite power.");
        var closed=Math.Abs(totalRoots-sum) <= 1e-8*Math.Max(1d,totalRoots)
            && totalUnknown <= 1e-9*Math.Max(1d,totalRoots);
        return new Report(nodes.Count,roots,splitNodes,totalRoots,
            coating,material,mechanical,detector,escaped,other,totalUnknown,
            closed,"explicit per-ray tree supplied by a future validated trace-native event reader",
            "Arithmetic first-deposit/source-tree partition; actual reflected/transmitted " +
            "power passes to child rays and is NOT recorded again as a coating loss. " +
            "Only PHYSICALLY deposited and non-overlapping detector-ABSORBED power is terminal. " +
            "Native detector incident flux, overlapping detectors, sampled CAD masks, " +
            "or per-surface coating R/T/A proxies are not admissible deposits. " +
            "Residual is UNKNOWN rather than fabricated material/coating loss. " +
            "ArithmeticClosure DOES NOT prove that these events came from a real OpticStudio trace; " +
            "current implementation does NOT read ZRD data or provide end-to-end calibrated watts.");
    }
}
