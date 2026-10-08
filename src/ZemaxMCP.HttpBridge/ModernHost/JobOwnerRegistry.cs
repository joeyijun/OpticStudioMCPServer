using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ZemaxMCP.HttpBridge.ModernHost;

/// <summary>
/// Host-side fail-closed ownership table for MCP-managed background jobs.
/// Creation is bound to authenticated identity and Worker generation. The
/// Worker can report a Job ID, but never gets to assert its requesting owner.
/// </summary>
internal sealed class JobOwnerRegistry
{
    private sealed record Ownership(string ClientId, long Generation, long Sequence);
    private readonly object _gate = new();
    private readonly Dictionary<string, Ownership> _owners = new(StringComparer.Ordinal);
    private long _sequence;
    internal const int MaximumRecords = 256;

    internal void Register(string clientId, string jobId, long generation)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(jobId) || generation <= 0)
            throw new InvalidOperationException("Background-job ownership cannot be registered without a valid client, job ID, and Worker generation.");
        lock (_gate)
        {
            if (_owners.TryGetValue(jobId, out var existing))
            {
                if (existing.ClientId != clientId || existing.Generation != generation)
                    throw new InvalidOperationException("Background-job ID collision across owners or Worker generations.");
                return;
            }
            // Worker Job history is separately capped at 128. The Host can
            // forget the oldest authorization record (failing closed on later
            // lookup) to keep ownership metadata bounded without blocking new
            // background jobs after many successive completions.
            if (_owners.Count >= MaximumRecords)
            {
                var oldest = _owners.OrderBy(pair => pair.Value.Sequence).First();
                _owners.Remove(oldest.Key);
            }
            _owners.Add(jobId, new Ownership(clientId, generation, ++_sequence));
        }
    }

    internal bool IsOwned(string clientId, string jobId, long generation)
    {
        lock (_gate)
            return generation > 0 && _owners.TryGetValue(jobId, out var owner) &&
                   owner.ClientId == clientId && owner.Generation == generation;
    }

    internal void ReleaseGeneration(long generation)
    {
        if (generation <= 0) return;
        lock (_gate)
            foreach (var id in _owners.Where(pair => pair.Value.Generation == generation)
                         .Select(pair => pair.Key).ToArray())
                _owners.Remove(id);
    }

    internal static bool TryGetJobId(CallToolRequestParams request, out string jobId)
    {
        jobId = string.Empty;
        if (request.Arguments == null ||
            !request.Arguments.TryGetValue("jobId", out var value) ||
            value.ValueKind != JsonValueKind.String)
            return false;
        var candidate = value.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 128)
            return false;
        jobId = candidate;
        return true;
    }

    internal static CallToolResult Denied() => new()
    {
        Content = new List<ContentBlock>
        {
            new TextContentBlock { Text = "Job not found or not owned by the authenticated client." }
        },
        IsError = true
    };

    internal static CallToolResult ValidateSingleResult(CallToolResult result, string jobId)
    {
        if (result.IsError == true) return result;
        if (result.Content.Count != 1 || result.Content[0] is not TextContentBlock textBlock)
            return Denied();
        try
        {
            using var document = JsonDocument.Parse(textBlock.Text);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryReadJobId(document.RootElement, out var returnedId) ||
                !string.Equals(returnedId, jobId, StringComparison.Ordinal))
                return Denied();
            return result;
        }
        catch (JsonException) { return Denied(); }
    }

    internal static CallToolResult FilterList(CallToolResult result, string clientId, long generation, JobOwnerRegistry registry)
    {
        if (result.IsError == true) return result;
        var content = new List<ContentBlock>(result.Content.Count);
        foreach (var block in result.Content)
        {
            if (block is not TextContentBlock textBlock) return Denied();
            try
            {
                using var document = JsonDocument.Parse(textBlock.Text);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return Denied();

                var filtered = new List<JsonElement>();
                foreach (var job in document.RootElement.EnumerateArray())
                {
                    if (job.ValueKind != JsonValueKind.Object) return Denied();
                    if (!TryReadJobId(job, out var id)) return Denied();
                    if (registry.IsOwned(clientId, id, generation)) filtered.Add(job.Clone());
                }
                content.Add(new TextContentBlock { Text = JsonSerializer.Serialize(filtered) });
            }
            catch (JsonException) { return Denied(); }
        }
        if (content.Count == 0) return Denied();
        return new CallToolResult { Content = content, IsError = false };
    }

    private static bool TryReadJobId(JsonElement job, out string jobId)
    {
        jobId = string.Empty;
        foreach (var property in job.EnumerateObject())
        {
            if (!property.Name.Equals("jobId", StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.String) continue;
            jobId = property.Value.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(jobId);
        }
        return false;
    }
}
