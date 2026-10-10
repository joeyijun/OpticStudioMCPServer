using System.ComponentModel;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;
using ZOSAPI;
using ZOSAPI.Editors.NCE;

namespace ZemaxMCP.Server.Tools.NonSequential;

/// <summary>Intentionally separate from the read-only detector inspection
/// tool: writing a filesystem artifact is HighImpact and opt-in.</summary>
[ZemaxToolType]
public sealed class ExportNscDetectorCsvTool
{
    private readonly IZemaxSession _session;
    public ExportNscDetectorCsvTool(IZemaxSession session) => _session=session;

    public sealed record Result(bool Success,string? Error,int ObjectNumber,
        string? ObjectType,int DataType,NscDetectorCsvExport.Summary? Export,
        string Interpretation);

    [ZemaxTool(Name="zemax_export_nsc_detector_csv")]
    [Description("Export an explicit bounded NSC detector ROI (up to 262144 pixels) to an atomic native-row-major CSV on the OPTICSTUDIO COMPUTER. This is a local FILE WRITE, not a read-only tool. No new ray trace and no detector clearing. Existing files are kept unless overwrite=true. DataType 1 may mean absorbed flux for volumes rather than irradiance.")]
    public async Task<Result> ExecuteAsync(
        [Description("1-indexed NSC detector object number.")] int objectNumber,
        [Description("Absolute local .csv output filename on the OpticStudio computer; parent folder must already exist.")] string csvPath,
        [Description("Native 0-based ROI start row.")] int startRow,
        [Description("Native 0-based ROI start column.")] int startColumn,
        [Description("Explicit positive ROI height; limited together with width to 262144 pixels.")] int rowCount,
        [Description("Explicit positive ROI width.")] int columnCount,
        [Description("0 = native incident flux; 1 = native flux per area, or absorbed flux for DetectorVolume.")] int dataType=0,
        [Description("Allow replacing the existing CSV only when explicitly true.")] bool overwrite=false,
        CancellationToken cancellationToken=default)
    {
        const string note="CSV is stored on the OpticStudio Host, not downloaded to the AI computer. Native 0-based row/column layout; no screenshot rotation or wavelength/radiometric unit calibration. DetectorVolume dataType 1 is absorbed flux. Inspect the returned SHA-256 and native units before interpreting power.";
        if(objectNumber<1 || dataType is not (0 or 1))
            return new Result(false,"Object number must be positive and dataType must be 0 or 1.",
                objectNumber,null,dataType,null,note);
        try
        {
            return await _session.ExecuteAsync("ExportNscDetectorCsv",
                new Dictionary<string,object?> {
                    ["objectNumber"]=objectNumber,["csvPath"]=csvPath,
                    ["startRow"]=startRow,["startColumn"]=startColumn,
                    ["rowCount"]=rowCount,["columnCount"]=columnCount,["dataType"]=dataType,
                    ["overwrite"]=overwrite
                },system=>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if(system.Mode!=SystemType.NonSequential)
                        throw new InvalidOperationException("NSC CSV export requires NonSequential mode.");
                    var nce=system.NCE ?? throw new InvalidOperationException("NCE is unavailable.");
                    if(objectNumber>nce.NumberOfObjects)
                        throw new ArgumentOutOfRangeException(nameof(objectNumber),"NSC detector does not exist.");
                    var row=nce.GetObjectAt(objectNumber)
                        ?? throw new InvalidOperationException("NCE detector row unavailable.");
                    if(!NscObjectClassification.IsDetector(row) ||
                        row.Type is ObjectType.DetectorColor or ObjectType.DetectorPolar)
                        throw new InvalidOperationException("Export supports native scalar flux detectors; use dedicated APIs for Color/Polar.");
                    if(!nce.GetDetectorDimensions(objectNumber,out var r,out var c) ||
                        r==0 || c==0)
                        throw new InvalidOperationException("Detector dimensions are unavailable.");
                    var detectorSize=nce.GetDetectorSize(objectNumber);
                    if((ulong)r*c!=detectorSize)
                        throw new InvalidDataException("Inconsistent NCE detector dimensions/size.");
                    if(row.Type==ObjectType.DetectorRectangle &&
                        row.ObjectData is IObjectDetectorRectangle rectangle &&
                        rectangle.NumberXPixels>0 && rectangle.NumberYPixels>0 &&
                        (ulong)rectangle.NumberXPixels*(ulong)rectangle.NumberYPixels==detectorSize)
                    {
                        c=(uint)rectangle.NumberXPixels;
                        r=(uint)rectangle.NumberYPixels;
                    }
                    if(!nce.GetDetectorData(objectNumber,0,0,out var total) ||
                        !double.IsFinite(total) ||
                        !nce.GetDetectorData(objectNumber,-3,0,out var hits) ||
                        !double.IsFinite(hits))
                        throw new InvalidOperationException("No finite detector flux/hit statistics; trace the model first.");
                    if(r>int.MaxValue || c>int.MaxValue)
                        throw new ArgumentException("Detector dimensions exceed the native integer index bounds.");
                    var result=NscDetectorCsvExport.Write(csvPath,overwrite,(int)r,(int)c,
                        startRow,startColumn,rowCount,columnCount,(y,x)=>
                        {
                            var pixel=checked((int)((long)y*c+x+1));
                            if(!nce.GetDetectorData(objectNumber,pixel,dataType,out var value))
                                throw new InvalidDataException("Detector pixel is unavailable: "+pixel);
                            return value;
                        },cancellationToken);
                    return new Result(true,null,objectNumber,row.TypeName,dataType,result,note);
                },cancellationToken).ConfigureAwait(false);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex)
        {
            return new Result(false,ex.Message,objectNumber,null,dataType,null,note);
        }
    }
}
