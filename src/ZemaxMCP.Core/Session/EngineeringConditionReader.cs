using ZOSAPI;

namespace ZemaxMCP.Core.Session;

// The only vendor-bound part of provenance; always called inside the STA callback.
public static class EngineeringConditionReader
{
    public static EngineeringObservation.Conditions Read(IOpticalSystem system, string modelId)
    {
        var data = system.SystemData; var sequential = system.Mode == SystemType.Sequential;
        var fields = new List<EngineeringObservation.FieldCondition>(); var count = sequential ? data.Fields.NumberOfFields : 0;
        for (var i = 1; i <= Math.Min(count, 32); i++)
        {
            var f = data.Fields.GetField(i);
            fields.Add(new(i, Finite(f.X), Finite(f.Y), Finite(f.Weight), f.IsActive,
                Finite(f.VDX), Finite(f.VDY), Finite(f.VCX), Finite(f.VCY), Finite(f.TAN)));
        }
        var waves = new List<EngineeringObservation.WavelengthCondition>(); var waveCount = data.Wavelengths.NumberOfWavelengths;
        for (var i = 1; i <= Math.Min(waveCount, 32); i++) { var w = data.Wavelengths.GetWavelength(i); waves.Add(new(i, Finite(w.Wavelength), Finite(w.Weight), w.IsPrimary, w.IsActive)); }
        return new(modelId, system.SystemFile, system.Mode.ToString(), data.Units.LensUnits.ToString(),
            system.MCE.CurrentConfiguration, system.MCE.NumberOfConfigurations, sequential ? data.Fields.GetFieldType().ToString() : null,
            data.Aperture.ApertureType.ToString(), Finite(data.Aperture.ApertureValue), fields, waves,
            Math.Max(0, count - fields.Count), Math.Max(0, waveCount - waves.Count),
            "Recorded active configuration/units/fields/wavelengths/aperture only; not a complete optical fingerprint. Sampling is requested sampling; unrecorded effective native settings remain unknown.",
            data.Units.MTFUnits.ToString(), sequential ? data.Fields.Normalization.ToString() : null);
    }
    private static double? Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : v;
}
