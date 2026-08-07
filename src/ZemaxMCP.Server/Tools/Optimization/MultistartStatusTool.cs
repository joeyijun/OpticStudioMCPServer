using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Services.ConstrainedOptimization;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class MultistartStatusTool
{
    private readonly MultistartState _state;

    public MultistartStatusTool(MultistartState state) => _state = state;

    public record MultistartStatusResult(
        bool IsRunning,
        bool IsInInitialLm,
        int CurrentTrial,
        int MaxTrials,
        double InitialMerit,
        double BestMerit,
        int TotalTrialsRun,
        int TotalTrialsAccepted,
        int SaveCount,
        string? SaveFolder,
        string? ErrorMessage,
        string? LastWarning,
        int InitialLmIteration,
        int InitialLmMaxIterations,
        double InitialLmMerit,
        string Summary);

    [ZemaxTool(Name = "zemax_multistart_status")]
    [Description("Check multistart progress, best merit, acceptance count, completed checkpoint saves, and the most recent auxiliary save warning. Does not block.")]
    public MultistartStatusResult Execute()
    {
        string summary;
        if (_state.IsRunning)
        {
            if (_state.IsInInitialLm)
            {
                if (_state.InitialLmMaxIterations > 0)
                {
                    double pct = (double)_state.InitialLmIteration / _state.InitialLmMaxIterations * 100;
                    summary = $"Initial LM optimization: iteration {_state.InitialLmIteration}/{_state.InitialLmMaxIterations} ({pct:F0}%) | Merit: {_state.InitialLmMerit:F6}";
                }
                else
                {
                    summary = "Starting initial LM optimization...";
                }
            }
            else
            {
                double pct = _state.MaxTrials > 0 ? (double)_state.CurrentTrial / _state.MaxTrials * 100 : 0;
                summary = $"Trial {_state.CurrentTrial}/{_state.MaxTrials} ({pct:F1}%) | " +
                          $"Best merit: {_state.BestMerit:F6} | Accepted: {_state.TotalTrialsAccepted} | " +
                          $"Completed saves: {_state.SaveCount}";
            }
        }
        else if (_state.HasState)
        {
            var errorPart = _state.ErrorMessage != null ? $" ({_state.ErrorMessage})" : "";
            summary = $"Completed{errorPart}. Final merit: {_state.BestMerit:F6} | " +
                      $"Trials: {_state.TotalTrialsRun} | Accepted: {_state.TotalTrialsAccepted} | " +
                      $"Completed saves: {_state.SaveCount}";
        }
        else
        {
            summary = "No multistart optimization has been run yet.";
        }

        if (!string.IsNullOrWhiteSpace(_state.LastWarning))
            summary += $" | Warning: {_state.LastWarning}";

        return new MultistartStatusResult(
            _state.IsRunning,
            _state.IsInInitialLm,
            _state.CurrentTrial,
            _state.MaxTrials,
            _state.InitialMerit,
            _state.BestMerit,
            _state.TotalTrialsRun,
            _state.TotalTrialsAccepted,
            _state.SaveCount,
            _state.SaveFolder,
            _state.ErrorMessage,
            _state.LastWarning,
            _state.InitialLmIteration,
            _state.InitialLmMaxIterations,
            _state.InitialLmMerit,
            summary);
    }
}
