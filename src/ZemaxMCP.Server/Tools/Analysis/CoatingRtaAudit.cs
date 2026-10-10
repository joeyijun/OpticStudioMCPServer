namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>Explicit user-supplied passive coating R/T/A assumptions, separate
/// from OpticStudio's real-ray intensity. Cannot infer coating physics.</summary>
public static class CoatingRtaAudit
{
    public sealed record SurfaceLoss(int Surface, double Reflectance,
        double Transmittance, double Absorptance, bool SelectedReflection,
        double Incoming, double SelectedOutgoing, double OtherBranch,
        double Absorbed, double Unresolved);
    public sealed record Audit(double NormalizedInput, double RemainingSelectedPath,
        double TotalOtherBranch, double TotalAbsorbed, double TotalUnresolved,
        IReadOnlyList<SurfaceLoss> Surfaces, string Interpretation);

    internal static Audit Evaluate(int startSurface,
        double[][] passiveRta, bool[] followReflection)
    {
        if (passiveRta==null || followReflection==null ||
            passiveRta.Length is < 1 or > 24 ||
            passiveRta.Length!=followReflection.Length ||
            passiveRta.Any(row=>row==null || row.Length!=3 ||
                row.Any(v=>!double.IsFinite(v) || v<0 || v>1) ||
                row.Sum()>1+1e-10))
            throw new ArgumentException(
                "Provide 1..24 passive [R,T,A] records and an equal-length selected-reflection mask; coefficients must be finite in [0,1] with R+T+A<=1.");
        var selected=1d;var other=0d;var absorbed=0d;var unknown=0d;
        var rows=new List<SurfaceLoss>();
        for(var i=0;i<passiveRta.Length;i++)
        {
            var r=passiveRta[i][0];var t=passiveRta[i][1];var a=passiveRta[i][2];
            var unmodeled=Math.Max(0,1-r-t-a);
            var chosen=followReflection[i]?r:t;
            var otherFraction=followReflection[i]?t:r;
            var remainder=selected*chosen;
            var bypass=selected*otherFraction;
            var loss=selected*a;
            var unknownLoss=selected*unmodeled;
            rows.Add(new SurfaceLoss(startSurface+i,r,t,a,followReflection[i],
                selected,remainder,bypass,loss,unknownLoss));
            selected=remainder;other+=bypass;absorbed+=loss;unknown+=unknownLoss;
        }
        if(!double.IsFinite(selected+other+absorbed+unknown) ||
           Math.Abs(selected+other+absorbed+unknown-1)>1e-8)
            throw new InvalidDataException("Assumed-coating bookkeeping is not conservative.");
        return new Audit(1,selected,other,absorbed,unknown,rows,
            "User-supplied PASSIVE grey R/T/A coefficients, one per surface in this window; single selected branch, no wave/interference/polarization or multiple reflections. Independent of ray-intensity and clipping values. Not an OpticStudio coating readback or measured throughput.");
    }
}
