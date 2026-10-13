namespace ZemaxMCP.Core.Session;

/// <summary>Invocation-local provenance, captured on the same STA as the calculation.</summary>
public sealed class EngineeringObservation : IDisposable
{
    private static readonly AsyncLocal<EngineeringObservation?> CurrentSlot = new();
    private readonly EngineeringObservation? _previous;
    private readonly List<OperationEvidence> _operations = new();
    public static EngineeringObservation? Current => CurrentSlot.Value;
    public bool Truncated { get; private set; }
    public EngineeringObservation() { _previous = CurrentSlot.Value; CurrentSlot.Value = this; }
    public IReadOnlyList<OperationEvidence> Operations { get { lock (_operations) return _operations.ToArray(); } }
    public void Observe(Func<Conditions> readConditions, string command, IDictionary<string, object?>? parameters)
    {
        OperationEvidence evidence;
        try
        {
            evidence = new(command, DateTimeOffset.UtcNow, "known", readConditions(),
            parameters?.Take(32).ToDictionary(p => p.Key, p => p.Value?.ToString()), null);
        }
        catch (Exception ex) { evidence = new(command, DateTimeOffset.UtcNow, "unavailable", null, null, ex.Message); }
        lock (_operations) { if (_operations.Count < 16) _operations.Add(evidence); else Truncated = true; if (parameters?.Count > 32) Truncated = true; }
    }
    public void Dispose() => CurrentSlot.Value = _previous;

    public sealed record OperationEvidence(string Command, DateTimeOffset CapturedAt, string Status,
        Conditions? Conditions, IReadOnlyDictionary<string, string?>? RequestedParameters, string? Error);
    public sealed record FieldCondition(int Index, double? X, double? Y, double? Weight, bool Active,
        double? Vdx = null, double? Vdy = null, double? Vcx = null, double? Vcy = null, double? Tan = null);
    public sealed record WavelengthCondition(int Index, double? Micrometers, double? Weight, bool Primary, bool Active);
    public sealed record Conditions(string ModelIdentity, string? FilePath, string Mode, string LensUnit,
        int Configuration, int ConfigurationCount, string? FieldType, string ApertureType, double? ApertureValue,
        IReadOnlyList<FieldCondition> Fields, IReadOnlyList<WavelengthCondition> Wavelengths,
        int OmittedFields, int OmittedWavelengths, string Scope, string? MtfFrequencyUnit = null,
        string? FieldNormalization = null);

}
