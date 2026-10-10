// Offline licensed diagnostic. Compiled by Windows PowerShell against the
// LOCAL installed ZOS-API. No DLL redistribution and no normal Host access.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using ZOSAPI;
using ZOSAPI.Editors.NCE;
using ZOSAPI.Tools;
using ZOSAPI.Tools.RayTrace;

public sealed class ZrdProbeRead
{
    public string Label;
    public string PreviousFilter;
    public string AppliedFilter;
    public string Status;
    public string Error;
    public string Sha256;
    public long Bytes;
    public int Rays;
    public int Segments;
    public int UnexpectedParents;
}

public static class NativeZrdProbe
{
    private static void RunBounded(ISystemTool tool)
    {
        var clock=Stopwatch.StartNew();
        if(!tool.Run()) throw new InvalidOperationException("Native tool refused Run().");
        while(tool.IsRunning)
        {
            if(clock.Elapsed.TotalSeconds>=30)
            {
                if(tool.CanCancel)tool.Cancel();
                throw new TimeoutException("Independent native diagnostic exceeded 30 seconds.");
            }
            tool.WaitWithTimeout(0.25);
        }
        if(!tool.Succeeded)throw new InvalidOperationException(tool.ErrorMessage ?? "Native tool failed.");
    }

    private static ZrdProbeRead Read(IOpticalSystem system,string path,string label,bool clearFilter)
    {
        var result=new ZrdProbeRead {Label=label};
        IZRDReader reader=null;
        try
        {
            result.Bytes=new FileInfo(path).Length;
            if(result.Bytes<=0 || result.Bytes>134217728)throw new InvalidDataException("ZRD must be 1..128 MiB.");
            using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))
                result.Sha256=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
            reader=system.Tools.OpenRayDatabaseReader();
            if(reader==null)throw new InvalidOperationException("Native reader unavailable for mode "+system.Mode);
            reader.ZRDFile=path;
            result.PreviousFilter=reader.Filter;
            if(clearFilter)reader.Filter="";
            result.AppliedFilter=reader.Filter;
            RunBounded(reader);
            result.Status=reader.Status.ToString();
            var native=reader.GetResults();
            if(native==null || !native.IsValid)throw new InvalidDataException("Native results invalid.");
            int ray,wave,count;
            double wavelength;
            var clock=Stopwatch.StartNew();
            while(native.ReadNextResult(out ray,out wave,out wavelength,out count))
            {
                if(++result.Rays>1024 || count<1 || result.Segments+count>8192 || clock.Elapsed.TotalSeconds>30)
                    throw new InvalidDataException("Diagnostic exceeds 1024 rays / 8192 segments / 30 seconds.");
                for(int i=0;i<count;i++)
                {
                    int level,parent,obj,face,inside,xy,angular;
                    RayStatus status;
                    double x,y,z,l,m,n,exr,exi,eyr,eyi,ezr,ezi,intensity,length,xn,yn,zn,index,phase,phaseOf,phaseAt;
                    if(!native.ReadNextSegmentFull(out level,out parent,out obj,out face,out inside,out status,
                        out x,out y,out z,out l,out m,out n,out exr,out exi,out eyr,out eyi,out ezr,out ezi,
                        out intensity,out length,out xy,out angular,out xn,out yn,out zn,out index,out phase,out phaseOf,out phaseAt))
                        throw new InvalidDataException("Unexpected segment EOF.");
                    result.Segments++;
                    if(i>0 && (parent<0 || parent>=i))result.UnexpectedParents++;
                }
            }
            if(result.Rays==0)result.Error="Native reader yielded zero ray records (not a passing data test).";
        }
        catch(Exception e){result.Error=e.Message;}
        finally{if(reader!=null){try{if(reader.IsRunning && reader.CanCancel)reader.Cancel();}finally{reader.Close();}}}
        return result;
    }

    public static List<ZrdProbeRead> Run(string root,string sample,string archived,string output)
    {
        if(System.Threading.Thread.CurrentThread.GetApartmentState()!=System.Threading.ApartmentState.STA)
            throw new InvalidOperationException("Run with Windows PowerShell -STA.");
        if(!ZOSAPI_NetHelper.ZOSAPI_Initializer.Initialize(root))throw new InvalidOperationException("Local API initializer failed.");
        var connection=new ZOSAPI_Connection();
        var app=connection.CreateNewApplication();
        if(app==null)throw new InvalidOperationException("Cannot open an isolated Standalone application; the license may have no free instance.");
        try
        {
            if(!app.IsValidLicenseForAPI)throw new InvalidOperationException("Invalid API license: "+app.LicenseStatus);
            var system=app.PrimarySystem;
            if(!system.LoadFile(sample,false) || system.Mode!=SystemType.NonSequential)throw new InvalidOperationException("Probe requires the official pure-NSC Beam splitter sample.");
            // Isolated application's in-memory model only; Samples unchanged.
            system.NCE.GetObjectAt(1).GetObjectCell(ObjectColumn.Par2).IntegerValue=10;
            var lens=Path.Combine(output,"probe.zmx");
            system.SaveAs(lens);
            var path=Path.Combine(output,"probe.ZRD");
            var trace=system.Tools.OpenNSCRayTrace();
            if(trace==null)throw new InvalidOperationException("Native NSC tracer unavailable.");
            try
            {
                trace.ClearDetectors(0);
                trace.SplitNSCRays=true;
                trace.ScatterNSCRays=false;
                trace.UsePolarization=true;
                trace.IgnoreErrors=false;
                trace.SaveRays=true;
                trace.ZRDFormat=ZRDFormatType.UncompressedFullData;
                trace.SaveRaysFile="probe.ZRD";
                RunBounded(trace);
            }
            finally{try{if(trace.IsRunning && trace.CanCancel)trace.Cancel();}finally{trace.Close();}}
            if(!File.Exists(path))throw new FileNotFoundException("Native trace did not write its expected database beside the temporary model.",path);
            var results=new List<ZrdProbeRead>();
            results.Add(Read(system,path,"fresh/default-filter",false));
            results.Add(Read(system,path,"fresh/empty-filter",true));
            if(File.Exists(archived))
            {
                results.Add(Read(system,archived,"archive/default-filter",false));
                results.Add(Read(system,archived,"archive/empty-filter",true));
                var archivedModel=Path.ChangeExtension(archived,".ZMX");
                if(File.Exists(archivedModel) && system.LoadFile(archivedModel,false))
                    results.Add(Read(system,archived,"archive/matching-model/empty-filter",true));
            }
            return results;
        }
        finally{app.CloseApplication();}
    }
}
