namespace ZemaxMCP.Server.Tools.Catalog;

public sealed class AnalysisEvidenceException : ArgumentException
{
    public string Code { get; }
    public string NextAction { get; }
    public AnalysisEvidenceException(string code, string message, string nextAction) : base(message)
    { Code = code; NextAction = nextAction; }
}
