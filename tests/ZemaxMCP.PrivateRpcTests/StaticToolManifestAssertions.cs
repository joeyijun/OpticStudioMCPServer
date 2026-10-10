using System.Runtime.CompilerServices;
using System.Text.Json;
using ZemaxMCP.ToolManifest;

namespace ZemaxMCP.PrivateRpcTests;

internal static class StaticToolManifestAssertions
{
    [ModuleInitializer]
    internal static void VerifyStaticToolManifestContract()
    {
        if (StaticToolManifest.All.Count != 145)
            throw new InvalidOperationException("Static Host tool manifest must contain all 145 Worker commands.");
        if (StaticToolManifest.ContractFingerprint.Length != 64 ||
            StaticToolManifest.ContractFingerprint.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("Static tool contract fingerprint must be a SHA-256 hex digest.");

        var openFileEntry = StaticToolManifest.GetRequired("zemax_open_file");
        if (openFileEntry.DomainId != "files" || openFileEntry.Impact != "Caution")
            throw new InvalidOperationException("Static manifest must carry explicit domain and impact metadata.");
        var openFile = openFileEntry.InputSchema;
        var openFileRequired = openFile.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        if (!openFileRequired.SetEquals(new[] { "filePath" }) ||
            openFile.GetProperty("properties").GetProperty("filePath").GetProperty("type").GetString() != "string")
            throw new InvalidOperationException("zemax_open_file must advertise filePath as a required string.");

        var statusEntry = StaticToolManifest.GetRequired("zemax_status");
        if (statusEntry.DomainId != "administration" || statusEntry.Impact != "ReadOnly" ||
            !StaticToolManifest.IsAllowed("full-expert", statusEntry.Name, readOnly: true))
            throw new InvalidOperationException("Read-only policy metadata must be available directly from the static manifest.");
        if (!StaticToolManifest.IsAllowed("full-expert", openFileEntry.Name, readOnly: true))
            throw new InvalidOperationException("Global read-only mode must preserve Caution session operations.");
        if (StaticToolManifest.IsAllowed("full-expert", "zemax_set_surface", readOnly: true))
            throw new InvalidOperationException("Global read-only mode must reject HighImpact tools.");
        if (StaticToolManifest.IsAllowed("basic-viewing", openFileEntry.Name, readOnly: false))
            throw new InvalidOperationException("The basic-viewing profile must remain stricter than the global read-only switch.");

        var expectedProfileCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["basic-viewing"] = 41,
            ["sequential-design"] = 87,
            ["nonsequential-stray-light"] = 30,
            ["optimization-tolerance"] = 73,
            ["full-expert"] = 145
        };
        foreach (var pair in expectedProfileCounts)
        {
            var actual = StaticToolManifest.All.Count(tool => StaticToolManifest.IsAllowed(pair.Key, tool.Name, readOnly: false));
            if (actual != pair.Value)
                throw new InvalidOperationException($"Tool profile {pair.Key} exposes {actual} tools; expected {pair.Value}. Profiles must remain task-sized and explicitly reviewed.");
        }
        if (StaticToolManifest.IsAllowed("nonsequential-stray-light", "zemax_set_surface", readOnly: false) ||
            StaticToolManifest.IsAllowed("optimization-tolerance", "zemax_get_nsc_objects", readOnly: false))
            throw new InvalidOperationException("Focused profiles leaked unrelated editor domains back into tools/list.");

        var budget = StaticToolManifest.GetRequired("zemax_nsc_energy_budget");
        if (budget.DomainId != "non-sequential" || budget.Impact != "ReadOnly" ||
            !StaticToolManifest.IsAllowed("nonsequential-stray-light", budget.Name, readOnly: true) ||
            StaticToolManifest.IsAllowed("basic-viewing", budget.Name, readOnly: false))
            throw new InvalidOperationException("NSC energy budget must be non-mutating and available only in appropriate profiles.");
        var budgetSchema = budget.InputSchema;
        if (budgetSchema.GetProperty("properties").GetProperty("detectorObjects").GetProperty("type").GetString() != "array" ||
            !budgetSchema.GetProperty("required").EnumerateArray().Any(x => x.GetString() == "detectorObjects"))
            throw new InvalidOperationException("NSC energy budgeting must require an array of detector IDs.");

        var csvExport = StaticToolManifest.GetRequired("zemax_export_nsc_detector_csv");
        var csvExportProperties = csvExport.InputSchema.GetProperty("properties");
        if (csvExport.DomainId != "files" || csvExport.Impact != "HighImpact" ||
            !StaticToolManifest.IsAllowed("nonsequential-stray-light",csvExport.Name,false) ||
            StaticToolManifest.IsAllowed("nonsequential-stray-light",csvExport.Name,true) ||
            StaticToolManifest.IsAllowed("basic-viewing",csvExport.Name,false) ||
            !csvExportProperties.TryGetProperty("csvPath",out _) ||
            !csvExportProperties.TryGetProperty("rowCount",out _) ||
            !csvExportProperties.TryGetProperty("columnCount",out _) ||
            !csvExportProperties.TryGetProperty("overwrite",out _))
            throw new InvalidOperationException("Filesystem-writing NSC CSV export must be privileged and explicitly bounded.");

        var detectorSchema = StaticToolManifest.GetRequired("zemax_get_nsc_detector").InputSchema.GetProperty("properties");
        foreach (var field in new[] { "includePixels", "dataType", "startRow", "startColumn", "rowCount", "columnCount" })
            if (!detectorSchema.TryGetProperty(field, out _))
                throw new InvalidOperationException("Detector ROI tool is missing the published field " + field);

        var coating = StaticToolManifest.GetRequired("zemax_coating_rta");
        var coatingFields = coating.InputSchema.GetProperty("properties");
        if (coating.DomainId != "analysis" || coating.Impact != "ReadOnly" ||
            !StaticToolManifest.IsAllowed("sequential-design",coating.Name,true) ||
            StaticToolManifest.IsAllowed("nonsequential-stray-light",coating.Name,false) ||
            !coatingFields.TryGetProperty("surfaces",out _) ||
            !coatingFields.TryGetProperty("anglesDegrees",out _) ||
            !coatingFields.TryGetProperty("wavelengths",out _) ||
            !coatingFields.TryGetProperty("direction",out _))
            throw new InvalidOperationException("Native coating RTA must be a bounded read-only sequential analysis.");

        var footprintInputs = StaticToolManifest.GetRequired("zemax_ray_footprint").InputSchema.GetProperty("properties");
        if (!footprintInputs.TryGetProperty("globalMechanicalMode",out _) ||
            !footprintInputs.TryGetProperty("mechanicalGlobalPolygon",out _) ||
            !footprintInputs.TryGetProperty("mechanicalPlaneTolerance",out _) ||
            !footprintInputs.TryGetProperty("includeGlobalCoordinates",out _) ||
            !footprintInputs.TryGetProperty("mechanicalRectangle", out _) ||
            !footprintInputs.TryGetProperty("mechanicalPolygon", out _) ||
            !footprintInputs.TryGetProperty("mechanicalSurface", out _))
            throw new InvalidOperationException("Footprint tool must advertise explicit local mechanical boundary controls.");
        var detectorInputs = StaticToolManifest.GetRequired("zemax_get_nsc_detector").InputSchema.GetProperty("properties");
        if (!detectorInputs.TryGetProperty("includeTilePlan", out _) ||
            !detectorInputs.TryGetProperty("tilePlanPage", out _) ||
            !detectorInputs.TryGetProperty("heatmapBins", out _))
            throw new InvalidOperationException("Detector tool must advertise paged native tile/mean-map controls.");
        if (!StaticToolManifest.GetRequired("zemax_system_summary").InputSchema.GetProperty("properties").TryGetProperty("baselineSummaryJson", out _))
            throw new InvalidOperationException("System summary must expose bounded prior-design comparison.");

        var energyInputs=StaticToolManifest.GetRequired("zemax_energy_budget").InputSchema.GetProperty("properties");
        if (!energyInputs.TryGetProperty("relativeSourceSpectralWeights", out _) ||
            !energyInputs.TryGetProperty("relativeDetectorSpectralResponse",out _))
            throw new InvalidOperationException("Sequential energy budgets need separately supplied source and detector spectral inputs.");
        if (!StaticToolManifest.GetRequired("zemax_energy_budget").InputSchema.GetProperty("properties").TryGetProperty("startSurface", out _))
            throw new InvalidOperationException("Sequential energy budgeting must advertise bounded startSurface for segmented LDEs.");

        foreach (var pair in new[] {
            (Name: "zemax_energy_budget", Domain: "analysis",
                RequiredField: "gridSize"),
            (Name: "zemax_ray_footprint", Domain: "analysis",
                RequiredField: "maxPointsPerSurface")
        })
        {
            var entry = StaticToolManifest.GetRequired(pair.Name);
            if (entry.DomainId != pair.Domain || entry.Impact != "ReadOnly" ||
                !StaticToolManifest.IsAllowed("sequential-design", pair.Name, readOnly: true) ||
                StaticToolManifest.IsAllowed("nonsequential-stray-light", pair.Name, readOnly: false) ||
                !entry.InputSchema.GetProperty("properties").TryGetProperty(pair.RequiredField, out _))
                throw new InvalidOperationException("Engineering analysis tool schema/permission contract regressed: " + pair.Name);
        }

        var clipping = StaticToolManifest.GetRequired("zemax_diagnose_clipping");
        if (clipping.DomainId != "analysis" || clipping.Impact != "ReadOnly" ||
            !StaticToolManifest.IsAllowed("basic-viewing", clipping.Name, readOnly: true) ||
            !StaticToolManifest.IsAllowed("sequential-design", clipping.Name, readOnly: true) ||
            !StaticToolManifest.IsAllowed("optimization-tolerance", clipping.Name, readOnly: true) ||
            StaticToolManifest.IsAllowed("nonsequential-stray-light", clipping.Name, readOnly: false))
            throw new InvalidOperationException("One-call clipping diagnosis must preserve ReadOnly semantics and sequential-only profile exposure.");
        var clippingSchema = clipping.InputSchema.GetProperty("properties");
        foreach (var field in new[] { "surfaces", "hx", "hy", "wavelength", "gridSize", "maxPointsPerSurface" })
            if (!clippingSchema.TryGetProperty(field, out _))
                throw new InvalidOperationException("Clipping diagnosis is missing bounded sampling input: " + field);

        var summary = StaticToolManifest.GetRequired("zemax_system_summary");
        if (summary.DomainId != "system" || summary.Impact != "ReadOnly" ||
            !StaticToolManifest.IsAllowed("basic-viewing", summary.Name, readOnly: true) ||
            !StaticToolManifest.IsAllowed("sequential-design", summary.Name, readOnly: true) ||
            !StaticToolManifest.IsAllowed("nonsequential-stray-light", summary.Name, readOnly: true) ||
            !StaticToolManifest.IsAllowed("optimization-tolerance", summary.Name, readOnly: true))
            throw new InvalidOperationException("Bounded system summary must be read-only and present in every optical profile.");
        var summaryProperties = summary.InputSchema.GetProperty("properties");
        foreach (var pair in new[] { ("maxSurfaces", 12), ("maxFields", 8), ("maxWavelengths", 8) })
            if (!summaryProperties.TryGetProperty(pair.Item1, out var setting) ||
                setting.GetProperty("default").GetInt32() != pair.Item2)
                throw new InvalidOperationException("Bounded model summary lost its required safe default: " + pair.Item1);

        foreach (var aiName in new[] { "zemax_validate_model","zemax_explain_result" })
        {
            var ai = StaticToolManifest.GetRequired(aiName);
            if (ai.DomainId != "system" || ai.Impact != "ReadOnly" ||
                !StaticToolManifest.IsAllowed("basic-viewing",aiName,true) ||
                !StaticToolManifest.IsAllowed("nonsequential-stray-light",aiName,true) ||
                !StaticToolManifest.IsAllowed("optimization-tolerance",aiName,true))
                throw new InvalidOperationException("AI explain/validation tools must remain read-only in all profiles.");
        }
        if (!StaticToolManifest.GetRequired("zemax_explain_result").InputSchema.GetProperty("properties").TryGetProperty("resultJson",out _))
            throw new InvalidOperationException("AI explanation must require an explicit user-provided result payload.");
        if (!StaticToolManifest.GetRequired("zemax_validate_model").InputSchema.GetProperty("properties").TryGetProperty("purpose",out _))
            throw new InvalidOperationException("AI preflight must expose purpose-specific checks.");

        var aiPlanner = StaticToolManifest.GetRequired("zemax_task_plan");
        if (aiPlanner.DomainId != "system" || aiPlanner.Impact != "ReadOnly" ||
            !StaticToolManifest.IsAllowed("basic-viewing", aiPlanner.Name, readOnly: true) ||
            !StaticToolManifest.IsAllowed("nonsequential-stray-light", aiPlanner.Name, readOnly: true) ||
            !StaticToolManifest.IsAllowed("optimization-tolerance", aiPlanner.Name, readOnly: true))
            throw new InvalidOperationException("Engineering task planner must remain read-only and discoverable in all focused profiles.");
        if (!aiPlanner.InputSchema.GetProperty("required").EnumerateArray()
            .Any(field => field.GetString() == "task"))
            throw new InvalidOperationException("Engineering task planner must require an explicit task intent.");

        var snapshotList = StaticToolManifest.GetRequired("zemax_snapshot_list").InputSchema.GetProperty("properties");
        if (snapshotList.GetProperty("limit").GetProperty("default").GetInt32() != 25)
            throw new InvalidOperationException("Snapshot listing must preserve its bounded newest-first default.");

        var snapshotDiff = StaticToolManifest.GetRequired("zemax_snapshot_diff").InputSchema;
        var snapshotDiffRequired = snapshotDiff.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        if (!snapshotDiffRequired.SetEquals(new[] { "snapshotFileName" }) ||
            snapshotDiff.GetProperty("properties").GetProperty("maxDifferences").GetProperty("default").GetInt32() != 50 ||
            snapshotDiff.GetProperty("properties").GetProperty("maxSurfaces").GetProperty("default").GetInt32() != 500)
            throw new InvalidOperationException("Snapshot diff must require only a snapshot file name and preserve bounded output defaults.");

        var snapshotRestore = StaticToolManifest.GetRequired("zemax_snapshot_restore").InputSchema;
        var snapshotRestoreRequired = snapshotRestore.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        if (!snapshotRestoreRequired.SetEquals(new[] { "snapshotFileName" }))
            throw new InvalidOperationException("Snapshot restore must accept only the snapshot file name as its required public argument.");

        var tolerancing = StaticToolManifest.GetRequired("zemax_run_tolerancing").InputSchema.GetProperty("properties");
        if (tolerancing.GetProperty("criterion").GetProperty("default").GetString() != "RMSSpotRadius" ||
            tolerancing.GetProperty("monteCarloRuns").GetProperty("default").GetInt32() != 20 ||
            tolerancing.GetProperty("timeoutSeconds").GetProperty("default").GetDouble() != 300 ||
            tolerancing.GetProperty("runInBackground").GetProperty("default").GetBoolean() != true)
            throw new InvalidOperationException("Structured tolerancing must preserve bounded criterion, Monte Carlo, timeout, and background defaults.");

        var nscTrace = StaticToolManifest.GetRequired("zemax_run_nsc_ray_trace").InputSchema.GetProperty("properties");
        if (nscTrace.GetProperty("clearDetectors").GetProperty("default").GetBoolean() != true ||
            nscTrace.GetProperty("timeoutSeconds").GetProperty("default").GetDouble() != 60 ||
            nscTrace.GetProperty("runInBackground").GetProperty("default").GetBoolean() != true)
            throw new InvalidOperationException("Managed NSC ray trace must preserve safe detector clearing, timeout, and background defaults.");

        var batchSurfaces = StaticToolManifest.GetRequired("zemax_batch_set_surfaces").InputSchema;
        var batchRequired = batchSurfaces.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        if (!batchRequired.SetEquals(new[] { "edits" }))
            throw new InvalidOperationException("zemax_batch_set_surfaces must require only the edits array.");
        var batchItems = batchSurfaces.GetProperty("properties").GetProperty("edits").GetProperty("items");
        var batchItemRequired = batchItems.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        if (!batchItemRequired.SetEquals(new[] { "surfaceNumber" }) ||
            !batchItems.GetProperty("properties").TryGetProperty("thickness", out _) ||
            !batchItems.GetProperty("properties").TryGetProperty("isStop", out _))
            throw new InvalidOperationException("Batch surface-edit schema must preserve required surfaceNumber and nullable edit fields.");

        var rayDiagnostics = StaticToolManifest.GetRequired("zemax_ray_trace_diagnostics").InputSchema.GetProperty("properties");
        if (rayDiagnostics.GetProperty("fieldSampling").GetProperty("default").GetInt32() != 5 ||
            rayDiagnostics.GetProperty("pupilSampling").GetProperty("default").GetInt32() != 5 ||
            rayDiagnostics.GetProperty("maxFailures").GetProperty("default").GetInt32() != 50)
            throw new InvalidOperationException("Ray-trace diagnostics must preserve bounded sampling defaults.");

        var setFields = StaticToolManifest.GetRequired("zemax_set_fields").InputSchema;
        var setFieldsRequired = setFields.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        if (!setFieldsRequired.SetEquals(new[] { "fields" }))
            throw new InvalidOperationException("zemax_set_fields must advertise fields as required and fieldType as optional.");
        var fields = setFields.GetProperty("properties").GetProperty("fields");
        if (fields.GetProperty("type").GetString() != "array")
            throw new InvalidOperationException("zemax_set_fields.fields must be an array.");
        var item = fields.GetProperty("items");
        var itemRequired = item.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        var itemProperties = item.GetProperty("properties");
        if (!itemRequired.SetEquals(new[] { "x", "y" }) ||
            !itemProperties.TryGetProperty("weight", out var weight) ||
            Math.Abs(weight.GetProperty("default").GetDouble() - 1.0) > double.Epsilon)
            throw new InvalidOperationException("Nested field schemas must preserve camelCase names, required values, and defaults.");

        var optimize = StaticToolManifest.GetRequired("zemax_optimize").InputSchema.GetProperty("properties");
        if (optimize.GetProperty("algorithm").GetProperty("default").GetString() != "DLS" ||
            optimize.GetProperty("cycles").GetProperty("default").GetInt32() != 0)
            throw new InvalidOperationException("Static manifest must preserve tool parameter defaults.");

        if (StaticToolManifest.All.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != StaticToolManifest.All.Count)
            throw new InvalidOperationException("Static Host tool manifest contains duplicate tool names.");

        var opaque = new List<string>();
        foreach (var tool in StaticToolManifest.All)
            FindOpaqueObjects(tool.InputSchema, tool.Name + ".input", opaque);
        if (opaque.Count > 0)
            throw new InvalidOperationException("Generated tool schemas contain unresolved opaque object contracts: " + string.Join(", ", opaque.Take(20)));
    }

    private static void FindOpaqueObjects(JsonElement schema, string path, List<string> opaque)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("type", out var type) && type.GetString() == "object")
        {
            var hasProperties = schema.TryGetProperty("properties", out var properties);
            var hasAdditional = schema.TryGetProperty("additionalProperties", out _);
            if (!hasProperties && !hasAdditional) opaque.Add(path);
            if (hasProperties)
                foreach (var property in properties.EnumerateObject())
                    FindOpaqueObjects(property.Value, path + "." + property.Name, opaque);
            if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object)
                FindOpaqueObjects(additional, path + ".additionalProperties", opaque);
        }
        if (schema.TryGetProperty("items", out var items)) FindOpaqueObjects(items, path + "[]", opaque);
    }
}
