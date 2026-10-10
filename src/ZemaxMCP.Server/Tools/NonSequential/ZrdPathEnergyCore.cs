namespace ZemaxMCP.Server.Tools.NonSequential;

/// <summary>
/// Pure energy-flow topology verifier for BOUNDED, explicitly linked ray
/// segment records. Does not infer interface R/T/A or absorption from
/// intensity differences. A ZRD adapter must preserve ray/parent indexing,
/// wavelength and branch semantics before calling this core.
/// </summary>
public static class ZrdPathEnergyCore
{
    public sealed record Segment(
        int RayNumber,int SegmentIndex,int ParentSegmentIndex,
        double Intensity,int HitObject,bool DetectorObservation);

    public sealed record Report(int Rays,int Segments,int Branches,
        double RootIntensitySum,double PositiveUnexplainedTransitionDifference,
        double NegativeTransitionDifferenceMagnitude,double LeafIntensitySum,
        int DetectorHitEvents,double DetectorHitIntensityObservations,
        bool PhysicalCoatingMaterialClippingAttributionKnown,
        bool DetectorHitEnergyDisjoint,bool PhysicalEnergyClosureEstablished,
        string Interpretation);

    internal static Report Audit(IReadOnlyList<Segment> events)
    {
        if(events==null || events.Count is <1 or >16384 ||
           events.Any(e=>e.RayNumber<0 || e.SegmentIndex<0 ||
               e.ParentSegmentIndex>=e.SegmentIndex ||
               e.ParentSegmentIndex<-1 ||
               !double.IsFinite(e.Intensity)||e.Intensity<0 ||
               e.HitObject<0))
            throw new ArgumentException("ZRD graph requires <=16384 finite nonnegative segments with strictly earlier parents.");
        var groups=events.GroupBy(x=>x.RayNumber).ToArray();
        if(groups.Length>2048)
            throw new ArgumentException("ZRD evidence audit accepts at most 2048 unique native ray IDs.");
        var launched=0d;var positive=0d;var negative=0d;var leafTotal=0d;var branches=0;
        foreach(var group in groups)
        {
            var nodes=new Dictionary<int,Segment>();
            foreach(var e in group)
                if(!nodes.TryAdd(e.SegmentIndex,e))
                    throw new ArgumentException("Duplicate ray/segment index within one path.");
            var roots=nodes.Values.Where(e=>e.ParentSegmentIndex==-1).ToArray();
            if(roots.Length!=1)
                throw new ArgumentException("Each native ray must have exactly one explicit root segment.");
            launched+=roots[0].Intensity;
            foreach(var e in nodes.Values)
                if(e.ParentSegmentIndex!=-1 && !nodes.ContainsKey(e.ParentSegmentIndex))
                    throw new ArgumentException("A ZRD segment referenced an absent parent in its ray.");
            var childGroups=nodes.Values.Where(x=>x.ParentSegmentIndex>=0)
                .GroupBy(x=>x.ParentSegmentIndex)
                .ToDictionary(g=>g.Key,g=>g.ToArray());
            foreach(var parent in nodes.Values)
            {
                var children=childGroups.TryGetValue(parent.SegmentIndex,out var groupChildren)
                    ? groupChildren : Array.Empty<Segment>();
                if(children.Length==0){leafTotal+=parent.Intensity;continue;}
                if(children.Length>1)branches++;
                var outgoing=children.Sum(x=>x.Intensity);
                if(!double.IsFinite(outgoing))
                    throw new InvalidDataException("ZRD outgoing intensity sum overflowed.");
                var diff=parent.Intensity-outgoing;
                if(diff>=0)positive+=diff;else negative-=diff;
            }
        }
        var hitEvents=events.Where(e=>e.DetectorObservation).ToArray();
        var observed=hitEvents.Sum(x=>x.Intensity);
        if(!new[]{launched,positive,negative,leafTotal,observed}.All(double.IsFinite))
            throw new InvalidDataException("ZRD aggregate intensity overflowed.");
        var residual=Math.Abs((leafTotal+positive-negative)-launched);
        if(residual>1e-8*Math.Max(1d,launched))
            throw new InvalidDataException("Linked segment graph violates algebraic ray-power partition.");
        return new Report(groups.Length,events.Count,branches,launched,
            positive,negative,leafTotal,hitEvents.Length,observed,
            false,false,false,
            "ZRD candidate parent/child intensity topology only. Transition differences are NOT a physically proven coating R/T/A, bulk absorption, scattering, or CAD obstruction term; leaf intensity is NOT automatically detected/absorbed power. " +
            "Detector hit intensity observations can re-count rays that visit multiple detectors or bounce; do not subtract hit sums from source intensity. " +
            "This audit proves only algebraic consistency of the explicitly supplied graph. Real ZRD segment-parent mapping, polarization, splitting and source power normalization must be independently verified against the installed OpticStudio version. " +
            "No path-loss category is silently inferred and physical source-to-detector energy conservation is NOT established.");
    }
}
