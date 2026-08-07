using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Documentation;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class SearchOperandsTool
{
    private const int MaximumResults = 100;
    private readonly OperandSearchService _searchService;

    public SearchOperandsTool(OperandSearchService searchService)
        => _searchService = searchService;

    public record OperandMatch(
        string Name,
        string Description,
        string Category,
        double Relevance
    );

    public record SearchOperandsResult(
        bool Success,
        string? Error,
        int TotalMatches,
        List<OperandMatch> Matches
    );

    [ZemaxTool(Name = "zemax_search_operands")]
    [Description("Search the packaged optimization-operand documentation by name or description. This does not access OpticStudio.")]
    public Task<SearchOperandsResult> ExecuteAsync(
        [Description("Non-empty search query (for example 'spot size', 'MTF', 'thickness')")] string query,
        [Description("Maximum results to return (1-100)")] int maxResults = 10,
        [Description("Optional exact category filter") ] string? category = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("query cannot be empty.", nameof(query));
            if (maxResults < 1 || maxResults > MaximumResults)
                throw new ArgumentOutOfRangeException(nameof(maxResults), $"maxResults must be between 1 and {MaximumResults}.");

            var normalizedCategory = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
            var results = _searchService.Search(query.Trim(), maxResults, normalizedCategory);
            var matches = results.Select(r =>
            {
                if (double.IsNaN(r.Score) || double.IsInfinity(r.Score))
                    throw new InvalidDataException($"Operand search returned non-finite relevance for {r.Operand.Name}: {r.Score}.");
                return new OperandMatch(r.Operand.Name, r.Operand.Description, r.Operand.Category, r.Score);
            }).ToList();

            return Task.FromResult(new SearchOperandsResult(true, null, matches.Count, matches));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new SearchOperandsResult(false, ex.Message, 0, new List<OperandMatch>()));
        }
    }
}
