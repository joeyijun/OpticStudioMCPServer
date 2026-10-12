using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ZemaxMCP.DesktopShared;

internal static class HostMaintenanceClient
{
    private static readonly string DefaultTicket = Guid.NewGuid().ToString("N");
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

    internal static int Prepare(int hostPid, string? ticket = null)
    {
        var reply = Send(hostPid, "prepare:" + (ticket ?? DefaultTicket));
        if (!reply.StartsWith("ready:", StringComparison.Ordinal) || !int.TryParse(reply.Substring(6), out var workerPid))
            throw new InvalidOperationException("Update postponed. " + (reply.StartsWith("busy:", StringComparison.Ordinal) ? reply.Substring(5) : reply));
        return workerPid;
    }
    internal static void Resume(int hostPid, string? ticket = null)
    {
        if (Send(hostPid, "resume:" + (ticket ?? DefaultTicket)) != "resumed")
            throw new InvalidOperationException("This update does not own the Host maintenance reservation.");
    }

    private static string Send(int hostPid, string command)
    {
        using var pipe = new NamedPipeClientStream(".", "ZemaxMcpMaintenance-" + hostPid, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { pipe.Connect(4000); }
        catch (Exception ex) { throw new InvalidOperationException("Cannot verify safe shutdown. Stop the installed service with its tray Exit, wait for Jobs to finish, then retry installation.", ex); }
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actualPid) || actualPid != (uint)hostPid)
            throw new InvalidOperationException("Maintenance pipe does not belong to the checked Host process.");
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        var write = writer.WriteLineAsync(command);
        if (!write.Wait(6000)) throw new TimeoutException("Local maintenance write timed out.");
        var read = reader.ReadLineAsync();
        if (!read.Wait(6000)) throw new TimeoutException("Local maintenance reply timed out.");
        return read.GetAwaiter().GetResult() ?? throw new IOException("Host closed its maintenance reply.");
    }
}
