using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ZemaxMCP.Launcher;

internal static class ConfigurationSafety
{
    // Missing or stale health must not be mistaken for an idle optical system.
    internal static bool IsIdle(JObject health) =>
        health["bridgeRunning"]?.Value<bool>() == true &&
        health["statusFresh"]?.Value<bool>() == true &&
        health["workerBusy"]?.Value<bool>() != true &&
        (health["activeRequests"]?.Value<int>() ?? 0) == 0 &&
        (health["requestQueue"]?["queued"]?.Value<int>() ?? 0) == 0 &&
        !((health["jobs"] as JArray)?.Any(j => IsActive(j["state"]?.ToString())) ?? false) &&
        !((health["tasks"] as JArray)?.Any(j => IsActive(j["status"]?.ToString() ?? j["state"]?.ToString())) ?? false);

    private static bool IsActive(string? state) => new[] { "Queued", "Running", "Cancelling", "working", "input_required" }
        .Any(value => string.Equals(value, state, StringComparison.OrdinalIgnoreCase));
}
