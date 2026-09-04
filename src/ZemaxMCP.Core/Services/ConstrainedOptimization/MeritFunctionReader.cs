using ZemaxMCP.Core.Models;
using ZOSAPI;
using ZOSAPI.Editors.MFE;

namespace ZemaxMCP.Core.Services.ConstrainedOptimization;

public class MeritFunctionReader
{
    public List<MeritRow> ReadMeritRows(IOpticalSystem system, CancellationToken cancellationToken = default)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        var rows = new List<MeritRow>();
        IMeritFunctionEditor mfe = system.MFE ?? throw new InvalidOperationException("Merit Function Editor is not available.");
        int numRows = mfe.NumberOfOperands;

        for (int i = 1; i <= numRows; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IMFERow row = mfe.GetOperandAt(i)
                ?? throw new InvalidOperationException($"OpticStudio returned no MFE operand for row {i}.");
            if (!row.IsValidRow)
                throw new InvalidDataException($"MFE row {i} is not a valid operand row.");

            double weight = row.Weight;
            if (double.IsNaN(weight) || double.IsInfinity(weight) || weight < 0)
                throw new InvalidDataException($"MFE row {i} ({row.Type}) has invalid weight {weight}.");
            if (weight == 0)
                continue;

            double value = row.Value;
            double target = row.Target;
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidDataException($"Weighted MFE row {i} ({row.Type}) has non-finite value {value}.");
            if (double.IsNaN(target) || double.IsInfinity(target))
                throw new InvalidDataException($"Weighted MFE row {i} ({row.Type}) has non-finite target {target}.");

            rows.Add(new MeritRow
            {
                RowNumber = i,
                TypeName = row.Type.ToString(),
                Target = target,
                Value = value,
                Weight = weight
            });
        }

        return rows;
    }
}
