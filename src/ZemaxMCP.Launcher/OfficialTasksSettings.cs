using Newtonsoft.Json.Linq;

namespace ZemaxMCP.Launcher;

internal static class OfficialTasksSettings
{
    // Existing installations without this setting adopt the requested new default.
    // An explicitly saved false must survive upgrades and restarts.
    internal static bool IsEnabled(JToken? persistedValue) => persistedValue?.Value<bool>() ?? true;

    internal static string HostArgument(bool enabled) =>
        "--enable-official-tasks " + (enabled ? "true" : "false");
}
