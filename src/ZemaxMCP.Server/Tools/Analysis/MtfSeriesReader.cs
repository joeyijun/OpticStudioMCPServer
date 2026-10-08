using ZemaxMCP.Core.Models;
using ZemaxMCP.Server.Tools.Base;

namespace ZemaxMCP.Server.Tools.Analysis;

internal static class MtfSeriesReader
{
    internal static MtfFieldData Read(string label, int fieldNumber, double[] x, double[,] y)
    {
        if (x == null || y == null || x.Length == 0 || y.GetLength(0) != x.Length || y.GetLength(1) != 2)
            throw new InvalidDataException("FFT MTF requires a frequency vector and two matching tangential/sagittal columns.");
        var frequencies = new double[x.Length];
        var tangential = new double[x.Length];
        var sagittal = new double[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            frequencies[i] = x[i].Sanitize();
            if (frequencies[i] < 0 || i > 0 && frequencies[i] <= frequencies[i - 1])
                throw new InvalidDataException("FFT MTF frequencies must be nonnegative and strictly increasing.");
            tangential[i] = y[i, 0].Sanitize();
            sagittal[i] = y[i, 1].Sanitize();
        }
        return new MtfFieldData { FieldLabel = label, FieldNumber = fieldNumber,
            Frequencies = frequencies, TangentialMtf = tangential, SagittalMtf = sagittal, DataPoints = x.Length };
    }
}
