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
            VerifyMechanicalFootprint();
            VerifyGlobalFootprint();
            VerifyGlobalMechanicalPolygon();
            VerifyUserCoatingRta();
            VerifyNativeDetectorTiles();
            VerifyDetectorCsvExport();
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
