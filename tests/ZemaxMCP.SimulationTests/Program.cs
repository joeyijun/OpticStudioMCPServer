using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Services.GlassCatalog;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Services.Jobs;
using ZemaxMCP.Server.Tools.Analysis;
using ZemaxMCP.Server.Tools.NonSequential;
using ZemaxMCP.Server.Tools.Catalog;
using ZemaxMCP.Server.Tools.Base;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            VerifyOperationMetadataAndSnapshotBoundary();
            VerifyScientificNumberTruthfulness();
            VerifyStructuredMtf();
            VerifySequentialEnergyWindows();
            VerifySpectralWeights();
            VerifyDetectorSpectralResponse();
            VerifyMechanicalFootprint();
            VerifyGlobalFootprint();
            VerifyGlobalMechanicalPolygon();
            VerifyFiniteRaySegmentBoundary();
            VerifyCadMultistopFirstBlocker();
            VerifyCadTriangleMeshFirstHit();
            VerifyUserCoatingRta();
            VerifyNativeCoatingReflection();
            VerifyNativeDetectorTiles();
            VerifySignedPixelCalibration();
            VerifyDetectorCsvExport();
            VerifySameTraceLedger();
            VerifyZrdPathEnergyCore();
            VerifyModelSummaryDiff();
            VerifyModelPreflight();
            VerifyPurposePreflight();
            VerifyOpticalResultExplanation();
            VerifyGlassCatalogSafety();
            await VerifyStaDispatcherAsync();
            await VerifyJobManagerAsync();
            await VerifyJobLimitsAsync();
            await VerifyJobHardRecoveryAsync();
            Console.WriteLine("Core safety abstraction, scientific-number truthfulness, bounded LDE energy windows, glass-catalog integrity, STA dispatcher, and bounded server job simulation tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifyPurposePreflight()
    {
        const string nsc = "{\"success\":true,\"mode\":\"NonSequential\",\"numberOfNscObjects\":1,\"numberOfWavelengths\":1,\"filePath\":\"case.zos\"}";
        var check=ModelWorkflowValidator.Assess(nsc,"imaging");
        Assert(!check.ModeApplicable && !check.NoIdentifiedMetadataBlockers &&
            check.Findings.Any(f=>f.Code=="incompatible_mode"),"NSC imaging was incorrectly approved.");
        check=ModelWorkflowValidator.Assess(nsc,"straylight");
        Assert(check.ModeApplicable && check.NoIdentifiedMetadataBlockers &&
            check.RequiredNextChecks.Any(x=>x.Contains("detector",StringComparison.OrdinalIgnoreCase)),
            "Straylight preflight should require explicit NSC detector verification.");
        AssertThrows<ArgumentException>(()=>ModelWorkflowValidator.Assess(nsc,"autorun"),
            "Unrecognized AI workflow was accepted.");
    }

    private static void VerifyOpticalResultExplanation()
    {
        const string detector = "{\"success\":true,\"totalIncidentFlux\":5.0,\"roiFluxIntegral\":2.5,\"launchedFlux\":null}";
        var result=OpticalResultInterpreter.Explain("zemax_get_nsc_detector",detector);
        Assert(result.Metrics.Count==2 &&
            result.Caveats.Any(c=>c.Contains("launched")) &&
            !result.Metrics.Any(m=>m.Name.Contains("efficiency")),
            "Unnormalized detector readings became false efficiency claims.");
        const string footprint = "{\"success\":true,\"surfaces\":[{\"vignettedRays\":4,\"traceErrorRays\":2,\"userMechanicalBoundary\":{\"minimumSignedClearance\":-0.5}}]}";
        var image=OpticalResultInterpreter.Explain("zemax_ray_footprint",footprint);
        Assert(image.Metrics.Any(x=>x.Value==-0.5 && x.Unit=="lens units") &&
               image.Caveats.Any(x=>x.Contains("local",StringComparison.OrdinalIgnoreCase)),
            "Signed local mechanical clearance was not honestly explained.");
        const string trace = "{\"success\":true,\"state\":\"Completed\",\"sameTraceEnergy\":{\"configuredSourcePowerSum\":8,\"userDeclaredLaunchedFlux\":null,\"additiveSourceToDetectorBalanceValid\":false,\"detectors\":[{\"objectNumber\":2,\"incidentFlux\":5,\"fractionOfDeclaredSource\":null}]}}";
        var traceInterpretation=OpticalResultInterpreter.Explain("zemax_run_nsc_ray_trace",trace);
        Assert(traceInterpretation.Metrics.Any(m=>m.Name.Contains("configured model")) &&
               traceInterpretation.Metrics.Any(m=>m.Name.Contains("detector")) &&
               !traceInterpretation.Metrics.Any(m=>m.Name.Contains("source fraction")) &&
               traceInterpretation.Caveats.Any(m=>m.Contains("NOT independently measured")) &&
               traceInterpretation.Caveats.Any(m=>m.Contains("DO NOT multiply")),
            "Same-trace optical result explanation must not invent flux normalization or double-count coatings.");
        AssertThrows<ArgumentException>(()=>OpticalResultInterpreter.Explain(
            "zemax_run_nsc_ray_trace","{\"success\":true,\"state\":\"Queued\"}"),
            "Unfinished NSC trace was interpreted as physical source-to-detector data.");
        const string zrd = "{\"success\":true,\"nativeRayRecords\":4,\"nativeSegmentRecords\":10,\"topology\":{\"rootIntensitySum\":2,\"positiveUnexplainedTransitionDifference\":0.25,\"physicalEnergyClosureEstablished\":false}}";
        var topology=OpticalResultInterpreter.Explain("zemax_audit_native_zrd",zrd);
        Assert(topology.Metrics.Any(x=>x.Name.Contains("unexplained transition")) &&
            !topology.Metrics.Any(x=>x.Name.Contains("absorption")) &&
            topology.Caveats.Any(x=>x.Contains("NOT verified coating absorption")) &&
            topology.Caveats.Any(x=>x.Contains("same trace")),
            "ZRD interpreter assigned topology gaps to unsupported physical losses.");
        const string mesh = "{\"success\":true,\"surfaces\":[],\"opaqueCadMeshPath\":{\"sampledRays\":20,\"firstBlockedRays\":7,\"unknownRays\":3}}";
        var meshExplain=OpticalResultInterpreter.Explain("zemax_ray_footprint",mesh);
        Assert(meshExplain.Metrics.Any(x=>x.Name.Contains("CAD mesh first")) &&
            meshExplain.Caveats.Any(x=>x.Contains("watertight")),
            "Triangle mesh output lost geometric provenance in AI explanation.");
        AssertThrows<ArgumentException>(()=>OpticalResultInterpreter.Explain("zemax_energy_budget",
            "{\"success\":false,\"cases\":[]}"),
            "Failed optical results were interpreted as numerical evidence.");
        AssertThrows<ArgumentException>(()=>OpticalResultInterpreter.Explain("zemax_set_surface",
            "{\"success\":true}"),"Mutating tool output was accepted as read-only analysis.");
    }

    private static void VerifyModelPreflight()
    {
        const string empty = "{\"success\":true,\"mode\":\"NonSequential\",\"numberOfNscObjects\":0,\"numberOfWavelengths\":1,\"configurations\":1,\"filePath\":\"test.zos\"}";
        var findings=ModelPreflightChecks.Evaluate(empty);
        Assert(findings.Any(x=>x.Code=="empty_nsc_scene" && x.Severity=="blocker") &&
               findings.All(x=>x.NextAction.Length>0),
            "Empty NSC scene must produce actionable bounded preflight finding.");
        const string seq = "{\"success\":true,\"mode\":\"Sequential\",\"numberOfSurfaces\":20,\"numberOfWavelengths\":1,\"numberOfFields\":1,\"omittedSurfaces\":12,\"filePath\":\"design.zmx\",\"needsSave\":true,\"keySurfaces\":[{\"isCoordinateBreak\":true}]}";
        findings=ModelPreflightChecks.Evaluate(seq);
        Assert(findings.Any(x=>x.Code=="bounded_lde") &&
               findings.Any(x=>x.Code=="local_coordinate_frame") &&
               findings.Any(x=>x.Code=="unsaved_or_unlocated"),
            "Limited LDE/coordinate-break and unsaved warnings were lost.");
        AssertThrows<ArgumentException>(()=>ModelPreflightChecks.Evaluate("{\"success\":false}"),
            "Failed summary unexpectedly passed preflight.");
    }

    private static void VerifyModelSummaryDiff()
    {
        const string first = "{\"success\":true,\"mode\":\"Sequential\",\"numberOfSurfaces\":5,\"lensUnit\":\"Millimeters\",\"omittedSurfaces\":3,\"keySurfaces\":[{\"number\":1,\"radius\":10.0}],\"fields\":[],\"wavelengths\":[]}";
        const string second = "{\"success\":true,\"mode\":\"Sequential\",\"numberOfSurfaces\":5,\"lensUnit\":\"Millimeters\",\"omittedSurfaces\":3,\"keySurfaces\":[{\"number\":1,\"radius\":11.0}],\"fields\":[],\"wavelengths\":[]}";
        var diff = SystemSummaryComparer.Compare(first,second);
        Assert(diff.Comparable && diff.ChangedProperties==1 &&
            diff.Differences[0].Path=="keySurfaces[1].radius" &&
            diff.Warnings.Any(x=>x.Contains("omittedSurfaces")),
            "Metadata diff should compare matched sample IDs and admit unseen LDE rows.");
        AssertThrows<ArgumentException>(()=>SystemSummaryComparer.Compare(
            "{\"success\":false}",second), "Failed baseline summary was accepted.");
        var unmatched=second.Replace("\"number\":1","\"number\":2");
        var unknown=SystemSummaryComparer.Compare(first,unmatched);
        Assert(unknown.ChangedProperties==0 && unknown.Warnings.Any(x=>x.Contains("UNKNOWN")),
            "Unmatched bounded sample must not imply a verified surface deletion.");
    }

    private static void VerifyZrdPathEnergyCore()
    {
        var segments=new[]{
            new ZrdPathEnergyCore.Segment(7,0,-1,10,1,false),
            new ZrdPathEnergyCore.Segment(7,1,0,4,4,true),
            new ZrdPathEnergyCore.Segment(7,2,0,3,7,false),
            new ZrdPathEnergyCore.Segment(7,3,2,2,8,true),
            new ZrdPathEnergyCore.Segment(8,0,-1,5,2,false)
        };
        var audit=ZrdPathEnergyCore.Audit(segments);
        Assert(audit.Rays==2 && audit.Segments==5 && audit.Branches==1 &&
               Math.Abs(audit.RootIntensitySum-15)<1e-12 &&
               Math.Abs(audit.PositiveUnexplainedTransitionDifference-4)<1e-12 &&
               audit.NegativeTransitionDifferenceMagnitude==0 &&
               Math.Abs(audit.LeafIntensitySum-11)<1e-12 &&
               audit.DetectorHitEvents==2 &&
               !audit.PhysicalCoatingMaterialClippingAttributionKnown &&
               !audit.DetectorHitEnergyDisjoint &&
               !audit.PhysicalEnergyClosureEstablished,
            "ZRD branch accounting invented physical conservation or incorrectly summed split-ray intensity.");
        AssertThrows<ArgumentException>(()=>ZrdPathEnergyCore.Audit(new[]{
            new ZrdPathEnergyCore.Segment(1,0,-1,1,1,false),
            new ZrdPathEnergyCore.Segment(1,1,4,0.5,2,false)
        }),"Missing ZRD ray parent was accepted.");
        AssertThrows<ArgumentException>(()=>ZrdPathEnergyCore.Audit(new[]{
            new ZrdPathEnergyCore.Segment(1,0,-1,1,1,false),
            new ZrdPathEnergyCore.Segment(1,0,-1,1,1,false)
        }),"Duplicate ZRD child node was accepted.");
        AssertThrows<ArgumentException>(()=>ZrdPathEnergyCore.Audit(new[]{
            new ZrdPathEnergyCore.Segment(1,0,-1,double.NaN,1,false)
        }),"Nonfinite physical power was accepted.");
        var gain=ZrdPathEnergyCore.Audit(new[]{
            new ZrdPathEnergyCore.Segment(1,0,-1,1,1,false),
            new ZrdPathEnergyCore.Segment(1,1,0,2,2,false)
        });
        Assert(gain.NegativeTransitionDifferenceMagnitude==1 &&
               !gain.PhysicalEnergyClosureEstablished,
            "Intensity gain was hidden or renormalized away.");
    }

    private static void VerifySameTraceLedger()
    {
        var ledger=NscSameTraceLedger.Build(10,new[]{
            (Id:2,Type:"DetectorRectangle",Flux:4.0,Hits:12.0),
            (Id:4,Type:"DetectorRectangle",Flux:3.0,Hits:9.0)
        },true);
        Assert(ledger.SameTraceCaptured && ledger.Detectors.Count==2 &&
            Math.Abs(ledger.Detectors[0].FractionOfDeclaredSource!.Value-0.4)<1e-12 &&
            !ledger.AdditiveSourceToDetectorBalanceValid && ledger.UnassignedEnergy==null &&
            ledger.CoatingLossStatus=="not directly measured" &&
            ledger.MechanicalClippingStatus=="not directly measured",
            "Same-trace ledger summed overlapping detectors or invented unavailable R/T/A/geometry losses.");
        var configured=NscSameTraceLedger.Build(null,new[]{
            (Id:2,Type:"DetectorRectangle",Flux:4.0,Hits:12.0)
        },true,new[]{
            new NscSameTraceLedger.ConfiguredSource(1,3.0,1000,1),
            new NscSameTraceLedger.ConfiguredSource(5,2.0,0,0)
        });
        Assert(configured.ConfiguredSourcePowerSum==5 &&
               configured.Detectors[0].FractionOfDeclaredSource==null &&
               !configured.AdditiveSourceToDetectorBalanceValid &&
               configured.ConfiguredSourcePowerInterpretation.Contains("not measured"),
            "Native configured power was confused with measured launched flux.");
        AssertThrows<ArgumentException>(()=>NscSameTraceLedger.Build(null,new[]{
            (Id:2,Type:"DetectorRectangle",Flux:4.0,Hits:12.0)
        },true,new[]{
            new NscSameTraceLedger.ConfiguredSource(1,double.NaN,1000,1)
        }),"Nonfinite source power metadata accepted.");
        var withoutSource=NscSameTraceLedger.Build(null,new[]{
            (Id:2,Type:"DetectorRectangle",Flux:4.0,Hits:12.0)
        },true);
        Assert(withoutSource.Detectors.Single().FractionOfDeclaredSource==null,
            "Missing source denominator was fabricated.");
        AssertThrows<ArgumentException>(()=>NscSameTraceLedger.Build(10,new[]{
            (Id:2,Type:"DetectorRectangle",Flux:4.0,Hits:12.0)
        },false),"Old un-cleared detector readings treated as same trace.");
        AssertThrows<ArgumentException>(()=>NscSameTraceLedger.Build(10,new[]{
            (Id:2,Type:"DetectorRectangle",Flux:4.0,Hits:12.0),
            (Id:2,Type:"DetectorRectangle",Flux:1.0,Hits:2.0)
        },true),"Duplicate detector flux contributions were accepted.");
        AssertThrows<ArgumentException>(()=>NscSameTraceLedger.Build(10,new[]{
            (Id:2,Type:"DetectorRectangle",Flux:double.NaN,Hits:12.0)
        },true),"Nonfinite detector flux passed a native ledger.");
    }

    private static void VerifyDetectorCsvExport()
    {
        var dir=Path.Combine(Path.GetTempPath(),"ZemaxCsvTest-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path=Path.Combine(dir,"detector.csv");
        try
        {
            var summary=NscDetectorCsvExport.Write(path,false,2,3,0,1,2,2,
                (y,x)=>y*10+x,CancellationToken.None);
            Assert(summary.PixelCount==4 && summary.Sum==26 &&
                summary.Minimum==1 && summary.Maximum==12 &&
                summary.Sha256.Length==64,"Atomic native detector CSV metadata incorrect.");
            var lines=File.ReadAllLines(path);
            Assert(lines.SequenceEqual(new[]{
                "row,column,value","0,1,1","0,2,2","1,1,11","1,2,12"}),
                "Native row/column CSV ordering or format incorrect.");
            AssertThrows<IOException>(()=>NscDetectorCsvExport.Write(path,false,2,3,0,0,1,1,
                (_,_)=>99,CancellationToken.None),"Accidental CSV overwrite was permitted.");
            AssertThrows<ArgumentException>(()=>NscDetectorCsvExport.Write(path,true,1024,1024,0,0,1024,1024,
                (_,_)=>0,CancellationToken.None),"Unbounded export was accepted.");
            AssertThrows<InvalidDataException>(()=>NscDetectorCsvExport.Write(path,true,2,3,0,0,1,2,
                (_,x)=>x==0?1:double.NaN,CancellationToken.None),
                "Invalid pixel data was committed.");
            Assert(File.ReadAllLines(path).Length==5,"Failed CSV replacement corrupted existing file.");
            using var cancelled=new CancellationTokenSource();
            cancelled.Cancel();
            AssertThrows<OperationCanceledException>(()=>NscDetectorCsvExport.Write(path,true,2,3,0,0,1,1,
                (_,_)=>42,cancelled.Token),"Cancelled export wrote its final destination.");
            Assert(File.ReadAllLines(path).Length==5 &&
                Directory.GetFiles(dir).Length==1,
                "A cancelled/failed export left a temporary or replaced CSV.");
        }
        finally { Directory.Delete(dir,true); }
    }

    private static void VerifySignedPixelCalibration()
    {
        var peak=NscPixelCalibration.ConvertPeak(0,0,4,6,0.1,0.2,1,-1);
        Assert(Math.Abs(peak.LocalX+0.25)<1e-12 &&
               Math.Abs(peak.LocalY-0.3)<1e-12 &&
               peak.Evidence.Contains("USER-declared"),
            "Native detector coordinate calibration invented axis conventions.");
        var flipped=NscPixelCalibration.ConvertPeak(0,0,4,6,0.1,0.2,-1,1);
        Assert(Math.Abs(flipped.LocalX-0.25)<1e-12 &&
               Math.Abs(flipped.LocalY+0.3)<1e-12,
            "Calibrated detector signed axis mapping failed.");
        AssertThrows<ArgumentException>(()=>NscPixelCalibration.ConvertPeak(
            0,0,4,6,0.1,0.2,0,1),
            "Uncalibrated native detector column orientation was accepted.");
        AssertThrows<ArgumentException>(()=>NscPixelCalibration.ConvertPeak(
            4,0,4,6,0.1,0.2,1,1),
            "Out-of-detector peak row was accepted.");
    }

    private static void VerifyNativeDetectorTiles()
    {
        var page=NscDetectorTilePreview.MakePlan(130,129,0);
        Assert(page.TotalTiles==9 && page.Tiles.Count==9 &&
            page.Tiles[0] == new NscDetectorTilePreview.Tile(0,0,64,64) &&
            page.Tiles[8] == new NscDetectorTilePreview.Tile(128,128,2,1),
            "Native detector tiles must cover edge pixels without reordering.");
        var big=NscDetectorTilePreview.MakePlan(4096,4096,63);
        Assert(big.TotalTiles==4096 && big.TotalPages==64 &&
            big.Tiles.Count==64 && big.Tiles[0].StartRow==4032,
            "Large detector paging uses overlapping or incorrect indices.");
        AssertThrows<ArgumentOutOfRangeException>(
            ()=>NscDetectorTilePreview.MakePlan(64,64,1),
            "Out-of-range native tile page accepted.");
        var pixels=new[] {new[]{1d,3d,2d,4d},new[]{5d,7d,6d,8d}};
        var heat=NscDetectorTilePreview.MeanHeatmap(pixels,2);
        Assert(heat.Length==2 && heat[0].Length==2 &&
            heat[0][0]==2 && heat[1][1]==7,
            "Native ROI mean heatmap does not preserve row/column binning.");
        AssertThrows<ArgumentException>(
            ()=>NscDetectorTilePreview.MeanHeatmap(pixels,1),
            "Unsupported heatmap bin count accepted.");
    }

    private interface IFakeCoatingRow
    {
        FakeCoatingPerformance GetCoatingPerformanceData();
    }
    private enum FakeCoatingDirection { inward=0, outward=1 }
    private sealed class FakeCoatingParameter
    {
        public double S {get;set;}
        public double P {get;set;}
    }
    private sealed class FakeCoatingPerformance
    {
        public FakeCoatingParameter Reflection {get;}=new();
        public FakeCoatingParameter Transmission {get;}=new();
        public FakeCoatingParameter Absorption {get;}=new();
        public void GetCoatingPerformance(double aoi,double wavelength,FakeCoatingDirection direction)
        {
            if(aoi!=10 || wavelength!=0.55 || direction!=FakeCoatingDirection.inward)
                throw new InvalidOperationException("Coating sample args were not passed through correctly.");
            Reflection.S=0.3; Reflection.P=0.2;
            Transmission.S=0.6; Transmission.P=0.7;
            Absorption.S=0.1; Absorption.P=0.1;
        }
    }
    private sealed class FakeCoatingRow : IFakeCoatingRow
    {
        private readonly FakeCoatingPerformance _data=new();
        public FakeCoatingPerformance GetCoatingPerformanceData()=>_data;
    }
    private interface IFakeOldCoatingRow {}

    private static void VerifyNativeCoatingReflection()
    {
        var row=new FakeCoatingRow();
        var result=CoatingPerformanceReflection.Read(row,typeof(IFakeCoatingRow),
            10,0.55,"inward");
        Assert(Math.Abs(result.Reflection.Unpolarized-0.25)<1e-12 &&
               Math.Abs(result.Transmission.Unpolarized-0.65)<1e-12 &&
               Math.Abs(result.Absorption.Unpolarized-0.1)<1e-12 &&
               Math.Abs(result.ResidualS)<1e-12 &&
               Math.Abs(result.ResidualP)<1e-12 && result.PassiveRange,
            "Official coating S/P reflection, transmission and absorption were not read independently.");
        AssertThrows<NotSupportedException>(()=>CoatingPerformanceReflection.Read(
            new object(),typeof(IFakeOldCoatingRow),10,0.55,"inward"),
            "Old runtime missing coating API must fail rather than fabricating values.");
        AssertThrows<ArgumentException>(()=>CoatingPerformanceReflection.Read(
            row,typeof(IFakeCoatingRow),90,0.55,"inward"),
            "Grazing incidence outside documented range was accepted.");
    }

    private static void VerifyUserCoatingRta()
    {
        var passive=CoatingRtaAudit.Evaluate(4,
            new[]{new[]{0.9,0.08,0.02},new[]{0.04,0.95,0.01}},
            new[]{true,false});
        Assert(passive.Surfaces.Count==2 && passive.Surfaces[0].Surface==4 &&
            Math.Abs(passive.RemainingSelectedPath-0.855)<1e-12 &&
            Math.Abs(passive.RemainingSelectedPath+passive.TotalAbsorbed+
                passive.TotalOtherBranch+passive.TotalUnresolved-1)<1e-12,
            "User R/T/A ledger is not branch-consistent or conservative.");
        AssertThrows<ArgumentException>(()=>CoatingRtaAudit.Evaluate(1,
            new[]{new[]{0.8,0.3,0.0}},new[]{true}),
            "Non-passive coating was accepted.");
        AssertThrows<ArgumentException>(()=>CoatingRtaAudit.Evaluate(1,
            new[]{new[]{double.NaN,0.5,0.2}},new[]{false}),
            "Non-finite coating accepted.");
    }

    private static void VerifyCadTriangleMeshFirstHit()
    {
        static GlobalFootprintProjection.Point3 P(double x,double y,double z)=>new(x,y,z);
        static GlobalRaySegmentBoundary.Segment Segment(double x,double y,double z1,double z2)=>
            new(P(x,y,z1),P(x,y,z2));
        var faces=new[]{
            new CadTriangleMeshAudit.Triangle(new[]{
                new[]{-2d,-2d,5d},new[]{2d,-2d,5d},new[]{0d,2d,5d}},101),
            new CadTriangleMeshAudit.Triangle(new[]{
                new[]{-2d,-2d,8d},new[]{2d,-2d,8d},new[]{0d,2d,8d}},202)
        };
        var rays=new List<IReadOnlyList<GlobalRaySegmentBoundary.Segment?>>{
            new GlobalRaySegmentBoundary.Segment?[]{Segment(0,0,0,10)},
            new GlobalRaySegmentBoundary.Segment?[]{Segment(4,4,0,10)},
            new GlobalRaySegmentBoundary.Segment?[]{null},
            new GlobalRaySegmentBoundary.Segment?[]{Segment(0,0,6,10)}
        };
        var outcome=CadTriangleMeshAudit.Assess(faces,rays);
        Assert(outcome.SampledRays==4 && outcome.FirstBlockedRays==2 &&
               outcome.UnblockedRays==1 && outcome.UnknownRays==1 &&
               outcome.Parts.Single(x=>x.PartId==101).FirstBlockedRays==1 &&
               outcome.Parts.Single(x=>x.PartId==202).FirstBlockedRays==1 &&
               outcome.FirstCriticalHit==P(0,0,5),
            "Mesh audit must attribute at most one earliest first-hit per ray/part.");
        // Supporting-plane coplanarity is NOT itself triangle overlap.
        var coplanar=CadTriangleMeshAudit.Assess(faces,
            new List<IReadOnlyList<GlobalRaySegmentBoundary.Segment?>> {
                new GlobalRaySegmentBoundary.Segment?[]{
                    new(P(20,20,5),P(21,20,5)) },
                new GlobalRaySegmentBoundary.Segment?[]{
                    new(P(-3,0,5),P(3,0,5)) }
            });
        Assert(coplanar.SampledRays==2 && coplanar.UnblockedRays==1 &&
               coplanar.UnknownRays==1 && coplanar.AmbiguousRays==1 &&
               coplanar.FirstBlockedRays==0,
            "Distant coplanar segment must not be marked ambiguous; actual triangle overlap remains unknown.");
        var reversed=CadTriangleMeshAudit.Assess(faces,new List<IReadOnlyList<GlobalRaySegmentBoundary.Segment?>>{
            new GlobalRaySegmentBoundary.Segment?[]{Segment(0,0,10,0)}
        });
        Assert(reversed.Parts.Single(x=>x.PartId==202).FirstBlockedRays==1,
            "Reverse propagation must hit the physically first mesh surface.");
        AssertThrows<ArgumentException>(()=>CadTriangleMeshAudit.Assess(new[]{
            new CadTriangleMeshAudit.Triangle(new[]{
                new[]{0d,0d,0d},new[]{1d,1d,1d},new[]{2d,2d,2d}},1)
        },rays),"Degenerate opaque triangles must be rejected.");
        AssertThrows<ArgumentException>(()=>CadTriangleMeshAudit.Assess(new[]{
            new CadTriangleMeshAudit.Triangle(new[]{
                new[]{0d,0d,double.NaN},new[]{1d,0d,0d},new[]{0d,1d,0d}},1)
        },rays),"Nonfinite triangles must be rejected.");
    }

    private static void VerifyCadMultistopFirstBlocker()
    {
        static double[][] Rect(double z) => new[]{
            new[]{-1d,-1d,z},new[]{1d,-1d,z},
            new[]{1d,1d,z},new[]{-1d,1d,z}};
        static GlobalFootprintProjection.Point3 Point(double x,double z) =>
            new(x,0,z);
        var stops=new[]{
            new CadMultiSegmentAudit.Stop(2,Rect(5),0.01),
            new CadMultiSegmentAudit.Stop(3,Rect(15),0.01),
            new CadMultiSegmentAudit.Stop(2,Rect(8),0.01)
        };
        var rays=new List<IReadOnlyList<GlobalRaySegmentBoundary.Segment?>>{
            new GlobalRaySegmentBoundary.Segment?[]{
                new(Point(2,0),Point(2,10)),new(Point(2,10),Point(2,20))
            },
            new GlobalRaySegmentBoundary.Segment?[]{
                new(Point(0,0),Point(0,10)),new(Point(0,10),Point(4,20))
            },
            new GlobalRaySegmentBoundary.Segment?[]{
                null,new(Point(2,10),Point(2,20))
            },
            new GlobalRaySegmentBoundary.Segment?[]{
                new(Point(0,0),Point(0,10)),new(Point(0,10),Point(0,20))
            }
        };
        var audit=CadMultiSegmentAudit.Assess(2,stops,rays);
        Assert(audit.SampledPupilRays==4 && audit.FirstBlockedRays==2 &&
               audit.FullyCheckedUnblockedRays==1 && audit.UncertainRays==1 &&
               audit.Stops[0].FirstBlockedRays==1 &&
               audit.Stops[1].FirstBlockedRays==1 &&
               audit.Stops[2].FirstBlockedRays==0,
            "Multiple CAD stops double-counted blocked rays or credited untraceable rays.");
        var order=CadMultiSegmentAudit.Assess(2,new[]{
            new CadMultiSegmentAudit.Stop(2,Rect(8),0.01),
            new CadMultiSegmentAudit.Stop(2,Rect(5),0.01)
        },new List<IReadOnlyList<GlobalRaySegmentBoundary.Segment?>>{
            new GlobalRaySegmentBoundary.Segment?[]{new(Point(2,0),Point(2,10))}
        });
        Assert(order.Stops[1].FirstBlockedRays==1 && order.Stops[0].FirstBlockedRays==0,
            "Same-chord mechanical stops must be ordered by real crossing location.");
        var broken=CadMultiSegmentAudit.Assess(2,stops,new List<IReadOnlyList<GlobalRaySegmentBoundary.Segment?>> {
            new GlobalRaySegmentBoundary.Segment?[]{
                new(Point(0,0),Point(0,10)),new(Point(2,10),Point(2,20))
            }
        });
        Assert(broken.FirstBlockedRays==0 && broken.UncertainRays==1 &&
               broken.DiscontinuousRayPaths==1,
            "Two spatially disconnected ray segments were silently joined into a fabricated optical path.");
    }

    private static void VerifyFiniteRaySegmentBoundary()
    {
        var polygon=new[]{new[]{-2d,-1d,5d},new[]{2d,-1d,5d},
            new[]{2d,1d,5d},new[]{-2d,1d,5d}};
        static GlobalRaySegmentBoundary.Segment Line(double x,double z1,double z2)=>
            new(new GlobalFootprintProjection.Point3(x,0,z1),
                new GlobalFootprintProjection.Point3(x,0,z2));
        var segments=new [] {
            Line(0,0,10), // through plane, inside aperture
            Line(3,0,10), // through plane but outside aperture
            Line(0,6,10), // both endpoints beyond plane -> no crossing
            Line(0,5,5.001), // within tolerance at both endpoints: coplanar ambiguous
            Line(0,5,5), // degenerate
            new GlobalRaySegmentBoundary.Segment(
                new GlobalFootprintProjection.Point3(0,0,6),
                new GlobalFootprintProjection.Point3(1,0,6))
        };
        var result=GlobalRaySegmentBoundary.Assess(polygon,segments,0.01);
        Assert(result.InputSegments==6 && result.IntersectedPlane==2 &&
               result.ApertureInside==1 && result.ApertureOutside==1 &&
               result.NoPlaneIntersection==2 && result.CoplanarAmbiguous==1 &&
               result.DegenerateSegments==1 &&
               Math.Abs(result.MinimumSignedApertureClearance!.Value+1)<1e-12 &&
               Math.Abs(result.OutsideFractionOfPlaneIntersections!.Value-0.5)<1e-12 &&
               result.MostCriticalIntersection==
                   new GlobalFootprintProjection.Point3(3,0,5),
            "Finite 3D stop crossing counts, exact hit location or signed aperture clearance are wrong.");
        var sampled=GlobalRaySegmentBoundary.Assess(polygon,segments,0.01,1);
        Assert(sampled.SampleIntersections?.Count==1 && sampled.SamplesTruncated,
            "Bounded global stop plotting samples were not truncated honestly.");
        var reverse=GlobalRaySegmentBoundary.Assess(polygon,
            new[]{Line(3,10,0)},0.01);
        Assert(reverse.IntersectedPlane==1 && reverse.ApertureOutside==1,
            "Reverse optical propagation must not change mechanical ray-plane intersection.");
        var none=GlobalRaySegmentBoundary.Assess(polygon,
            new[]{Line(0,8,9)},0.01);
        Assert(none.IntersectedPlane==0 &&
               none.OutsideFractionOfPlaneIntersections==null,
            "A stop outside the finite segment was incorrectly called a cut.");
        var empty=GlobalRaySegmentBoundary.Assess(polygon,
            Array.Empty<GlobalRaySegmentBoundary.Segment>(),0.01);
        Assert(empty.InputSegments==0 && empty.OutsideFractionOfValidSegments==null,
            "Empty valid optical segments must not produce fabricated throughput.");
        AssertThrows<InvalidDataException>(()=>GlobalRaySegmentBoundary.Assess(
            polygon,new[]{Line(double.NaN,0,10)},0.01),
            "Nonfinite global ray coordinates were accepted.");
        AssertThrows<ArgumentException>(()=>GlobalRaySegmentBoundary.Assess(
            new[]{new[]{0d,0d,0d},new[]{1d,0d,0d},new[]{1d,1d,0d},
                new[]{0d,1d,2d}},segments,0.01),
            "Nonplanar CAD outline was flattened before a ray-plane intersection.");
    }

    private static void VerifyGlobalMechanicalPolygon()
    {
        var polygon=new[]{new[]{-2d,-1d,5d},new[]{2d,-1d,5d},
            new[]{2d,1d,5d},new[]{-2d,1d,5d}};
        var identity=new[]{new[]{1d,0d,0d},new[]{0d,1d,0d},new[]{0d,0d,1d}};
        var local=new (double X,double Y,double Z)[]{
            (0,0,5),(3,0,5),(0,0,5.1),(-2,0,5)};
        var result=GlobalPlanarMechanicalBoundary.Assess(polygon,identity,
            new[]{0d,0d,0d},local,0.01);
        Assert(result.SurvivingRays==4 && result.PlaneMatchedRays==3 &&
               result.PlaneUnmatchedRays==1 && result.OutsideRays==1 &&
               Math.Abs(result.MinimumSignedEdgeClearance!.Value+1)<1e-12 &&
               Math.Abs(result.OutsideFractionOfPlaneMatched!.Value-1d/3)<1e-12,
            "Global planar mechanical result confused outside rays and rays off the boundary plane.");
        var folded=new[]{new[]{0d,-1d,0d},new[]{1d,0d,0d},new[]{0d,0d,1d}};
        var rotated=GlobalPlanarMechanicalBoundary.Assess(polygon,folded,
            new[]{0d,0d,0d},new (double X,double Y,double Z)[]{(0,0,5),(0,-3,5)},0.01);
        Assert(rotated.OutsideRays==1 && rotated.PlaneMatchedRays==2,
            "Global CAD classification failed under a folded rotated LDE frame.");
        AssertThrows<ArgumentException>(()=>GlobalPlanarMechanicalBoundary.Assess(
            new[]{new[]{0d,0d,0d},new[]{1d,0d,0d},
                new[]{1d,1d,0d},new[]{0d,1d,1d}},
            identity,new[]{0d,0d,0d},local,0.01),
            "Noncoplanar CAD polygon was flattened silently.");
        AssertThrows<ArgumentException>(()=>GlobalPlanarMechanicalBoundary.Assess(
            polygon,identity,new[]{0d,0d,0d},local,0),
            "Zero plane matching tolerance was accepted.");
        var none=GlobalPlanarMechanicalBoundary.Assess(polygon,identity,
            new[]{0d,0d,0d},new (double X,double Y,double Z)[]{(0,0,7)},0.01);
        Assert(none.PlaneMatchedRays==0 && none.MinimumSignedEdgeClearance==null &&
               none.OutsideFractionOfPlaneMatched==null,
            "Unmatched ray intercepts were reported as mechanical clipping.");
    }

    private static void VerifyGlobalFootprint()
    {
        var rotation=new[]{new[]{0d,-1d,0d},new[]{1d,0d,0d},new[]{0d,0d,1d}};
        var origin=new[]{10d,20d,30d};
        var projection=GlobalFootprintProjection.Project(rotation,origin,
            new (double X,double Y,double Z)[]{(2,3,4),(0,0,-1)},1);
        Assert(projection.SurvivingRayCount==2 &&
               projection.Minimum==new GlobalFootprintProjection.Point3(7,20,29) &&
               projection.Maximum==new GlobalFootprintProjection.Point3(10,22,34) &&
               projection.SamplePoints[0]==new GlobalFootprintProjection.Point3(7,22,34) &&
               projection.PointsTruncated,
            "Global LDE transform must include actual traced sag/Z and rotation.");
        AssertThrows<ArgumentException>(()=>GlobalFootprintProjection.Project(
            new[]{new[]{double.NaN,0d,0d},new[]{0d,1d,0d},new[]{0d,0d,1d}},
            origin,new (double X,double Y,double Z)[]{(0,0,0)},1),
            "Nonfinite GetGlobalMatrix transform was accepted.");
        AssertThrows<InvalidDataException>(()=>GlobalFootprintProjection.Project(
            rotation,origin,new (double X,double Y,double Z)[]{(0,0,double.NaN)},1),
            "Nonfinite traced local Z was accepted.");
    }

    private static void VerifyMechanicalFootprint()
    {
        var rect = MechanicalFootprintBoundary.Rectangle(new[] {-2d,-1d,2d,1d});
        Assert(Math.Abs(MechanicalFootprintBoundary.SignedClearance(rect,0,0)-1)<1e-12,
            "Inside margin wrong.");
        Assert(Math.Abs(MechanicalFootprintBoundary.SignedClearance(rect,3,0)+1)<1e-12,
            "Outside margin must be negative.");
        Assert(MechanicalFootprintBoundary.SignedClearance(rect,2,0)==0,"Boundary point must have zero clearance.");
        var concave=new[] { new[] {0d,0d}, new[] {4d,0d}, new[] {4d,1d},
            new[] {1d,1d}, new[] {1d,4d}, new[] {0d,4d} };
        MechanicalFootprintBoundary.Validate(concave);
        Assert(MechanicalFootprintBoundary.SignedClearance(concave,0.5,3)>0 &&
            MechanicalFootprintBoundary.SignedClearance(concave,3,3)<0,
            "Concave boundary classification failed.");
        var result=MechanicalFootprintBoundary.Assess(rect,
            new (double X,double Y)[]{(0,0),(3,0),(2,0)},"user-rectangle");
        Assert(result.Outside==1 && Math.Abs(result.OutsideFractionOfSurvivors!.Value-1d/3)<1e-12,
            "Outside-sample fraction wrong.");
        AssertThrows<ArgumentException>(()=>MechanicalFootprintBoundary.Rectangle(new[]{1d,0d,0d,1d}),
            "Reversed rectangle accepted.");
        AssertThrows<ArgumentException>(()=>MechanicalFootprintBoundary.Validate(new[]{
            new[]{0d,0d},new[]{2d,2d},new[]{0d,2d},new[]{2d,0d}}),
            "Self-crossing outline accepted.");
    }

    private static void VerifyDetectorSpectralResponse()
    {
        var weights=SpectralWeighting.Normalize(new[]{3d,1d},2);
        var (response,proxy)=SpectralWeighting.ApplyDetectorResponse(
            new[]{0.8,0.4},weights,new[]{0.5,0d});
        Assert(Math.Abs(response-0.375)<1e-12 &&
               Math.Abs(proxy-0.3)<1e-12,
            "Detector response spectral losses were normalized away.");
        var (_,half)=SpectralWeighting.ApplyDetectorResponse(
            new[]{0.8,0.4},weights,new[]{0.5,0.5});
        Assert(Math.Abs(half-0.35)<1e-12,
            "Uniform detector response must scale the original ray proxy.");
        Assert(SpectralWeighting.ApplyDetectorResponse(new[]{0.8,0.4},
            weights,new[]{0d,0d}).ResponseWeightedRayProxy==0,
            "Zero detector response must yield zero proxy.");
        AssertThrows<ArgumentException>(()=>SpectralWeighting.ApplyDetectorResponse(
            new[]{0.8,0.4},weights,new[]{1.01,0d}),
            "Detector response above one accepted.");
        AssertThrows<ArgumentException>(()=>SpectralWeighting.ApplyDetectorResponse(
            new[]{0.8,0.4},weights,new[]{double.NaN,0d}),
            "Nonfinite detector spectral response accepted.");
        AssertThrows<ArgumentException>(()=>SpectralWeighting.ApplyDetectorResponse(
            new[]{0.8,0.4},weights,new[]{0.5}),
            "Mismatched relative detector bins accepted.");
    }

    private static void VerifySpectralWeights()
    {
        var weights = SpectralWeighting.Normalize(new[] { 8d, 2d, 0d }, 3);
        Assert(Math.Abs(weights.Sum() - 1) < 1e-12 &&
               Math.Abs(SpectralWeighting.WeightedMean(new[] { 0.5, 1d, 0.1 }, weights) - 0.6) < 1e-12,
            "Wavelength weights produced the wrong source-relative aggregate.");
        var large = SpectralWeighting.Normalize(new[] { 1e308, 1e308 }, 2);
        Assert(Math.Abs(large[0] - 0.5) < 1e-12, "Large finite weights caused overflow.");
        AssertThrows<ArgumentException>(() => SpectralWeighting.Normalize(new[] { 0d, 0d }, 2),
            "All-zero source weights accepted.");
        AssertThrows<ArgumentException>(() => SpectralWeighting.Normalize(new[] { -1d, 2d }, 2),
            "Negative source weights accepted.");
        AssertThrows<ArgumentException>(() => SpectralWeighting.Normalize(new[] { double.NaN, 2d }, 2),
            "Non-finite source weights accepted.");
        AssertThrows<ArgumentException>(() => SpectralWeighting.Normalize(new[] { 1d }, 2),
            "Mismatched source weights accepted.");
    }

    private static void VerifySequentialEnergyWindows()
    {
        Assert(SequentialEnergySurfaceRange.Resolve(1, 24, 80) == (1, 24, 24),
            "First bounded LDE window was not 1..24.");
        Assert(SequentialEnergySurfaceRange.Resolve(24, 47, 80) == (24, 47, 24),
            "Adjacent windows must use absolute indices with one-surface overlap.");
        Assert(SequentialEnergySurfaceRange.Resolve(57, 0, 80) == (57, 80, 24),
            "FinalSurface=0 must mean the image plane.");
        Assert(SequentialEnergySurfaceRange.Resolve(80, 80, 80) == (80, 80, 1),
            "One-surface tail window must remain valid.");
        AssertThrows<ArgumentException>(() => SequentialEnergySurfaceRange.Resolve(1, 0, 80),
            "Oversized implicit whole-system window was accepted.");
        AssertThrows<ArgumentException>(() => SequentialEnergySurfaceRange.Resolve(24, 22, 80),
            "Reversed range was accepted.");
        AssertThrows<ArgumentOutOfRangeException>(() => SequentialEnergySurfaceRange.Resolve(0, 3, 80),
            "Surface zero was accepted as start.");
        AssertThrows<ArgumentOutOfRangeException>(() => SequentialEnergySurfaceRange.Resolve(10, 81, 80),
            "A final surface outside the LDE was accepted.");
        AssertThrows<ArgumentOutOfRangeException>(() => SequentialEnergySurfaceRange.Resolve(2, -1, 80),
            "A negative final surface was accepted.");
    }

    private static void VerifyOperationMetadataAndSnapshotBoundary()
    {
        Assert(ZemaxOperationMetadata.GetCommandImpact("SetSurface") == ZemaxOperationImpact.HighImpact, "SetSurface must be high impact.");
        Assert(ZemaxOperationMetadata.GetCommandImpact("FutureUnclassifiedMutation") == ZemaxOperationImpact.HighImpact, "Unknown commands must fail closed.");
        Assert(ZemaxOperationMetadata.GetToolImpact("zemax_set_surface") == ZemaxOperationImpact.HighImpact, "Tool metadata must use the shared high-impact policy.");
        Assert(ZemaxOperationMetadata.GetToolImpact("future_tool") == ZemaxOperationImpact.Caution, "Unknown tools must not be displayed as read-only.");

        var root = Path.Combine(Path.GetTempPath(), "ZemaxMCP-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var oldReadOnly = Environment.GetEnvironmentVariable("ZEMAX_MCP_READ_ONLY");
        var oldSnapshots = Environment.GetEnvironmentVariable("ZEMAX_MCP_SNAPSHOT_DIR");
        try
        {
            Environment.SetEnvironmentVariable("ZEMAX_MCP_READ_ONLY", "false");
            Environment.SetEnvironmentVariable("ZEMAX_MCP_SNAPSHOT_DIR", root);
            var safety = new ZemaxOperationSafety();
            var fake = new FakeSnapshotSystem("C:\\Designs\\demo.zos");
            safety.BeforeOperation(fake, "SetSurface");
            Assert(fake.CopyCalls == 1 && fake.LastCopy?.Closed == true, "High-impact safety must snapshot through the ZOS abstraction and close the copy.");
            Assert(File.Exists(safety.LastSnapshotPath!), "Safety snapshot was not written by the simulated system.");

            safety.BeforeOperation(fake, "GetSystem");
            Assert(fake.CopyCalls == 1, "Read-only operations must not create a snapshot.");

            Environment.SetEnvironmentVariable("ZEMAX_MCP_READ_ONLY", "true");
            var readOnly = new ZemaxOperationSafety();
            AssertThrows<InvalidOperationException>(() => readOnly.BeforeOperation(fake, "SetSurface"), "Read-only mode did not block a high-impact operation.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEMAX_MCP_READ_ONLY", oldReadOnly);
            Environment.SetEnvironmentVariable("ZEMAX_MCP_SNAPSHOT_DIR", oldSnapshots);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void VerifyScientificNumberTruthfulness()
    {
        foreach (var radius in new[] { 0.0, double.PositiveInfinity, double.NegativeInfinity })
        {
            var rawRadius = radius;
            var readback = SurfaceReadback.FromRaw(2, rawRadius, double.PositiveInfinity,
                "", double.NegativeInfinity, 0, "plane", false);
            using var parsed = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(readback));
            Assert(parsed.RootElement.GetProperty("Radius").GetDouble() == 0 &&
                parsed.RootElement.GetProperty("Thickness").ValueKind == System.Text.Json.JsonValueKind.Null &&
                parsed.RootElement.GetProperty("ThicknessState").GetString() == "PositiveInfinity" &&
                parsed.RootElement.GetProperty("SemiDiameterState").GetString() == "NegativeInfinity",
                "Batch surface readback must serialize planes and signed optical infinity safely.");
            Assert(rawRadius.Equals(radius), "Wire normalization must not modify the raw rollback value.");
        }
        var finiteSurface = SurfaceReadback.FromRaw(1, 12.5, 1.25, "N-BK7", 2, -1, "finite", true);
        Assert(finiteSurface.Radius == 12.5 && finiteSurface.Thickness == 1.25 && finiteSurface.SemiDiameter == 2 &&
            finiteSurface.Conic == -1 && finiteSurface.IsStop && finiteSurface.ThicknessState == "Finite",
            "Batch readback must preserve finite values and surface metadata.");
        AssertThrows<InvalidDataException>(() => SurfaceReadback.FromRaw(1, double.NaN, 1, "", 1, 0, "", false), "NaN radius accepted.");
        AssertThrows<InvalidDataException>(() => SurfaceReadback.FromRaw(1, 0, double.NaN, "", 1, 0, "", false), "NaN thickness accepted.");
        AssertThrows<InvalidDataException>(() => SurfaceReadback.FromRaw(1, 0, 1, "", double.NaN, 0, "", false), "NaN semi-diameter accepted.");
        AssertThrows<InvalidDataException>(() => SurfaceReadback.FromRaw(1, 0, 1, "", 1, double.PositiveInfinity, "", false), "Invalid conic accepted.");
        Assert(double.PositiveInfinity.OpticalDimension() == null &&
               double.PositiveInfinity.OpticalDimensionState() == "PositiveInfinity",
               "Optical infinity must be explicitly represented, not fabricated as a finite number.");
        Assert(double.NegativeInfinity.OpticalDimension() == null &&
               double.NegativeInfinity.OpticalDimensionState() == "NegativeInfinity", "Infinity sign was lost.");
        Assert(1.25.OpticalDimension() == 1.25 && 1.25.OpticalDimensionState() == "Finite", "Finite dimension changed.");
        AssertThrows<InvalidDataException>(() => double.NaN.OpticalDimension(), "NaN dimensions must remain errors.");
        Assert(Math.Abs(1.25.Sanitize() - 1.25) < 1e-12, "Finite scientific values must be preserved exactly.");
        AssertThrows<InvalidDataException>(
            () => double.NaN.Sanitize(),
            "NaN must not be rewritten into a plausible finite measurement.");
        AssertThrows<InvalidDataException>(
            () => double.PositiveInfinity.Sanitize(),
            "Infinity must not be rewritten into an arbitrary finite measurement.");
        Assert(double.PositiveInfinity.SanitizeRadius() == 0,
            "Infinite optical radius should retain the established plane-surface convention.");
        AssertThrows<InvalidDataException>(
            () => double.NaN.SanitizeRadius(),
            "NaN radius must not be misreported as a plane surface.");
        var cardinal = new CardinalPoints { Success = true, Magnification = 0, Wavelength = 1 };
        var dimensions = typeof(CardinalPoints).GetProperties()
            .Where(property => property.PropertyType == typeof(double?)).ToArray();
        Assert(dimensions.Length == 9, "All nine cardinal optical dimensions must support explicit infinity.");
        foreach (var value in new[] { 1.25, double.PositiveInfinity, double.NegativeInfinity })
        {
            foreach (var dimension in dimensions)
            {
                dimension.SetValue(cardinal, value.OpticalDimension());
                cardinal.DimensionStates[dimension.Name] = value.OpticalDimensionState();
            }
            var json = System.Text.Json.JsonSerializer.Serialize(cardinal);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            foreach (var dimension in dimensions)
            {
                var field = document.RootElement.GetProperty(dimension.Name);
                Assert(double.IsInfinity(value) ? field.ValueKind == System.Text.Json.JsonValueKind.Null : field.GetDouble() == value,
                    "Cardinal-point infinity or finite readback was lost during strict JSON serialization.");
                Assert(document.RootElement.GetProperty("DimensionStates").GetProperty(dimension.Name).GetString() == value.OpticalDimensionState(),
                    "Cardinal-point JSON lost the infinity sign.");
            }
        }
    }

    private static void VerifyStructuredMtf()
    {
        var field = ZemaxMCP.Server.Tools.Analysis.MtfSeriesReader.Read("视场：0 度", 1,
            new[] { 0.0, 10.0 }, new double[,] { { 1, 1 }, { 0.8, 0.7 } });
        Assert(field.FieldLabel == "视场：0 度" && field.TangentialMtf![1] == 0.8 && field.SagittalMtf![1] == 0.7,
            "Structured MTF must preserve localized labels and distinguish tangential/sagittal columns.");
        AssertThrows<InvalidDataException>(() => ZemaxMCP.Server.Tools.Analysis.MtfSeriesReader.Read("bad", 1,
            new[] { 0.0 }, new double[,] { { double.NaN, 1 } }), "Invalid MTF measurements must fail.");
        AssertThrows<InvalidDataException>(() => ZemaxMCP.Server.Tools.Analysis.MtfSeriesReader.Read("bad", 1,
            new[] { 0.0, 10.0 }, new double[,] { { 1, 1 } }), "MTF shape mismatch must fail.");
    }

    private static void VerifyGlassCatalogSafety()
    {
        AssertThrows<ArgumentException>(
            () => CatalogExportService.ValidateCatalogName(@"..\escape"),
            "Glass catalog names must not permit path traversal.");
        AssertThrows<ArgumentException>(
            () => CatalogExportService.ValidateCatalogName("CON"),
            "Glass catalog names must reject reserved Windows device names.");
        AssertThrows<ArgumentOutOfRangeException>(
            () => GlassFilterService.Validate(new GlassFilterCriteria { Wn = -1 }),
            "Glass filters must reject negative distance weights.");
        AssertThrows<ArgumentOutOfRangeException>(
            () => GlassFilterService.Validate(new GlassFilterCriteria { DistanceRadius = double.NaN }),
            "Glass filters must reject non-finite values.");
        AssertThrows<ArgumentException>(
            () => GlassFilterService.Validate(new GlassFilterCriteria { NdMin = 1.7, NdMax = 1.6 }),
            "Glass filters must reject contradictory min/max bounds.");

        var root = Path.Combine(Path.GetTempPath(), "ZemaxMCP-glass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var safePath = CatalogExportService.GetCatalogPath(root, "SAFE");
            Assert(Path.GetDirectoryName(safePath) == root, "Safe catalog path did not remain in the requested Glasscat directory.");

            var glass = new GlassEntry
            {
                Name = "TEST",
                CatalogName = "SOURCE",
                Nd = 1.5168,
                Vd = 64.17,
                RawLines = new List<string>
                {
                    "NM TEST 2 0 1.5168 64.17 0 1",
                    "LD 0.4 0.7"
                }
            };

            File.WriteAllText(safePath, "original");
            AssertThrows<IOException>(
                () => CatalogExportService.Export(new[] { glass }, safePath, "SAFE", overwrite: false),
                "overwrite=false must remain a final no-clobber guarantee.");
            Assert(File.ReadAllText(safePath) == "original", "A rejected no-overwrite export modified the existing catalog.");

            CatalogExportService.Export(new[] { glass }, safePath, "SAFE", overwrite: true);
            var exported = File.ReadAllText(safePath);
            Assert(exported.Contains("NM TEST 2 0 1.5168 64.17 0 1", StringComparison.Ordinal), "Overwrite export did not publish the expected AGF contents.");

            var validAgf = Path.Combine(root, "VALID.agf");
            File.WriteAllLines(validAgf, new[]
            {
                "NM VALID 2 0 1.5168 64.17 0 1",
                "LD 0.4 0.7"
            });
            var parsed = AgfFileParser.ParseCatalog(validAgf, "VALID");
            Assert(parsed.Count == 1 && parsed[0].Name == "VALID" && Math.Abs(parsed[0].Nd - 1.5168) < 1e-12,
                "Valid AGF data was not parsed as expected.");

            var malformedAgf = Path.Combine(root, "BAD.agf");
            File.WriteAllText(malformedAgf, "NM BAD 2 0 1.5168 not-a-vd 0 1");
            try
            {
                AgfFileParser.ParseCatalog(malformedAgf, "BAD");
                throw new InvalidOperationException("Malformed AGF numeric data was accepted.");
            }
            catch (FormatException exception)
            {
                Assert(exception.Message.Contains("line 1", StringComparison.OrdinalIgnoreCase), "Malformed AGF error did not identify the source line.");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task VerifyStaDispatcherAsync()
    {
        using var dispatcher = new ZosApiDispatcher();
        var threadIds = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => dispatcher.GetExecutingThreadIdAsync()));
        Assert(threadIds.Distinct().Count() == 1 && threadIds[0] == dispatcher.ThreadId, "ZOS dispatcher did not serialize calls onto one long-lived thread.");
        Assert(dispatcher.ApartmentState == ApartmentState.STA, "ZOS dispatcher must run in STA.");

        using var bounded = new ZosApiDispatcher(maxPending: 2);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var blocking = bounded.InvokeAsync(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(8));
            return 1;
        });
        Assert(entered.Wait(TimeSpan.FromSeconds(3)), "STA operation did not start.");
        using var cancel = new CancellationTokenSource();
        var cancelled = bounded.InvokeAsync(() => 2, cancel.Token);
        var later = bounded.InvokeAsync(() => 3);
        Assert(bounded.PendingCount == 2, "STA queue did not track the bounded pending count.");
        AssertThrows<InvalidOperationException>(() => bounded.InvokeAsync(() => 4),
            "A full STA queue must fail closed instead of growing without bound.");
        cancel.Cancel();
        try
        {
            await cancelled.WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException("Cancelled queued STA work unexpectedly executed.");
        }
        catch (OperationCanceledException) { }
        Assert(bounded.PendingCount == 1,
            "Cancellation should free a pending STA queue slot before a blocking COM operation returns.");
        var replacement = bounded.InvokeAsync(() => 5);
        Assert(bounded.PendingCount == 2, "STA queue slot was not reusable after cancellation.");
        release.Set();
        Assert(await blocking == 1 && await later == 3 && await replacement == 5,
            "Surviving STA work must execute in FIFO order after cancelling a queued item.");

        using var closing = new ZosApiDispatcher(maxPending: 1);
        using var closeEntered = new ManualResetEventSlim(false);
        using var closeRelease = new ManualResetEventSlim(false);
        var active = closing.InvokeAsync(() =>
        {
            closeEntered.Set();
            closeRelease.Wait(TimeSpan.FromSeconds(8));
            return 7;
        });
        Assert(closeEntered.Wait(TimeSpan.FromSeconds(3)), "Disposal test STA did not start.");
        var abandoned = closing.InvokeAsync(() => 8);
        closing.Dispose();
        try
        {
            await abandoned;
            throw new InvalidOperationException("Disposed STA accepted a queued operation.");
        }
        catch (ObjectDisposedException) { }
        closeRelease.Set();
        Assert(await active == 7, "An in-flight STA call must not access disposed queue state.");
    }

    private static async Task VerifyJobManagerAsync()
    {
        using var jobs = new McpJobManager();
        var completed = new TaskCompletionSource<McpJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.JobChanged += snapshot =>
        {
            if (snapshot.ToolName == "simulated-long-operation" && snapshot.State == McpJobState.Cancelled)
                completed.TrySetResult(snapshot);
        };
        var queued = jobs.Enqueue("simulated-long-operation", async context =>
        {
            context.ReportProgress(0.25, "started");
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
        }, TimeSpan.FromSeconds(5));
        Assert(jobs.Cancel(queued.JobId, out _), "Queued/running job could not be cancelled.");
        var terminal = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(terminal.State == McpJobState.Cancelled, "Cancelled job did not reach a terminal cancelled state.");
        Assert(terminal.Elapsed == terminal.CompletedAt - terminal.StartedAt, "Cancelled elapsed time must end at cancellation.");
        await Task.Delay(70);
        Assert(jobs.Get(terminal.JobId)!.Elapsed == terminal.Elapsed, "Cancelled elapsed time must not grow on later polling.");
    }

    private static async Task VerifyJobLimitsAsync()
    {
        using (var boundedQueue = new McpJobManager(maxHistory: 4, maxPending: 2))
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            boundedQueue.Enqueue("blocking", async context =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            boundedQueue.Enqueue("queued-1", _ => Task.CompletedTask);
            boundedQueue.Enqueue("queued-2", _ => Task.CompletedTask);
            AssertThrows<InvalidOperationException>(
                () => boundedQueue.Enqueue("queued-overflow", _ => Task.CompletedTask),
                "Background jobs beyond the configured pending limit must be rejected.");
            release.TrySetResult();
        }

        using (var boundedHistory = new McpJobManager(maxHistory: 3, maxPending: 8, maxResultHistory: 2))
        {
            var completedCount = 0;
            var allCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            boundedHistory.JobChanged += snapshot =>
            {
                if (snapshot.State == McpJobState.Completed &&
                    snapshot.ToolName.StartsWith("history-", StringComparison.Ordinal) &&
                    Interlocked.Increment(ref completedCount) == 5)
                    allCompleted.TrySetResult();
            };

            for (var index = 0; index < 5; index++)
            {
                var resultValue = index;
                boundedHistory.Enqueue("history-" + index, context =>
                {
                    context.SetResult(new string('x', 1024) + resultValue);
                    return Task.CompletedTask;
                });
            }

            await allCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            var retained = boundedHistory.List();
            Assert(retained.Count == 3, "Completed job history must be trimmed to the configured retention limit.");
            Assert(retained.All(job => job.State == McpJobState.Completed), "Retained job history unexpectedly contains non-terminal jobs.");
            foreach (var job in retained)
                Assert(job.Elapsed == job.CompletedAt - job.StartedAt, "Completed job elapsed time must stop at completion.");
            var finished = retained[0];
            await Task.Delay(70);
            Assert(boundedHistory.Get(finished.JobId)!.Elapsed == finished.Elapsed, "Terminal elapsed time must not grow on later polling.");
            Assert(retained.Count(job => !job.ResultExpired && job.Result != null) == 2,
                "Only the configured newest result payloads should remain resident.");
            Assert(retained.Count(job => job.ResultExpired && job.Result == null) == 1,
                "Older retained job metadata must explicitly mark its discarded result payload.");
        }
    }

    private static async Task VerifyJobHardRecoveryAsync()
    {
        var recovery = new TaskCompletionSource<McpJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var jobs = new McpJobManager(
            maxHistory: 4,
            maxPending: 2,
            cancellationGrace: TimeSpan.FromMilliseconds(75),
            hardRecoveryAction: snapshot => recovery.TrySetResult(snapshot));

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = jobs.Enqueue("hung-background-job", async context =>
        {
            started.TrySetResult();
            await never.Task;
            // Simulate an abandoned, non-cooperative COM operation returning
            // after the hard-recovery decision was already published.
            context.SetResult("late-result-must-not-leak");
            context.ReportProgress(1, "late success");
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedExecuted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = jobs.Enqueue("must-not-run-after-hard-recovery", _ =>
        {
            queuedExecuted.TrySetResult();
            return Task.CompletedTask;
        });
        Assert(jobs.Cancel(job.JobId, out _), "A running background job could not enter cancellation.");
        var hardFailure = await recovery.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(hardFailure.JobId == job.JobId && hardFailure.State == McpJobState.Failed,
            "A non-draining background job did not transition to failed hard-recovery state.");
        Assert(hardFailure.Message.Contains("hard recovery", StringComparison.OrdinalIgnoreCase),
            "Hard-recovery failure did not explain why the Worker generation must be replaced.");
        var abandoned = jobs.Get(waiting.JobId);
        Assert(abandoned is { State: McpJobState.Failed } && !queuedExecuted.Task.IsCompleted,
            "A queued background job was not failed when its Worker generation required hard recovery.");
        AssertThrows<InvalidOperationException>(
            () => jobs.Enqueue("unsafe-post-recovery", _ => Task.CompletedTask),
            "A Worker with an orphaned COM call must reject all new background jobs.");
        never.TrySetResult();
        await Task.Delay(100);
        var stillFailed = jobs.Get(job.JobId);
        Assert(stillFailed is { State: McpJobState.Failed, Result: null } &&
               stillFailed.Message.Contains("hard recovery", StringComparison.OrdinalIgnoreCase),
            "A late COM result/progress resurrected an already-failed Job after hard recovery.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class FakeSnapshotSystem : IZosSystemSnapshot
    {
        public FakeSnapshotSystem(string systemFile) => SystemFile = systemFile;
        public string? SystemFile { get; }
        public int CopyCalls { get; private set; }
        public FakeSnapshotSystem? LastCopy { get; private set; }
        public bool Closed { get; private set; }
        public IZosSystemSnapshot? CopySystem()
        {
            CopyCalls++;
            LastCopy = new FakeSnapshotSystem(SystemFile!);
            return LastCopy;
        }
        public void SaveAs(string path) => File.WriteAllText(path, "simulated-zos-snapshot");
        public void Close(bool saveChanges) => Closed = true;
    }
}
