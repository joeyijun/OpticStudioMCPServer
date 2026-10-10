using System.Text.Json;

namespace ZemaxMCP.Server.Tools.Catalog;

/// <summary>Truthful, deterministic interpretation of a small whitelist of
/// completed optical-tool DTOs. No model access, invented scores, AI inference
/// or unit conversion. Explicitly separates reported metrics from caveats.</summary>
public static class OpticalResultInterpreter
{
    public sealed record Metric(string Name,double Value,string Unit,string Evidence);
    public sealed record Interpretation(string ToolName,IReadOnlyList<Metric> Metrics,
        IReadOnlyList<string> Caveats,IReadOnlyList<string> NextTools,string Scope);

    internal static Interpretation Explain(string toolName,string resultJson)
    {
        if(resultJson==null || resultJson.Length is < 2 or > 131072)
            throw new ArgumentException("Result JSON must be 2..131072 characters.");
        if(toolName is not ("zemax_energy_budget" or "zemax_ray_footprint" or
            "zemax_get_nsc_detector" or "zemax_nsc_energy_budget" or
            "zemax_run_nsc_ray_trace" or "zemax_audit_native_zrd" or "zemax_system_summary"))
            throw new ArgumentException("Unsupported tool. Interpret only a recognized finished read-only optical result.");
        using var doc=JsonDocument.Parse(resultJson);
        var json=doc.RootElement;
        if(json.ValueKind!=JsonValueKind.Object ||
            !json.TryGetProperty("success",out var ok) || ok.ValueKind!=JsonValueKind.True)
            throw new ArgumentException("A successful tool result object is required; never explain a failed result as data.");
        var numbers=new List<Metric>();
        var warnings=new List<string>();
        var next=new List<string>();
        JsonElement[] Cases(string key,int max)
        {
            if(!json.TryGetProperty(key,out var arr) || arr.ValueKind!=JsonValueKind.Array)
                throw new ArgumentException("Expected the supported tool's "+key+" array.");
            if(arr.GetArrayLength()>max)
                throw new ArgumentException("Optical result array exceeds the bounded interpretation contract.");
            return arr.EnumerateArray().ToArray();
        }
        void Add(JsonElement root,string key,string label,string units,string scope)
        {
            if(!root.TryGetProperty(key,out var v) || v.ValueKind==JsonValueKind.Null) return;
            if(v.ValueKind!=JsonValueKind.Number) throw new ArgumentException(label+" must be a numeric value.");
            var value=v.GetDouble();
            if(!double.IsFinite(value)) throw new ArgumentException(label+" must be finite.");
            if(numbers.Count<24) numbers.Add(new Metric(label,value,units,scope));
        }
        switch(toolName)
        {
            case "zemax_energy_budget":
            {
                var cases=Cases("cases",36);
                for(var i=0;i<Math.Min(cases.Length,4);i++)
                {
                    Add(cases[i],"finalClearPupilFraction","case["+i+"] clear pupil fraction","ratio",
                        "sampled normalized pupil, at last surface of requested LDE window");
                    Add(cases[i],"finalNormalizedRayIntensity","case["+i+"] ray intensity proxy",
                        "dimensionless proxy","unpolarized ray trace; not watts");
                }
                warnings.Add("Geometric pupil sampling does not establish source radiance, separate coating R/T/A or detector collection.");
                warnings.Add("Windowed budgets refer to cumulative entrance-pupil survival; never multiply overlapping-window fractions.");
                if(cases.Length>4) warnings.Add("Only the first four field/wavelength cases are displayed; inspect all returned cases.");
                next.Add("zemax_ray_footprint");
                next.Add("zemax_aperture_throughput");
                break;
            }
            case "zemax_ray_footprint":
            {
                var surfaces=Cases("surfaces",24);
                for(var i=0;i<Math.Min(surfaces.Length,5);i++)
                {
                    Add(surfaces[i],"vignettedRays","surface["+i+"] vignetted rays","sampled rays",
                        "reported native ray trace at inspected surface");
                    Add(surfaces[i],"traceErrorRays","surface["+i+"] trace error rays","sampled rays",
                        "not interchangeable with mechanical obstruction");
                    if(surfaces[i].TryGetProperty("userMechanicalBoundary",out var mechanical) &&
                        mechanical.ValueKind==JsonValueKind.Object)
                        Add(mechanical,"minimumSignedClearance",
                            "surface["+i+"] user-boundary min clearance","lens units",
                            "signed LOCAL mechanical outline distance for surviving rays only");
                }
                warnings.Add("Surface intercepts and measured mechanical outlines must be in the same local LDE coordinate frame.");
                warnings.Add("Mechanical outside fractions exclude rays already rejected by the optical model.");
                if(json.TryGetProperty("opaqueCadMeshPath",out var cad) &&
                    cad.ValueKind==JsonValueKind.Object)
                {
                    Add(cad,"sampledRays","CAD mesh sampled optical rays","sampled ray paths",
                        "normalized pupil grid, not measured source photon/radiant power");
                    Add(cad,"firstBlockedRays","CAD mesh first intersections","sampled ray paths",
                        "first hit only; does not double count downstream triangles");
                    Add(cad,"unknownRays","CAD mesh uncertain ray paths","sampled ray paths",
                        "untraced, degenerate or ambiguous, not counted as transmitted");
                    warnings.Add("Opaque triangle mesh counts are geometrical first hits on surviving consecutive LDE chords; not a watertight CAD solid and not independently calibrated power.");
                }
                if(surfaces.Length>5) warnings.Add("Only the first five inspected surfaces are summarized.");
                next.Add("zemax_diagnose_clipping");
                break;
            }
            case "zemax_get_nsc_detector":
            {
                Add(json,"totalIncidentFlux","detector total incident flux","native source-flux units",
                    "OpticStudio detector statistic, not calibrated independently");
                Add(json,"roiFluxIntegral","ROI integrated native flux","native flux units",
                    "available only with a defensible pixel integration");
                Add(json,"roiPixelMean","ROI mean native pixel value","native pixel units",
                    "not integrated power");
                warnings.Add("Native detector row/column orientation is not a screen raster; physical orientation requires calibration.");
                warnings.Add("For DetectorVolume dataType=1 denotes absorbed flux, not irradiance.");
                if(!json.TryGetProperty("launchedFlux",out var source) || source.ValueKind==JsonValueKind.Null)
                    warnings.Add("No same-trace launched source flux is provided: do not infer detector collection efficiency.");
                next.Add("zemax_nsc_energy_budget");
                break;
            }
            case "zemax_nsc_energy_budget":
            {
                var detectors=Cases("detectors",16);
                for(var i=0;i<detectors.Length;i++)
                {
                    Add(detectors[i],"incidentFlux","detector["+i+"] incident flux",
                        "native source-flux units","one detector, independently");
                    Add(detectors[i],"fractionOfLaunchedFlux",
                        "detector["+i+"] source fraction","ratio",
                        "provided same-trace launched flux required");
                }
                warnings.Add("Never add potentially overlapping detector flux to assert global energy conservation.");
                warnings.Add("Hit counts include repeated/split interactions and do not measure missed unique rays.");
                next.Add("zemax_get_nsc_detector");
                break;
            }
            case "zemax_run_nsc_ray_trace":
            {
                if(!json.TryGetProperty("sameTraceEnergy",out var ledger) ||
                    ledger.ValueKind!=JsonValueKind.Object)
                    throw new ArgumentException("Trace result has no in-session sameTraceEnergy evidence. A Queued Job must be polled until completed.");
                Add(ledger,"configuredSourcePowerSum","configured model source power",
                    "native NSC source units","source OBJECT CONFIGURATION only; not measured rays launched");
                Add(ledger,"userDeclaredLaunchedFlux","user-declared source flux",
                    "user-declared native flux units","external declared denominator; not independently measured");
                if(!ledger.TryGetProperty("detectors",out var det) || det.ValueKind!=JsonValueKind.Array ||
                   det.GetArrayLength()>16)
                    throw new ArgumentException("Expected bounded same-trace detector readings.");
                var detectors=det.EnumerateArray().ToArray();
                for(var i=0;i<Math.Min(8,detectors.Length);i++)
                {
                    Add(detectors[i],"incidentFlux","same-trace detector["+i+"] flux",
                        "native NSC flux units","post-trace buffered native detector reading");
                    Add(detectors[i],"fractionOfDeclaredSource","same-trace detector["+i+"] source fraction",
                        "ratio","ONLY when user-declared same-trace source denominator supplied");
                }
                warnings.Add("Detector flux samples come from the same successful trace after clearing all detector buffers, not from a later or previous trace.");
                warnings.Add("Configured source power is NOT independently measured launched flux; do not use it automatically as an efficiency denominator.");
                warnings.Add("Detectors can receive the same split/multiply reflected ray; never sum their incident flux or infer a global conservation residual.");
                warnings.Add("Native detector readings already include physical interactions; DO NOT multiply separately measured coating R/T/A, material or CAD losses into them again.");
                warnings.Add("Coating, material, CAD and escaped power have not been independently attributed. An actual ray-path/ZRD audit and source validation are still required.");
                if(detectors.Length>8) warnings.Add("Only the first eight detector observations are summarized.");
                next.Add("zemax_get_nsc_detector");
                next.Add("zemax_nsc_energy_budget");
                break;
            }
            case "zemax_audit_native_zrd":
            {
                if(!json.TryGetProperty("topology",out var graph) ||
                    graph.ValueKind!=JsonValueKind.Object)
                    throw new ArgumentException("Native ZRD result must include completed topology evidence.");
                Add(json,"nativeRayRecords","ZRD native ray records","ray records",
                    "existing explicitly named local .ZRD; not verified same NSC trace");
                Add(json,"nativeSegmentRecords","ZRD native ray segments","segments",
                    "bounded native ZOS-API reader");
                Add(graph,"rootIntensitySum","ZRD candidate root intensity","native intensity proxy",
                    "validated algebraic parent/branch graph only");
                Add(graph,"positiveUnexplainedTransitionDifference","ZRD unexplained transition decrease",
                    "native intensity proxy","not assigned to coatings, material, or mechanical clipping");
                warnings.Add("ZRD file hash does NOT prove these segments were produced by the same trace as separately sampled NSC detectors.");
                warnings.Add("Native parent semantics, polarization, hit-object and detector revisits require version/hardware verification.");
                warnings.Add("ZRD transition differences are NOT verified coating absorption, material absorption, escaped flux or CAD clipping.");
                warnings.Add("Never add detector hit intensity observations across multiple detector objects unless physical disjointness is proven.");
                next.Add("zemax_run_nsc_ray_trace");
                break;
            }
            case "zemax_system_summary":
            {
                Add(json,"numberOfSurfaces","LDE surface count","surfaces",
                    "active sequential metadata; 0 in NSC");
                Add(json,"numberOfNscObjects","NSC object count","objects",
                    "active nonsequential metadata; 0 in sequential");
                Add(json,"omittedSurfaces","unsampled LDE rows","surfaces",
                    "not verified individually by bounded summary");
                warnings.Add("Metadata inventory does not establish optical image quality, physical clipping, or COM readiness.");
                next.Add("zemax_validate_model");
                break;
            }
        }
        return new Interpretation(toolName,numbers,warnings,next,
            "Deterministic explanation of a user-provided successful DTO; no live ZOS read, model edit, hidden assumptions, detector response correction or optical performance ranking.");
    }
}
