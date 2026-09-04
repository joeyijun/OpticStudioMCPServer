using ZOSAPI.Analysis.Data;

namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>
/// Generates BMP files from ZOS-API analysis data grids. Standalone mode has
/// no generic image-export method, so this helper renders the first DataGrid.
/// A false return means only that no renderable data grid exists; invalid grid
/// data and filesystem errors are surfaced to the caller.
/// </summary>
internal static class AnalysisBmpHelper
{
    internal static bool TryExportBmp(IAR_ results, string imagePath, CancellationToken cancellationToken = default)
    {
        if (results == null) throw new ArgumentNullException(nameof(results));
        if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentException("imagePath is required.", nameof(imagePath));
        cancellationToken.ThrowIfCancellationRequested();

        if (results.NumberOfDataGrids <= 0)
            return false;

        var grid = results.GetDataGrid(0);
        if (grid == null)
            return false;
        WriteDataGridAsBmp(grid, imagePath, cancellationToken);
        return true;
    }

    private static void WriteDataGridAsBmp(IAR_DataGrid grid, string path, CancellationToken cancellationToken)
    {
        int width = checked((int)grid.Nx);
        int height = checked((int)grid.Ny);
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"Analysis DataGrid returned invalid dimensions {width}x{height}.");

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                double value = grid.Z(x, y);
                ValidateFinite(value, x, y);
                if (value < min) min = value;
                if (value > max) max = value;
            }
        }

        double range = max - min;
        if (double.IsNaN(range) || double.IsInfinity(range))
            throw new InvalidDataException($"Analysis DataGrid has invalid value range [{min}, {max}].");
        bool constantGrid = range == 0;

        int rowBytes = checked((checked(width * 3) + 3) & ~3);
        int pixelDataSize = checked(rowBytes * height);
        int fileSize = checked(54 + pixelDataSize);

        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write(0);
        bw.Write(54);
        bw.Write(40);
        bw.Write(width);
        bw.Write(height);
        bw.Write((short)1);
        bw.Write((short)24);
        bw.Write(0);
        bw.Write(pixelDataSize);
        bw.Write(3780);
        bw.Write(3780);
        bw.Write(0);
        bw.Write(0);

        byte[] row = new byte[rowBytes];
        for (int y = height - 1; y >= 0; y--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(row, 0, row.Length);
            for (int x = 0; x < width; x++)
            {
                double value = grid.Z(x, y);
                ValidateFinite(value, x, y);
                double normalized = constantGrid ? 0.0 : (value - min) / range;
                HotColormap(normalized, out byte r, out byte g, out byte b);
                int offset = x * 3;
                row[offset] = b;
                row[offset + 1] = g;
                row[offset + 2] = r;
            }
            bw.Write(row);
        }
        bw.Flush();
        fs.Flush(true);
    }

    private static void ValidateFinite(double value, int x, int y)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidDataException($"Analysis DataGrid contains non-finite value {value} at ({x},{y}).");
    }

    private static void HotColormap(double t, out byte r, out byte g, out byte b)
    {
        if (double.IsNaN(t) || double.IsInfinity(t))
            throw new InvalidDataException($"Cannot map non-finite normalized DataGrid value {t} to a BMP pixel.");
        if (t < 0.0) t = 0.0;
        else if (t > 1.0) t = 1.0;

        if (t < 1.0 / 3.0)
        {
            double s = t * 3.0;
            r = (byte)(s * 255);
            g = 0;
            b = 0;
        }
        else if (t < 2.0 / 3.0)
        {
            double s = (t - 1.0 / 3.0) * 3.0;
            r = 255;
            g = (byte)(s * 255);
            b = 0;
        }
        else
        {
            double s = (t - 2.0 / 3.0) * 3.0;
            r = 255;
            g = 255;
            b = (byte)(s * 255);
        }
    }
}
