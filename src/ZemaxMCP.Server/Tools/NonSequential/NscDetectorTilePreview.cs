namespace ZemaxMCP.Server.Tools.NonSequential;

/// <summary>Bounded, read-only native index helpers: no disk writes, missing
/// pixel interpolation, detector reset, physical-unit conversion or rotation.</summary>
public static class NscDetectorTilePreview
{
    public sealed record Tile(int StartRow, int StartColumn, int RowCount, int ColumnCount);
    public sealed record Plan(long TotalTiles, long TotalPages, int Page,
        int MaximumPixelsPerTile, IReadOnlyList<Tile> Tiles, string Order);

    internal static Plan MakePlan(uint rows, uint columns, int page)
    {
        if(rows==0 || columns==0 || rows>int.MaxValue || columns>int.MaxValue ||
            page<0) throw new ArgumentException("Invalid native NSC dimensions/page.");
        const int side=64, perPage=64;
        var across=((long)columns+side-1)/side;
        var down=((long)rows+side-1)/side;
        var count=checked(across*down);
        var pages=(count+perPage-1)/perPage;
        if(page>=pages) throw new ArgumentOutOfRangeException(nameof(page));
        var start=(long)page*perPage;
        var tiles=new List<Tile>();
        for(long index=start;index<Math.Min(count,start+perPage);index++)
        {
            var y=(int)((index/across)*side);
            var x=(int)((index%across)*side);
            tiles.Add(new Tile(y,x,Math.Min(side,(int)rows-y),Math.Min(side,(int)columns-x)));
        }
        return new Plan(count,pages,page,4096,tiles,
            "native row-major tiles; use startRow/startColumn/rowCount/columnCount on zemax_get_nsc_detector");
    }

    internal static double[][] MeanHeatmap(double[][] pixels,int bins)
    {
        if(bins is < 2 or > 16 || pixels.Length==0 ||
           pixels[0].Length==0 || pixels.Any(r=>r==null ||
                r.Length!=pixels[0].Length || r.Any(v=>!double.IsFinite(v))))
            throw new ArgumentException("Heatmap needs rectangular finite ROI and 2..16 bins.");
        var height=pixels.Length;var width=pixels[0].Length;
        var outRows=Math.Min(height,bins);var outCols=Math.Min(width,bins);
        var map=new double[outRows][];
        for(var y=0;y<outRows;y++)
        {
            map[y]=new double[outCols];
            var y0=y*height/outRows;var y1=(y+1)*height/outRows;
            for(var x=0;x<outCols;x++)
            {
                var x0=x*width/outCols;var x1=(x+1)*width/outCols;
                var sum=0d;
                for(var yy=y0;yy<y1;yy++)
                for(var xx=x0;xx<x1;xx++)
                    sum+=pixels[yy][xx];
                var mean=sum/((y1-y0)*(x1-x0));
                if(!double.IsFinite(mean))
                    throw new InvalidDataException("Heatmap bin mean overflow.");
                map[y][x]=mean;
            }
        }
        return map;
    }
}
