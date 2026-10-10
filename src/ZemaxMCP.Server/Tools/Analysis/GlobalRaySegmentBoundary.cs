namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>Pure finite-segment / finite-planar aperture intersection,
/// using actual GLOBAL endpoints of rays at adjacent sequential surfaces.
/// No extrapolation, no COM, no assumption that a ray hits an infinite plane.
/// </summary>
public static class GlobalRaySegmentBoundary
{
    public sealed record Segment(GlobalFootprintProjection.Point3 Start,
        GlobalFootprintProjection.Point3 End);

    public sealed record Assessment(
        int InputSegments, int IntersectedPlane, int ApertureInside,
        int ApertureOutside, int NoPlaneIntersection,
        int CoplanarAmbiguous, int DegenerateSegments,
        double? MinimumSignedApertureClearance,
        double? OutsideFractionOfPlaneIntersections,
        double? OutsideFractionOfValidSegments,
        string CoordinateFrame, string Interpretation,
        GlobalFootprintProjection.Point3? MostCriticalIntersection = null,
        IReadOnlyList<GlobalFootprintProjection.Point3>? SampleIntersections = null,
        bool SamplesTruncated = false);

    internal static Assessment Assess(double[][] polygon,
        IReadOnlyList<Segment> segments,double tolerance,int sampleLimit=0)
    {
        var p=GlobalPlanarMechanicalBoundary.Build(polygon,tolerance);
        if(segments==null)throw new ArgumentNullException(nameof(segments));
        if(segments.Count>2601 || sampleLimit is <0 or >128)
            throw new ArgumentOutOfRangeException(nameof(segments),"Ray segment analysis is bounded to a 51x51 pupil and <=128 retained intersections.");
        var hits=0;var outside=0;var noCross=0;var ambiguous=0;var degenerate=0;
        var worst=double.PositiveInfinity;
        GlobalFootprintProjection.Point3? mostCritical=null;
        var retained=new List<GlobalFootprintProjection.Point3>();
        static double Dot3(double[] x,double[] y)=>x[0]*y[0]+x[1]*y[1]+x[2]*y[2];
        foreach(var seg in segments)
        {
            var a=new[]{seg.Start.X,seg.Start.Y,seg.Start.Z};
            var b=new[]{seg.End.X,seg.End.Y,seg.End.Z};
            if(a.Any(v=>!double.IsFinite(v))||b.Any(v=>!double.IsFinite(v)))
                throw new InvalidDataException("Non-finite optical ray segment endpoint.");
            var delta=new[]{b[0]-a[0],b[1]-a[1],b[2]-a[2]};
            var relative=new[]{a[0]-p.Origin[0],a[1]-p.Origin[1],a[2]-p.Origin[2]};
            var length=Math.Sqrt(Dot3(delta,delta));
            if(!double.IsFinite(length)||!double.IsFinite(Dot3(relative,relative)))
                throw new InvalidDataException("Optical ray segment geometry overflowed.");
            if(length<=1e-12){degenerate++;continue;}
            var d0=Dot3(relative,p.Normal);
            var denominator=Dot3(delta,p.Normal);
            var d1=d0+denominator;
            if(!double.IsFinite(d0)||!double.IsFinite(d1))
                throw new InvalidDataException("Optical ray-to-plane distances overflowed.");
            // Coplanar paths have infinitely many possible intersections:
            // they require solid-side collision handling, not one 2D sample.
            if(Math.Abs(d0)<=tolerance && Math.Abs(d1)<=tolerance)
            {ambiguous++;continue;}
            // A sufficiently parallel segment outside the plane tolerance
            // cannot be assigned a meaningful unique ray-plane intercept.
            if(Math.Abs(denominator)<=1e-12*Math.Max(1d,length))
            {noCross++;continue;}
            var u=-d0/denominator;
            // A physical aperture does not intersect a ray SEGMENT if the
            // infinite line hits beyond either endpoint (no extrapolation).
            if(u<0||u>1){noCross++;continue;}
            var xyz=new[]{a[0]+u*delta[0],a[1]+u*delta[1],a[2]+u*delta[2]};
            var shift=new[]{xyz[0]-p.Origin[0],xyz[1]-p.Origin[1],xyz[2]-p.Origin[2]};
            var clearance=MechanicalFootprintBoundary.SignedClearance(
                p.Vertices2D,Dot3(shift,p.AxisU),Dot3(shift,p.AxisV));
            if(!double.IsFinite(clearance))
                throw new InvalidDataException("Non-finite aperture clearance at ray-plane intersection.");
            hits++;
            if(clearance<0)outside++;
            var intersection=new GlobalFootprintProjection.Point3(xyz[0],xyz[1],xyz[2]);
            if(retained.Count<sampleLimit)retained.Add(intersection);
            if(clearance<worst) { worst=clearance; mostCritical=intersection; }
        }
        var valid=segments.Count-degenerate-ambiguous;
        return new Assessment(segments.Count,hits,hits-outside,outside,
            noCross,ambiguous,degenerate,
            hits>0?worst:null,hits>0?(double)outside/hits:null,
            valid>0?(double)outside/valid:null,
            "OpticStudio global XYZ (lens units)",
            "Finite straight ray segments from adjacent sequential LDE surfaces (both optically clear), independently projected via each surface's GetGlobalMatrix and actual ray sag Z. Out-of-aperture plane intersections are potential CAD clipping if the polygon is an opaque stop with its allowed opening INSIDE. Parallel/out-of-segment/coplanar/degenerate rays are not fabricated as cuts. Ratios refer only to valid surviving two-surface segments, not source throughput; failures/vignetting before either endpoint are excluded. No generalized solid CAD or diffraction.",
            mostCritical,retained,hits>retained.Count);
    }
}
