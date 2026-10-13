using System.IO;
using ZOSAPI.Editors.MFE;

namespace ZemaxMCP.Server.Tools.Optimization;

internal static class MeritFunctionCompatibility
{
    // MFE retains one mandatory row. Clear its contents, not the editor itself.
    internal static void Clear(IMeritFunctionEditor editor)
    {
        int count = editor.NumberOfOperands;
        if (count > 1)
        {
            int removed = editor.RemoveOperandsAt(2, count - 1);
            if (removed != count - 1 || editor.NumberOfOperands != 1)
                throw new InvalidOperationException("OpticStudio did not remove the requested trailing MFE operands.");
        }
        if (editor.NumberOfOperands == 0) editor.AddOperand();
        var placeholder = editor.GetOperandAt(1)
            ?? throw new InvalidOperationException("MFE did not expose its mandatory placeholder row.");
        if (!placeholder.ChangeType(MeritOperandType.BLNK))
            throw new InvalidOperationException("OpticStudio rejected resetting the MFE placeholder row.");
        if (placeholder.Type != MeritOperandType.BLNK || editor.NumberOfOperands != 1)
            throw new InvalidOperationException("MFE could not be reset to one blank placeholder row.");
    }

    internal static double? OptionalNumber(double value, bool weighted, string label)
    {
        if (!double.IsNaN(value) && !double.IsInfinity(value)) return value;
        if (weighted) throw new InvalidDataException($"OpticStudio returned non-finite {label}: {value}.");
        return null; // undefined is not zero; JSON-safe and explicitly nullable
    }
}
