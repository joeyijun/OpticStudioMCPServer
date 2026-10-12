namespace ZemaxMCP.ToolManifest;

/// <summary>One shared location contract for SDK discovery and every tool schema.</summary>
public static class ExecutionLocationPolicy
{
    public const string PathNamespace = "opticstudio-worker";
    public const string ToolNotice = "Runs on the OpticStudio computer, not the AI client's filesystem. ";
    public const string PathNotice = "This path belongs to the OpticStudio Worker computer, not the AI client. Files are not transferred automatically. ";
    public const string Instructions =
        "All Zemax tools execute on the computer running the OpticStudio Worker. " +
        "The AI client may be on another computer; do not assume they share a filesystem. " +
        "Call zemax_execution_context first to identify the execution computer, current lens and available server directory roots. " +
        "All lens, sample, import, export, snapshot and ZOS-API paths belong to that OpticStudio computer, not the AI client. " +
        "Use zemax_list_lens_files for bounded server-side lens browsing if your tool profile permits it. " +
        "Do not use the AI client's local shell to locate or install Zemax, or to test whether a server path exists. " +
        "A client-local file must be explicitly transferred by the user or an authorized file-transfer facility; this MCP does not upload it automatically. " +
        "Standalone mode controls a Worker-owned application, which may differ from the user's visible OpticStudio window. " +
        "Preserve unsaved work; do not switch lenses, overwrite files or change optical state without appropriate user authorization.";
}
