using System.Diagnostics;
using System.Runtime.CompilerServices;
using ZemaxMCP.Server.Tools.System;
using ZemaxMCP.ToolManifest;

internal static class ExecutionLocationAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        foreach (var entry in StaticToolManifest.All)
        {
            Require(entry.Description.StartsWith(ExecutionLocationPolicy.ToolNotice), "Every tool states the execution location.");
            foreach (var property in entry.InputSchema.GetProperty("properties").EnumerateObject())
                if (property.Name.EndsWith("Path", StringComparison.Ordinal))
                    Require(property.Value.GetProperty("description").GetString()!.StartsWith(ExecutionLocationPolicy.PathNotice),
                        "Server path annotation: " + entry.Name + "." + property.Name);
        }
        Require(StaticToolManifest.GetRequired("zemax_execution_context").DomainId == "system" &&
            StaticToolManifest.IsAllowed("basic-viewing", "zemax_execution_context", true),
            "Execution context is available read-only in every profile.");
        Require(StaticToolManifest.GetRequired("zemax_list_lens_files").DomainId == "files" &&
            StaticToolManifest.GetRequired("zemax_list_lens_files").Impact == "ReadOnly" &&
            !StaticToolManifest.IsAllowed("basic-viewing", "zemax_list_lens_files", true),
            "File browsing is explicit metadata, not implicitly added to basic optical viewing.");

        var temp = Path.Combine(Path.GetTempPath(), "ZemaxMCP-server-browser-" + Guid.NewGuid().ToString("N"));
        var server = Path.Combine(temp, "server");
        var client = Path.Combine(temp, "client");
        var samples = Path.Combine(server, "Samples");
        var lenses = Path.Combine(server, "Lens");
        var child = Path.Combine(samples, "Sequential");
        var link = Path.Combine(samples, "escape");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(lenses);
        Directory.CreateDirectory(client);
        File.WriteAllText(Path.Combine(samples, "server-only.zos"), "server fixture");
        File.WriteAllText(Path.Combine(samples, "server-only.zmx"), "server fixture");
        File.WriteAllText(Path.Combine(samples, "secret.txt"), "must not appear");
        File.WriteAllText(Path.Combine(client, "client-only.zos"), "must not be visible");
        try
        {
            var context = WorkerExecutionContext.Create(server, Path.Combine(lenses, "current.zos"), "Standalone");
            Require(context.ExecutionComputer == Environment.MachineName &&
                context.PathNamespace == "opticstudio-worker" && !context.ClientComputerKnown &&
                !context.AutomaticFileTransfer && context.SamplesDirectory == samples,
                "Context describes Worker paths without guessing client co-location.");
            var listing = ServerLensBrowser.List("samples", "", 100, server, null, default);
            Require(listing.Success && !listing.Truncated && listing.Entries.Count == 3 &&
                listing.Entries.All(e => e.ServerPath.StartsWith(samples + Path.DirectorySeparatorChar)) &&
                !listing.Entries.Any(e => e.Name is "secret.txt" or "client-only.zos"),
                "Lists only server lens files and child directories, never client files.");
            var limited = ServerLensBrowser.List("samples", "", 1, server, null, default);
            Require(limited.Success && limited.Truncated && limited.Entries.Count == 1, "Output is bounded and explicitly truncated.");
            Require(ServerLensBrowser.List("samples", "Sequential", 100, server, null, default).Success, "Relative subdirectory works.");
            foreach (var invalid in new[] { "../client", "..\\client", client, @"C:\Windows", @"\\server\share", "Sequential:stream", "Sequential.", "*", "Sequential/../.." })
                Require(ServerLensBrowser.List("samples", invalid, 100, server, null, default).Code == "path_not_allowed",
                    "Traversal/rooted/device/wildcard path rejected: " + invalid);
            Require(ServerLensBrowser.List("arbitrary", "", 100, server, null, default).Code == "path_not_allowed", "Arbitrary roots rejected.");
            Require(ServerLensBrowser.List("samples", "", 201, server, null, default).Code == "path_not_allowed", "Invalid bound rejected.");
            Require(ServerLensBrowser.List("current", "", 100, server, null, default).Code == "server_root_unavailable", "No invented active-lens directory.");
            Require(ServerLensBrowser.List("samples", "missing", 100, server, null, default).Code == "server_path_not_found", "Missing path explicitly refers to server.");
            Require(ServerLensBrowser.List("current", "", 100, server, Path.Combine(lenses, "current.zos"), default).ServerDirectory == lenses, "Current lens root resolved on server.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { ServerLensBrowser.List("samples", "", 100, server, null, cancelled.Token); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }

            // Junctions require no Developer Mode/admin; remove the link itself
            // before deleting this uniquely created fixture tree.
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{client}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            if (!mklink.WaitForExit(5000) || mklink.ExitCode != 0)
                throw new InvalidOperationException("Could not create the confined junction regression fixture.");
            Require(!ServerLensBrowser.List("samples", "", 100, server, null, default).Entries.Any(e => e.Name == "escape"), "Link entry not exposed.");
            Require(ServerLensBrowser.List("samples", "escape", 100, server, null, default).Code == "path_not_allowed", "Link target escape rejected.");
            Require(ServerLensBrowser.List("current", "", 100, server, Path.Combine(link, "fixture.zos"), default).Code == "path_not_allowed", "Linked configured root rejected.");
            Console.WriteLine("Execution location: shared instructions, server path schemas, restricted browser, separate client/server fixtures and junction rejection passed.");
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link, false);
            Directory.Delete(temp, true);
        }
    }

    private static void Require(bool passed, string message)
    { if (!passed) throw new InvalidOperationException(message); }
}
