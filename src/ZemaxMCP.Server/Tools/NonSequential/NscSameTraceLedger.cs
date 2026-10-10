namespace ZemaxMCP.Server.Tools.NonSequential;

/// <summary>
/// A trace-provenance ledger, NOT a decomposition of unexplained losses into
/// coating, bulk material, or mechanical obstruction. Detector readings MUST
/// be obtained immediately after the successful NSC ray trace that cleared
/// the detector buffers. Separate detector readings may overlap physically.
/// </summary>
public static class NscSameTraceLedger
{
    public sealed record DetectorReading(int ObjectNumber,string ObjectType,
        double IncidentFlux,double Hits,double? FractionOfDeclaredSource);

    public sealed record Report(
        string EvidenceKind,string DenominatorKind,bool SameTraceCaptured,
        bool DetectorsClearedBeforeTrace,bool DetectorsMayOverlap,
        double? UserDeclaredLaunchedFlux,
        IReadOnlyList<DetectorReading> Detectors,
        bool AdditiveSourceToDetectorBalanceValid,
        double? UnassignedEnergy,
        string CoatingLossStatus,string MaterialLossStatus,
        string MechanicalClippingStatus,string Interpretation);

    internal static Report Build(double? declaredFlux,
        IReadOnlyList<(int Id,string Type,double Flux,double Hits)> detectorReadings,
        bool clearedAllDetectors)
    {
        if(!clearedAllDetectors)
            throw new ArgumentException("Same-trace detector ledger requires clearing ALL detector buffers before this trace.");
        if(detectorReadings is null || detectorReadings.Count is < 1 or > 16 ||
            detectorReadings.Any(x=>x.Id<1 || string.IsNullOrWhiteSpace(x.Type) ||
                !double.IsFinite(x.Flux)||x.Flux<0 ||
                !double.IsFinite(x.Hits)||x.Hits<0) ||
            detectorReadings.Select(x=>x.Id).Distinct().Count()!=detectorReadings.Count)
            throw new ArgumentException("Provide 1..16 unique detectors with finite nonnegative native flux and hits.");
        if(declaredFlux.HasValue && (!double.IsFinite(declaredFlux.Value)||declaredFlux.Value<=0))
            throw new ArgumentException("Declared source flux must be finite and positive.");
        var rows=detectorReadings.Select(x=>
            new DetectorReading(x.Id,x.Type,x.Flux,x.Hits,
                declaredFlux is null ? null : x.Flux/declaredFlux.Value)).ToArray();
        if(rows.Any(x=>x.FractionOfDeclaredSource is double f && !double.IsFinite(f)))
            throw new InvalidDataException("Source-normalized detector ratio overflowed.");
        return new Report("post-trace native NSC detector GetDetectorData(0/−3)",
            declaredFlux is null ? "none; no total source power measured" :
                "USER-declared native launched flux; not independently read from source",
            true,true,true,declaredFlux,rows,false,null,
            "not directly measured", "not directly measured",
            "not directly measured",
            "Detector readings are snapshotted INSIDE the successful ray-trace session immediately after a full detector clear. " +
            "Fractions are independent per-detector observations, never added as disjoint power. " +
            "Source power is user-declared and must represent the SAME trace, source set and native units; no source power was independently measured. " +
            "OpticStudio's detector may record multiple hits or overlap another detector. " +
            "Coating reflectance/transmittance/absorption, bulk material absorption, and mechanical clipping are NOT independently attributed from detector totals: the residual and additive energy closure are UNKNOWN, not assigned to arbitrary losses. " +
            "No repeated multiplication of native ray intensities by R/T/A, no model edits, no ZRD, no watts claim without native unit validation.");
    }
}
