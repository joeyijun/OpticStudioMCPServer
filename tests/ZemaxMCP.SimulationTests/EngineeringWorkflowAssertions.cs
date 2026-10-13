using System.Text.Json;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tools.Catalog;

internal static class EngineeringWorkflowAssertions
{
    public static void Run()
    {
        VerifyPreviewAndGuidance();
        var baseline=EngineeringBaselineStore.Register(new[]{1,2},"model-a");
        EngineeringBaselineStore.Validate(baseline.Id,new[]{1},"model-a");
        Reject(()=>EngineeringBaselineStore.Validate(baseline.Id,new[]{1},"changed-model"));
        Reject(()=>EngineeringBaselineStore.Validate(baseline.Id,new[]{3},"model-a"));
        EngineeringBaselineStore.RecordApplied(baseline.Id,"after-a","snapshot.zmx");
        Reject(()=>EngineeringBaselineStore.Validate(baseline.Id,new[]{1},"model-a"));
        Assert(JsonSerializer.SerializeToElement(EngineeringBaselineStore.Verify(baseline.Id,"after-a")).GetProperty("readbackVerified").GetBoolean());
        Assert(!JsonSerializer.SerializeToElement(EngineeringBaselineStore.Verify(baseline.Id,"tampered-after")).GetProperty("readbackVerified").GetBoolean());
        Reject(()=>EngineeringBaselineStore.Get(new string('a',32)));

        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        EngineeringObservation.Conditions Conditions(string unit="Millimeters",int omitted=0)=>new("model-1","lens.zos","Sequential",unit,
            1,1,"Angle","EntrancePupilDiameter",10,new[]{new EngineeringObservation.FieldCondition(1,0,0,1,true,0,0,0,0,0)},
            new[]{new EngineeringObservation.WavelengthCondition(1,0.55,1,true,true)},omitted,0,"Test recorded scope","CyclesPerMillimeter","Radial");
        string Evidence(string command="SpotDiagram",int sampling=3,string unit="Millimeters",int omitted=0)=>JsonSerializer.Serialize(new {
            operations=new[]{new EngineeringObservation.OperationEvidence(command,DateTimeOffset.UtcNow,"known",Conditions(unit,omitted),
                command=="MTF" ? new Dictionary<string,string?>{{"frequency","50"},{"wavelength","0"},{"sampling",sampling.ToString()}} :
                new Dictionary<string,string?>{{"field","1"},{"wavelength","0"},{"rings",sampling.ToString()}},null)} },options);
        string Spot(double value,bool success=true)=>JsonSerializer.Serialize(new { success,rmsSpotRadius=value },options);
        var a=AnalysisEvidenceStore.Record("zemax_spot_diagram",Spot(2),Evidence())!;
        var b=AnalysisEvidenceStore.Record("zemax_spot_diagram",Spot(1),Evidence())!;
        var rules=new[]{new AnalysisEvidenceStore.Rule("/rmsSpotRadius","decrease")};
        Assert(JsonSerializer.SerializeToElement(AnalysisEvidenceStore.Compare(a,b,
            new[]{new AnalysisEvidenceStore.Rule("/rmsSpotRadius","decrease",MinimumChange:1)})).GetProperty("criteriaPassed").GetBoolean());
        Assert(!JsonSerializer.SerializeToElement(AnalysisEvidenceStore.Compare(a,a,rules)).GetProperty("criteriaPassed").GetBoolean());
        Assert(JsonSerializer.SerializeToElement(AnalysisEvidenceStore.Compare(a,b,rules)).GetProperty("criteriaPassed").GetBoolean());
        Assert(!JsonSerializer.SerializeToElement(AnalysisEvidenceStore.Compare(b,a,rules)).GetProperty("criteriaPassed").GetBoolean());
        foreach(var bad in new[]{Evidence(sampling:4),Evidence(unit:"Inches"),Evidence(omitted:1)})
        { var id=AnalysisEvidenceStore.Record("zemax_spot_diagram",Spot(1),bad)!; Reject(()=>AnalysisEvidenceStore.Compare(a,id,rules)); }
        foreach(var bad in new[]{Evidence().Replace("\"fieldNormalization\":\"Radial\"","\"fieldNormalization\":\"Rectangular\""),
            Evidence().Replace("\"vdx\":0","\"vdx\":0.1"),Evidence().Replace("\"vdy\":0","\"vdy\":null")})
        { var id=AnalysisEvidenceStore.Record("zemax_spot_diagram",Spot(1),bad)!; Reject(()=>AnalysisEvidenceStore.Compare(a,id,rules)); }
        var failed=AnalysisEvidenceStore.Record("zemax_spot_diagram",Spot(0,false),Evidence())!;
        var failedCriterion=JsonSerializer.SerializeToElement(AnalysisEvidenceStore.Compare(b,a,rules));
        Assert(failedCriterion.GetProperty("comparisonState").GetString()=="selected_criteria_not_met" &&
            !failedCriterion.GetProperty("saveAuthorized").GetBoolean() &&
            failedCriterion.GetProperty("metrics")[0].GetProperty("criterion").GetProperty("goal").GetString()=="decrease");
        try { AnalysisEvidenceStore.Compare(a,new string('b',32),rules); throw new Exception("Missing evidence accepted."); }
        catch(AnalysisEvidenceException error) { Assert(error.Code=="analysis_evidence_unavailable" && error.NextAction.Length>0); }
        var large=AnalysisEvidenceStore.Record("zemax_spot_diagram",Spot(double.MaxValue),Evidence())!;
        var negative=AnalysisEvidenceStore.Record("zemax_spot_diagram",Spot(-double.MaxValue),Evidence())!;
        Reject(()=>AnalysisEvidenceStore.Compare(large,negative,rules));
        foreach(var invalidEvidence in new[]{
            Evidence().TrimEnd('}')+",\"truncated\":true}",
            Evidence().Replace("\"known\"","\"unavailable\"")})
        {
            var invalidId=AnalysisEvidenceStore.Record("zemax_spot_diagram",Spot(1),invalidEvidence)!;
            Reject(()=>AnalysisEvidenceStore.Compare(a,invalidId,rules));
        }
        Reject(()=>AnalysisEvidenceStore.Compare(a,failed,rules));
        var missing=AnalysisEvidenceStore.Record("zemax_spot_diagram","{\"success\":true,\"rmsSpotRadius\":null}",Evidence())!;
        Reject(()=>AnalysisEvidenceStore.Compare(a,missing,rules));
        Reject(()=>AnalysisEvidenceStore.Compare(a,b,new[]{new AnalysisEvidenceStore.Rule("/airyRadius","decrease")}));
        string Mtf(double frequency)=>JsonSerializer.Serialize(new { success=true,fields=new[]{new {fieldNumber=1,frequencies=new[]{frequency},tangentialMtf=new[]{0.5},sagittalMtf=new[]{0.4}}} },options);
        var ma=AnalysisEvidenceStore.Record("zemax_fft_mtf",Mtf(10),Evidence("MTF"))!;
        var mb=AnalysisEvidenceStore.Record("zemax_fft_mtf",Mtf(20),Evidence("MTF"))!;
        var match=AnalysisEvidenceStore.Record("zemax_fft_mtf",Mtf(10),Evidence("MTF"))!;
        var labelled=JsonSerializer.SerializeToElement(AnalysisEvidenceStore.Compare(ma,match,
            new[]{new AnalysisEvidenceStore.Rule("/fields/0/tangentialMtf/0","range",Minimum:0.1)}));
        Assert(labelled.GetProperty("metrics")[0].GetProperty("fieldNumber").GetInt32()==1 &&
            labelled.GetProperty("metrics")[0].GetProperty("spatialFrequency").GetDouble()==10);
        Reject(()=>AnalysisEvidenceStore.Compare(ma,match,
            new[]{new AnalysisEvidenceStore.Rule("/fields/99999999999999999999/tangentialMtf/0","range",Minimum:0.1)}));
        Reject(()=>AnalysisEvidenceStore.Compare(ma,mb,new[]{new AnalysisEvidenceStore.Rule("/fields/0/tangentialMtf/0","range",Minimum:0.1)}));
        using(var observation=new EngineeringObservation())
        {
            observation.Observe(()=>Conditions(),"SpotDiagram",null);
            using(var nested=new EngineeringObservation())
            {
                nested.Observe(()=>throw new InvalidOperationException("metadata unavailable"),"SpotDiagram",null);
                Assert(nested.Operations[0].Status=="unavailable" && nested.Operations[0].Conditions==null);
            }
            Assert(ReferenceEquals(EngineeringObservation.Current,observation));
            for(var i=0;i<20;i++) observation.Observe(()=>Conditions(),"SpotDiagram",null);
            Assert(observation.Operations.Count==16 && observation.Truncated);
        }
        Assert(EngineeringObservation.Current==null);
        Console.WriteLine("Engineering workflow: stale/consumed targets, independent readback, known-condition numeric acceptance, mismatched units/sampling, failed/null metrics and MTF axes passed.");
    }
    private static void VerifyPreviewAndGuidance()
    {
        var untouched=EngineeringBaselineStore.Register(new[]{1},"untouched");
        var awaiting=JsonSerializer.SerializeToElement(EngineeringBaselineStore.Verify(untouched.Id,"untouched"));
        Assert(awaiting.GetProperty("workflowState").GetString()=="awaiting_edit" &&
            !awaiting.GetProperty("saveAuthorized").GetBoolean());
        EngineeringBaselineStore.RecordApplied(untouched.Id,"edited","snapshot.zmx");
        var changed=JsonSerializer.SerializeToElement(EngineeringBaselineStore.Verify(untouched.Id,"changed-after"));
        Assert(changed.GetProperty("workflowState").GetString()=="model_changed_after_edit");
        const string state="{\"conditions\":{\"lensUnit\":\"Millimeters\"},\"rows\":[{\"number\":1,\"radius\":\"Infinity\",\"thickness\":\"3\",\"semiDiameter\":\"1\",\"conic\":\"0\",\"material\":\"N-BK7\",\"comment\":\"note\",\"isStop\":true}]}";
        var preview=JsonSerializer.SerializeToElement(EngineeringEditPreview.Build(state,
            JsonSerializer.Deserialize<JsonElement>("[{\"surfaceNumber\":1,\"radius\":20,\"comment\":\"\",\"material\":null,\"isStop\":false}]")));
        Assert(preview.GetProperty("changedPropertyCount").GetInt32()==3 &&
            preview.GetProperty("changes")[0].GetProperty("before").GetString()=="Infinity" &&
            preview.GetProperty("warnings").GetArrayLength()==1);
        var noChange=JsonSerializer.SerializeToElement(EngineeringEditPreview.Build(state,
            JsonSerializer.Deserialize<JsonElement>("[{\"surfaceNumber\":1,\"thickness\":3,\"comment\":null}]")));
        Assert(!noChange.GetProperty("containsRequestedChanges").GetBoolean());
        foreach(var invalid in new[]{
            "[{\"surfaceNumber\":2,\"radius\":4}]",
            "[{\"surfaceNumber\":1,\"radius\":4},{\"surfaceNumber\":1,\"thickness\":5}]",
            "[{\"surfaceNumber\":1,\"unknownProperty\":4}]"})
            Reject(()=>EngineeringEditPreview.Build(state,JsonSerializer.Deserialize<JsonElement>(invalid)));
        Console.WriteLine("Engineering preview: native infinity, omission/clear/false semantics, no-op edits, stop warnings, target validation and explicit verification states passed.");
    }
    private static void Assert(bool value) { if(!value) throw new Exception("Engineering workflow assertion failed."); }
    private static void Reject(Action action)
    { try { action(); } catch(ArgumentException) { return; } catch(InvalidOperationException) { return; } throw new Exception("Unsafe workflow evidence was accepted."); }
}
