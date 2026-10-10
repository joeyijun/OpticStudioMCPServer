using System.ComponentModel;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;
using ZOSAPI;
using ZOSAPI.Editors.LDE;

namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>Official single-surface coating performance readback; not a
/// full-system polarized throughput or real-ray angle distribution.</summary>
[ZemaxToolType]
public sealed class GetCoatingRtaTool
{
    private readonly IZemaxSession _session;
    public GetCoatingRtaTool(IZemaxSession session) => _session=session;
    public sealed record Sample(int Surface,string? Coating,int WavelengthNumber,
        double WavelengthMicrometers,double AngleDegrees,string Direction,
        CoatingPerformanceReflection.Coefficients Rta);
    public sealed record Result(bool Success,string? Error,
        IReadOnlyList<Sample> Samples,string Interpretation,
        string Source="OpticStudio LDE GetCoatingPerformanceData (runtime readback)");

    [ZemaxTool(Name="zemax_coating_rta")]
    [Description("Read ACTUAL OpticStudio sequential coating R/T/A at explicit incidence angles, configured wavelengths and travel direction using the official LDE coating-performance API, with S/P and 50:50 unpolarized estimates. Bounded to 120 samples, no file/model edits. A single-surface coating calculation is NOT a full ray/path energy budget. Unsupported old ZOS-API versions fail explicitly.")]
    public async Task<Result> ExecuteAsync(
        [Description("1-based selected sequential LDE surfaces; 1..24 unique existing indices, no object surface.")] int[] surfaces,
        [Description("Explicit incidence angles in DEGREES, from 0 to 89.9 inclusive, 1..6 unique values. Default [0].")] double[]? anglesDegrees=null,
        [Description("Selected configured 1-based wavelength indices, up to 6 unique; empty uses primary wavelength. Wavelength is passed to ZOS-API in micrometers.")] int[]? wavelengths=null,
        [Description("Native ray travel direction: inward or outward.")] string direction="inward",
        CancellationToken cancellationToken=default)
    {
        const string scope="Native coating performance at explicitly specified AOI (degrees), wavelength (micrometers) and inward/outward travel. Reflection/Transmission/Absorption each contain intensity S/P and arithmetic 50:50 average. Residual = 1-R-T-A is reported, never replaced or clamped; check out-of-range and nonclosure. Does not include upstream bulk absorption, ray angle distributions, multiple bounces, vignetting, polarization transport or physical source-to-detector power. Old ZOS-API versions without the documented method explicitly fail.";
        var angles=anglesDegrees is {Length:>0}?anglesDegrees:new[]{0d};
        if(surfaces is not {Length:>=1 and <=24} ||
            surfaces.Any(x=>x<=0) || surfaces.Distinct().Count()!=surfaces.Length ||
            angles.Length is <1 or >6 || angles.Any(x=>!double.IsFinite(x)||x<0||x>89.9) ||
            angles.Distinct().Count()!=angles.Length ||
            direction is not ("inward" or "outward"))
            return new Result(false,"Invalid surface IDs, angle range, or direction.",Array.Empty<Sample>(),scope);
        try
        {
            return await _session.ExecuteAsync("GetCoatingRta",
                new Dictionary<string,object?>{
                    ["surfaces"]=surfaces,["anglesDegrees"]=angles,
                    ["wavelengths"]=wavelengths,["direction"]=direction
                },system=>{
                    cancellationToken.ThrowIfCancellationRequested();
                    if(system.Mode!=SystemType.Sequential)
                        throw new InvalidOperationException("Coating R/T/A requires sequential LDE mode.");
                    var selected=wavelengths is {Length:>0}?wavelengths:
                        Enumerable.Range(1,system.SystemData.Wavelengths.NumberOfWavelengths)
                            .Where(w=>system.SystemData.Wavelengths.GetWavelength(w).IsPrimary)
                            .DefaultIfEmpty(1).Take(1).ToArray();
                    if(selected.Length is <1 or >6 ||
                       selected.Any(w=>w<1||w>system.SystemData.Wavelengths.NumberOfWavelengths) ||
                       selected.Distinct().Count()!=selected.Length ||
                       (long)selected.Length*surfaces.Length*angles.Length>120)
                        throw new ArgumentException("Choose at most 120 unique surface/angle/wavelength samples.");
                    var output=new List<Sample>();
                    foreach(var surface in surfaces)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if(surface>=system.LDE.NumberOfSurfaces)
                            throw new ArgumentOutOfRangeException(nameof(surfaces),
                                "Surface index exceeds the active LDE image surface.");
                        ILDERow row=system.LDE.GetSurfaceAt(surface);
                        var coating=row.CoatingData?.Coating;
                        foreach(var wave in selected)
                        foreach(var aoi in angles)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var micrometers=system.SystemData.Wavelengths.GetWavelength(wave).Wavelength;
                            var rta=CoatingPerformanceReflection.Read(row,typeof(ILDERow),
                                aoi,micrometers,direction);
                            output.Add(new Sample(surface,coating,wave,micrometers,aoi,direction,rta));
                        }
                    }
                    return new Result(true,null,output,scope);
                },cancellationToken).ConfigureAwait(false);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex)
        {
            return new Result(false,ex is System.Reflection.TargetInvocationException {InnerException:not null} error
                ? error.InnerException!.Message:ex.Message,Array.Empty<Sample>(),scope);
        }
    }
}
