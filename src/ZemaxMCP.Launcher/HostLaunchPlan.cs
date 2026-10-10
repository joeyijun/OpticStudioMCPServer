using System;
using System.Diagnostics;
using System.IO;
using System.Net;

namespace ZemaxMCP.Launcher;

/// <summary>
/// WPF-free construction/validation of local Host process arguments and
/// environment-only credentials. No UI lifecycle and no COM calls.
/// </summary>
internal static class HostLaunchPlan
{
    internal static ProcessStartInfo Build(string hostExecutable,string workerExecutable,
        string zemaxRoot,string bindHost,int port,bool readOnly,string profile,
        string snapshotDir,bool officialTasks,bool shareLan,string lanAddress,
        bool tlsEnabled,string? pfxPath,string bearerToken,string? pfxPassword)
    {
        static string SafePath(string value,string name)
        {
            if(string.IsNullOrWhiteSpace(value)||!Path.IsPathRooted(value)||
               value.IndexOfAny(new[]{'"','\r','\n','\0'})>=0)
                throw new ArgumentException(name+" must be an absolute, quote-free local filesystem path.");
            return value;
        }
        var exe=SafePath(hostExecutable,nameof(hostExecutable));
        var worker=SafePath(workerExecutable,nameof(workerExecutable));
        var root=SafePath(zemaxRoot,nameof(zemaxRoot));
        var snapshots=SafePath(snapshotDir,nameof(snapshotDir));
        if(port is <1 or >65535 || bindHost!=(shareLan?"0.0.0.0":"127.0.0.1"))
            throw new ArgumentException("Host binding/port is not consistent with LAN mode.");
        if(profile is not ("basic-viewing" or "sequential-design" or
            "nonsequential-stray-light" or "optimization-tolerance" or "full-expert"))
            throw new ArgumentException("Unsupported MCP toolset profile.");
        if(shareLan && (!IPAddress.TryParse(lanAddress,out var ip) ||
            ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(ip)))
            throw new ArgumentException("LAN sharing requires a concrete IPv4 interface address.");
        var cert=tlsEnabled?SafePath(pfxPath??"",nameof(pfxPath)):"";
        if(tlsEnabled && (!cert.EndsWith(".pfx",StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(pfxPassword)))
            throw new ArgumentException("TLS requires a .pfx certificate and nonempty environment-only password.");
        if(bearerToken.IndexOfAny(new[]{'\r','\n','\0'})>=0)
            throw new ArgumentException("Bearer credential contains invalid control characters.");
        var args="--server \""+worker+"\" --zemax-root \""+root+"\" "+
            "--host "+bindHost+" --port "+port+" --read-only "+
            (readOnly?"true":"false")+" --toolset "+profile+
            " --snapshot-dir \""+snapshots+"\" --enable-official-tasks "+
            (officialTasks?"true":"false");
        if(shareLan)
            args+=" --allowed-host "+lanAddress+" --allowed-origin "+
                (tlsEnabled?"https":"http")+"://"+lanAddress+":*";
        if(tlsEnabled)
            args+=" --tls-pfx \""+cert+"\"";
        var info=new ProcessStartInfo(exe,args){UseShellExecute=false,CreateNoWindow=true};
        info.EnvironmentVariables["ZEMAX_MCP_TOKEN"]=bearerToken;
        if(tlsEnabled)
            info.EnvironmentVariables["ZEMAX_MCP_TLS_PFX_PASSWORD"]=pfxPassword!;
        return info;
    }
}
