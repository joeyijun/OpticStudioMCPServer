using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ZemaxMCP.Server.Tools.NonSequential;

/// <summary>Bounded atomic CSV writer for NSC native pixel values, with
/// failures/cancellation incapable of publishing an incomplete final file.</summary>
public static class NscDetectorCsvExport
{
    public const int MaximumPixels = 262144;
    public sealed record Summary(string FilePath, int PixelCount, int StartRow, int StartColumn,
        int Rows, int Columns, double Minimum, double Maximum, double Sum, string Sha256);

    internal static Summary Write(string outputPath, bool overwrite, int detectorRows,
        int detectorColumns, int startRow, int startColumn, int rows, int columns,
        Func<int,int,double> readNativePixel, CancellationToken cancellationToken)
    {
        if (detectorRows <= 0 || detectorColumns <= 0 ||
            (long)detectorRows * detectorColumns > int.MaxValue ||
            rows <= 0 || columns <= 0 || (long)rows * columns > MaximumPixels ||
            startRow < 0 || startColumn < 0 ||
            (long)startRow + rows > detectorRows ||
            (long)startColumn + columns > detectorColumns)
            throw new ArgumentException("CSV export requires explicit in-range ROI of 1..262144 pixels and a detector with supported native 1-based pixel indices.");
        if (string.IsNullOrWhiteSpace(outputPath) ||
            outputPath.IndexOf('\0') >= 0)
            throw new ArgumentException("An output CSV path is required.");
        var target = Path.GetFullPath(outputPath.Trim());
        if (!string.Equals(Path.GetExtension(target), ".csv", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("NSC export destination must end in .csv.");
        var folder = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            throw new DirectoryNotFoundException("CSV export directory must already exist.");
        if (!overwrite && File.Exists(target))
            throw new IOException("CSV output already exists; set overwrite=true to replace it.");
        var temp=Path.Combine(folder, "."+Path.GetFileNameWithoutExtension(target)+
            "."+Guid.NewGuid().ToString("N")+".tmp.csv");
        var minimum=double.PositiveInfinity;var maximum=double.NegativeInfinity;var sum=0d;
        try
        {
            using(var writer=new StreamWriter(
                new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None),
                new UTF8Encoding(false)))
            {
                writer.WriteLine("row,column,value");
                for(var y=0;y<rows;y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for(var x=0;x<columns;x++)
                    {
                        if((x&63)==0) cancellationToken.ThrowIfCancellationRequested();
                        var absoluteRow=startRow+y;var absoluteColumn=startColumn+x;
                        var value=readNativePixel(absoluteRow,absoluteColumn);
                        if(!double.IsFinite(value))
                            throw new InvalidDataException("Detector returned non-finite native pixel data.");
                        minimum=Math.Min(minimum,value);
                        maximum=Math.Max(maximum,value);
                        sum+=value;
                        if(!double.IsFinite(sum))
                            throw new InvalidDataException("Native ROI sum overflow; no partial output was published.");
                        writer.Write(absoluteRow.ToString(CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.Write(absoluteColumn.ToString(CultureInfo.InvariantCulture));
                        writer.Write(',');
                        writer.WriteLine(value.ToString("G17",CultureInfo.InvariantCulture));
                    }
                }
                writer.Flush();
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var file=File.OpenRead(temp);
            var digest=Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            cancellationToken.ThrowIfCancellationRequested();
            if(overwrite && File.Exists(target)) File.Replace(temp,target,null);
            else File.Move(temp,target);
            return new Summary(target,checked(rows*columns),startRow,startColumn,rows,columns,
                minimum,maximum,sum,digest);
        }
        finally
        {
            if(File.Exists(temp)) File.Delete(temp);
        }
    }
}
