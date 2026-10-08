namespace ZemaxMCP.Server.Tools.Base;

/// <summary>JSON-safe surface readback. Raw rollback state must remain separate.</summary>
public sealed record SurfaceReadback(
    int SurfaceNumber,
    double Radius,
    double? Thickness,
    string Material,
    double? SemiDiameter,
    double Conic,
    string Comment,
    bool IsStop,
    string ThicknessState,
    string SemiDiameterState)
{
    public static SurfaceReadback FromRaw(int surfaceNumber, double radius, double thickness,
        string material, double semiDiameter, double conic, string comment, bool isStop) => new(
        surfaceNumber,
        radius.SanitizeRadius(),
        thickness.OpticalDimension(),
        material,
        semiDiameter.OpticalDimension(),
        conic.Sanitize(),
        comment,
        isStop,
        thickness.OpticalDimensionState(),
        semiDiameter.OpticalDimensionState());
}
