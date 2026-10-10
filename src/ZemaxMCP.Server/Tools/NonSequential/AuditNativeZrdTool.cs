using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;
using ZOSAPI;
using ZOSAPI.Tools;
using ZOSAPI.Tools.RayTrace;

namespace ZemaxMCP.Server.Tools.NonSequential;

/// <summary>
/// Reads an explicitly selected native local ZRD ray database through the
/// supported OpticStudio reader, never a guessed or hand-parsed binary layout.
/// File access is Caution; results are ray-topology evidence, not a claim of
/// measured source power or independently classified coating/material loss.
/// </summary>
[ZemaxToolType]
public sealed class AuditNativeZrdTool
{
    private readonly IZemaxSession _session;
    public AuditNativeZrdTool(IZemaxSession session)=>_session=session;

    public sealed record Result(bool Success,string? Error,string? ZrdSha256,
        int NativeRayRecords,int NativeSegmentRecords,
        ZrdPathEnergyCore.Report? Topology,
        string Interpretation);

    [ZemaxTool(Name="zemax_audit_native_zrd")]
    [Description("PRIVILEGED local file READ: read an explicitly named LOCAL OpticStudio .ZRD ray database (sensitive user optical data), using the official native ZRD reader. Cap 1024 rays/8192 segments/128 MiB, verify file integrity and parent indexes, report only algebraic branch energy topology. It does NOT prove that the ZRD belongs to the most recent trace, derive coating/bulk absorption, or close a source-detector energy balance.")]
    public async Task<Result> ExecuteAsync(
        [Description("Absolute .ZRD filename on the OPTICSTUDIO HOST machine, existing regular file. File data may be private.")] string zrdPath,
        [Description("Wall-clock maximum for ZRD reader, in seconds (1..120).")] double timeoutSeconds=30,
        CancellationToken cancellationToken=default)
    {
        const string note="Read-only parse of an EXPLICIT EXISTING local .ZRD using ZOS-API OpenRayDatabaseReader and GetResults. RayNumber and parent semantics are version-sensitive; native segment-parent indexes are validated and never silently repaired. File hash identifies input but cannot prove it came from the same previous detector trace. The graph audit certifies only algebraic linkage. Detector-hit double-counting, native source-launched flux, polarized/coating/material/CAD category attribution and physical watts remain UNKNOWN.";
        try
        {
            if(string.IsNullOrWhiteSpace(zrdPath)||!Path.IsPathFullyQualified(zrdPath) ||
               !string.Equals(Path.GetExtension(zrdPath),".zrd",StringComparison.OrdinalIgnoreCase) ||
               !double.IsFinite(timeoutSeconds)||timeoutSeconds<1||timeoutSeconds>120)
                throw new ArgumentException("Supply an absolute existing .ZRD path and timeoutSeconds in [1,120].");
            var fullPath=Path.GetFullPath(zrdPath);
            var info=new FileInfo(fullPath);
            if(!info.Exists||info.Length is <=0 or >134217728)
                throw new ArgumentException("Native ZRD must exist and be 1..128 MiB.");
            var originalLength=info.Length;var modified=info.LastWriteTimeUtc;
            return await _session.ExecuteAsync("AuditNativeZrd",
                new Dictionary<string,object?>{
                    ["zrdPath"]=fullPath,["timeoutSeconds"]=timeoutSeconds
                },system=>{
                    cancellationToken.ThrowIfCancellationRequested();
                    if(system.Mode!=SystemType.NonSequential)
                        throw new InvalidOperationException("Native ZRD analysis requires NonSequential mode.");
                    var reader=system.Tools.OpenRayDatabaseReader()
                        ?? throw new NotSupportedException("Installed ZOS-API lacks the native ZRD reader.");
                    try
                    {
                        reader.ZRDFile=fullPath;
                        if(!reader.IsValid)
                            throw new InvalidOperationException("ZRD reader rejected the specified native ray database.");
                        if(!reader.Run())
                            throw new InvalidOperationException("ZRD reader failed to start.");
                        var watch=Stopwatch.StartNew();
                        while(reader.IsRunning)
                        {
                            if(cancellationToken.IsCancellationRequested)
                            {
                                if(reader.CanCancel)reader.Cancel();
                                cancellationToken.ThrowIfCancellationRequested();
                            }
                            if(watch.Elapsed.TotalSeconds>=timeoutSeconds)
                            {
                                if(reader.CanCancel)reader.Cancel();
                                throw new TimeoutException("ZRD reader timed out; Worker hard recovery remains a separate safety gate.");
                            }
                            var status=reader.WaitWithTimeout(0.25);
                            if(status is RunStatus.FailedToStart or RunStatus.InvalidTimeout)
                                throw new InvalidOperationException("Native ZRD reader failed to complete.");
                        }
                        if(!reader.Succeeded)
                            throw new InvalidOperationException(reader.ErrorMessage??"Native ZRD reader reported failure.");
                        var native=reader.GetResults();
                        if(native==null || !native.IsValid)
                            throw new InvalidOperationException("Native ZRD reader returned invalid results.");
                        var segments=new List<ZrdPathEnergyCore.Segment>();
                        var rays=0;
                        while(native.ReadNextResult(out var rayNumber,out var waveIndex,
                            out var wavelengthUm,out var count))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if(++rays>1024 || count is <1 or >8192 ||
                               segments.Count+count>8192 ||
                               !double.IsFinite(wavelengthUm) || wavelengthUm<=0 ||
                               waveIndex<1)
                                throw new InvalidDataException("ZRD evidence exceeded 1024 rays / 8192 segments or returned invalid wavelength/segment count.");
                            for(var index=0;index<count;index++)
                            {
                                if((index&63)==0)cancellationToken.ThrowIfCancellationRequested();
                                if(!native.ReadNextSegmentFull(
                                    out var level,out var parent,out var hitObject,
                                    out var hitFace,out var insideOf,out RayStatus rayStatus,
                                    out var x,out var y,out var z,
                                    out var l,out var m,out var n,
                                    out var exr,out var exi,out var eyr,out var eyi,
                                    out var ezr,out var ezi,out var intensity,
                                    out var pathLength,out var xybin,out var lmbin,
                                    out var xn,out var yn,out var zn,out var refrIndex,
                                    out var startingPhase,out var phaseOf,out var phaseAt))
                                    throw new InvalidDataException("Native ZRD unexpectedly ended within a ray.");
                                if(index>0 && (parent<0||parent>=index))
                                    throw new NotSupportedException("ZRD parent index is incompatible with this audited native reader release: no physical branch inference is permitted.");
                                if(!double.IsFinite(intensity)||intensity<0||hitObject<0 ||
                                   !double.IsFinite(pathLength)||pathLength<0)
                                    throw new InvalidDataException("Native ZRD contained nonfinite/invalid branch intensity, hit object or path length.");
                                // Stable internal record ordinal prevents RayNumber overlap
                                // between distinct native reader results and wavelengths.
                                segments.Add(new ZrdPathEnergyCore.Segment(rays,index,
                                    index==0?-1:parent,intensity,hitObject,false));
                            }
                        }
                        if(rays==0)
                            throw new InvalidDataException("Native ZRD contains no auditable ray records.");
                        var audit=ZrdPathEnergyCore.Audit(segments);
                        var check=new FileInfo(fullPath);
                        if(check.Length!=originalLength || check.LastWriteTimeUtc!=modified)
                            throw new IOException("ZRD input changed during the reader session.");
                        string checksum;
                        using(var stream=File.OpenRead(fullPath))
                            checksum=Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                        return new Result(true,null,checksum,rays,segments.Count,audit,note);
                    }
                    finally
                    {
                        try { if(reader.IsRunning && reader.CanCancel)reader.Cancel(); }
                        finally { reader.Close(); }
                    }
                },cancellationToken).ConfigureAwait(false);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception e)
        {return new Result(false,e.Message,null,0,0,null,note);}
    }
}
