namespace ZemaxMCP.Server.Tools.NonSequential;

/// <summary>
/// Explicit user-verified local detector axis mapping. Never infer polarity,
/// origin orientation or global XYZ from native row-major indexing alone.
/// </summary>
public static class NscPixelCalibration
{
    public sealed record Peak(double LocalX,double LocalY,string Evidence);
    internal static Peak ConvertPeak(int row,int column,int rows,int columns,
        double pixelPitchX,double pixelPitchY,int signColumnX,int signRowY)
    {
        if(rows<=0||columns<=0||row<0||row>=rows||column<0||column>=columns||
           !double.IsFinite(pixelPitchX)||!double.IsFinite(pixelPitchY)||
           pixelPitchX<=0||pixelPitchY<=0||
           signColumnX is not (-1 or 1)||signRowY is not (-1 or 1))
            throw new ArgumentException("Explicit calibrated native detector index transform needs valid rows/columns, pitch and both user-confirmed axis signs.");
        // Pixel-center grid. Sign/origin mapping must be verified on a
        // known physically illuminated landmark and is never auto-asserted.
        var x=(column+0.5-columns/2d)*pixelPitchX*signColumnX;
        var y=(row+0.5-rows/2d)*pixelPitchY*signRowY;
        if(!double.IsFinite(x)||!double.IsFinite(y))
            throw new InvalidDataException("User-declared pixel physical coordinate overflow.");
        return new Peak(x,y,
            "USER-declared pixel center in detector LOCAL X/Y after explicit column/row axis-sign calibration. Sign mapping is not independently verified by ZOS; global XYZ and display orientation require further calibration.");
    }
}
