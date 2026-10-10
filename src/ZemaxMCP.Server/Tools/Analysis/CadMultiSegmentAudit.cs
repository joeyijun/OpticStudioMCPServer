namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>Conservative first-blocker accounting across consecutive physical
/// sequential LDE ray chords; never add counts for the same ray at several
/// downstream CAD stops. No asserted physical flux/energy conservation.</summary>
public static class CadMultiSegmentAudit
{
    public sealed record Stop(int AfterSurface,double[][] AperturePolygon,
        double PlaneTolerance);
    public sealed record StopReport(int StopIndex,int AfterSurface,
        int FirstBlockedRays,double? WorstSignedClearance,
        GlobalFootprintProjection.Point3? WorstIntersection);
    public sealed record Report(int SampledPupilRays,int FirstBlockedRays,
        int FullyCheckedUnblockedRays,int UncertainRays,
        IReadOnlyList<StopReport> Stops,string Interpretation);

    internal static Report Assess(int firstSurface,
        IReadOnlyList<Stop> stops,
        IReadOnlyList<IReadOnlyList<GlobalRaySegmentBoundary.Segment?>> rayPaths)
    {
        if(firstSurface<1 || stops is null || stops.Count is <1 or >12 ||
           rayPaths is null || rayPaths.Count>2601 ||
           stops.Any(s=>s.AfterSurface<firstSurface ||
                        s.AfterSurface>=firstSurface+24 ||
                        !double.IsFinite(s.PlaneTolerance) ||
                        s.PlaneTolerance<=0))
            throw new ArgumentException("CAD path expects <=12 bounded stops, <=2601 pupil rays and consecutive LDE surfaces.");
        var chords=rayPaths.Count==0 ? stops.Max(s=>s.AfterSurface)-firstSurface+1 :
            rayPaths[0].Count;
        if(chords is <1 or >23 ||
           stops.Any(s=>s.AfterSurface-firstSurface>=chords) ||
           rayPaths.Any(path=>path==null||path.Count!=chords))
            throw new ArgumentException("All rays must preserve the same bounded consecutive-LDE segment indexing.");
        // Validate and project each stop only ONCE. Rebuilding polygons for
        // every sampled pupil ray would scale poorly for 12 stops x 2601 rays.
        var bases=stops.Select(s=>GlobalPlanarMechanicalBoundary.Build(
            s.AperturePolygon,s.PlaneTolerance)).ToArray();
        var blocked=new int[stops.Count];
        var margin=Enumerable.Repeat(double.PositiveInfinity,stops.Count).ToArray();
        var critical=new GlobalFootprintProjection.Point3?[stops.Count];
        var totalBlocked=0;var unknown=0;var passed=0;
        for(var ray=0;ray<rayPaths.Count;ray++)
        {
            var path=rayPaths[ray];
            bool uncertain=false,cut=false;
            for(var segmentIndex=0;segmentIndex<chords;segmentIndex++)
            {
                var segment=path[segmentIndex];
                if(segment==null) {uncertain=true;break;}
                var candidates=new List<(int Index,double Fraction,
                    double Signed,GlobalFootprintProjection.Point3 Hit)>();
                foreach(var indexed in stops.Select((stop,i)=>(stop,i))
                    .Where(x=>x.stop.AfterSurface==firstSurface+segmentIndex))
                {
                    var assessment=GlobalRaySegmentBoundary.AssessOnBasis(
                        bases[indexed.i],new[]{segment},indexed.stop.PlaneTolerance,1);
                    if(assessment.CoplanarAmbiguous>0 ||
                       assessment.DegenerateSegments>0)
                    {uncertain=true;break;}
                    if(assessment.ApertureOutside>0)
                    {
                        var hit=assessment.MostCriticalIntersection ??
                            throw new InvalidDataException("Outside aperture has no plane hit.");
                        var dx=segment.End.X-segment.Start.X;
                        var dy=segment.End.Y-segment.Start.Y;
                        var dz=segment.End.Z-segment.Start.Z;
                        var sq=dx*dx+dy*dy+dz*dz;
                        if(!double.IsFinite(sq)||sq<=0)
                            throw new InvalidDataException("Invalid ray segment length.");
                        var position=((hit.X-segment.Start.X)*dx+
                            (hit.Y-segment.Start.Y)*dy+
                            (hit.Z-segment.Start.Z)*dz)/sq;
                        candidates.Add((indexed.i,position,
                            assessment.MinimumSignedApertureClearance!.Value,hit));
                    }
                }
                if(uncertain)break;
                if(candidates.Count==0)continue;
                var earliest=candidates.OrderBy(x=>x.Fraction)
                    .ThenBy(x=>x.Index).First();
                blocked[earliest.Index]++;
                if(earliest.Signed<margin[earliest.Index])
                {
                    margin[earliest.Index]=earliest.Signed;
                    critical[earliest.Index]=earliest.Hit;
                }
                totalBlocked++;cut=true;break;
            }
            if(!cut)
            {
                if(uncertain)unknown++;else passed++;
            }
        }
        if(totalBlocked+unknown+passed!=rayPaths.Count)
            throw new InvalidDataException("CAD first-blocker accounting did not partition sampled rays.");
        var reports=stops.Select((s,i)=>new StopReport(i,s.AfterSurface,
            blocked[i],blocked[i]>0?margin[i]:null,critical[i])).ToArray();
        return new Report(rayPaths.Count,totalBlocked,passed,unknown,reports,
            "First possible CAD stop per ray, along actual consecutive LDE 3D ray chords. " +
            "Rays with missing/vignetted endpoints or coplanar ambiguous intersections are UNKNOWN, not credited to a later stop. " +
            "Two stops cannot both receive the same blocked ray. Multiple physical planes in one segment are sorted by actual crossing location, not caller order. " +
            "Only a straight segment between ADJACENT optical LDE vertices is assumed; no arbitrary solids, partial transparency, diffraction, or calibrated radiant power.");
    }
}
