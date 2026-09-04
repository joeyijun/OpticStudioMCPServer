using MathNet.Numerics.LinearAlgebra;
using ZemaxMCP.Core.Models;
using ZOSAPI;

namespace ZemaxMCP.Core.Services.ConstrainedOptimization;

public class LMOptimizer
{
    private readonly MeritFunctionReader _meritReader;

    public LMOptimizer(MeritFunctionReader meritReader)
    {
        _meritReader = meritReader ?? throw new ArgumentNullException(nameof(meritReader));
    }

    public OptimizationResult Optimize(
        IOpticalSystem system,
        List<OptVariable> variables,
        int maxIterations = 200,
        double initialMu = 1e-3,
        double delta = 1e-7,
        double gradientTolerance = 1e-10,
        double stepTolerance = 1e-10,
        double functionTolerance = 1e-10,
        bool useBroydenUpdate = false,
        int maxRestarts = 0,
        Action<int, int, double>? onIterationProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        if (variables == null) throw new ArgumentNullException(nameof(variables));
        ValidatePositive(maxIterations, nameof(maxIterations));
        ValidatePositiveFinite(initialMu, nameof(initialMu));
        ValidatePositiveFinite(delta, nameof(delta));
        ValidateNonNegativeFinite(gradientTolerance, nameof(gradientTolerance));
        ValidateNonNegativeFinite(stepTolerance, nameof(stepTolerance));
        ValidateNonNegativeFinite(functionTolerance, nameof(functionTolerance));
        if (maxRestarts < 0) throw new ArgumentOutOfRangeException(nameof(maxRestarts), "maxRestarts must be >= 0.");

        var result = new OptimizationResult();
        double[]? x = null;
        int nParams = variables.Count;
        int iterations = 0;
        int restarts = 0;
        double cost = 0;
        List<MeritRow>? meritRows = null;
        int effectiveMaxRestarts = useBroydenUpdate ? maxRestarts : 0;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nParams == 0)
            {
                result.Success = false;
                result.Message = "No variables to optimize.";
                return result;
            }

            x = new double[nParams];
            double[] lower = new double[nParams];
            double[] upper = new double[nParams];
            for (int i = 0; i < nParams; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                x[i] = ZosVariableAccessor.GetVariableValue(system, variables[i]);
                variables[i].Value = x[i];
                variables[i].StartingValue = x[i];
                lower[i] = variables[i].LowerBound;
                upper[i] = variables[i].UpperBound;
            }

            system.MFE.CalculateMeritFunction();
            cancellationToken.ThrowIfCancellationRequested();
            meritRows = _meritReader.ReadMeritRows(system);
            int nResiduals = meritRows.Count;
            if (nResiduals == 0)
            {
                result.Success = false;
                result.Message = "No merit function rows with non-zero weight.";
                return result;
            }

            double[] residuals = ComputeResiduals(meritRows);
            cost = DotProduct(residuals, residuals);
            result.InitialMerit = Math.Sqrt(cost / SumWeights(meritRows));
            ValidateFinite(result.InitialMerit, "initial merit function");
            double mu = initialMu;

            bool runAgain = true;
            while (runAgain)
            {
                cancellationToken.ThrowIfCancellationRequested();
                runAgain = false;
                double[,]? jacobian = null;
                bool needFullJacobian = true;
                bool exitedEarly = false;
                int iterationsThisRun = 0;
                int remainingIterations = maxIterations - iterations;
                if (remainingIterations <= 0) break;

                for (int iter = 0; iter < remainingIterations; iter++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    iterations++;
                    iterationsThisRun++;

                    if (needFullJacobian)
                    {
                        jacobian = ComputeJacobian(system, variables, x, residuals, nResiduals, nParams, delta, cancellationToken);
                        needFullJacobian = false;
                    }

                    var jtJ = new double[nParams, nParams];
                    var jtr = new double[nParams];
                    double gradNorm = 0;
                    for (int i = 0; i < nParams; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        for (int j = 0; j < nParams; j++)
                        {
                            double sum = 0;
                            for (int k = 0; k < nResiduals; k++)
                                sum += jacobian![k, i] * jacobian[k, j];
                            jtJ[i, j] = sum;
                        }

                        double residualSum = 0;
                        for (int k = 0; k < nResiduals; k++)
                            residualSum += jacobian![k, i] * residuals[k];
                        jtr[i] = residualSum;
                        gradNorm += jtr[i] * jtr[i];
                    }
                    gradNorm = Math.Sqrt(gradNorm);
                    if (gradNorm < gradientTolerance)
                    {
                        exitedEarly = true;
                        break;
                    }

                    bool stepAccepted = false;
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var matrix = Matrix<double>.Build.DenseOfArray(jtJ);
                        for (int i = 0; i < nParams; i++)
                            matrix[i, i] += mu * Math.Max(jtJ[i, i], 1e-6);
                        var rhs = Vector<double>.Build.DenseOfArray(jtr);

                        Vector<double> step;
                        try { step = matrix.Solve(-rhs); }
                        catch
                        {
                            mu *= 10;
                            continue;
                        }

                        double stepNorm = 0;
                        double xNorm = 0;
                        for (int i = 0; i < nParams; i++)
                        {
                            stepNorm += step[i] * step[i];
                            xNorm += x[i] * x[i];
                        }
                        if (Math.Sqrt(stepNorm) < stepTolerance * (Math.Sqrt(xNorm) + stepTolerance))
                        {
                            exitedEarly = true;
                            stepAccepted = true;
                            break;
                        }

                        var xNew = new double[nParams];
                        for (int i = 0; i < nParams; i++)
                            xNew[i] = Math.Max(lower[i], Math.Min(upper[i], x[i] + step[i]));

                        cancellationToken.ThrowIfCancellationRequested();
                        for (int i = 0; i < nParams; i++)
                            ZosVariableAccessor.SetVariableValue(system, variables[i], xNew[i]);
                        system.MFE.CalculateMeritFunction();
                        cancellationToken.ThrowIfCancellationRequested();

                        var newRows = _meritReader.ReadMeritRows(system);
                        if (newRows.Count != nResiduals)
                            throw new InvalidOperationException($"Merit-function residual count changed during optimization ({nResiduals} -> {newRows.Count}).");
                        double[] newResiduals = ComputeResiduals(newRows);
                        double newCost = DotProduct(newResiduals, newResiduals);
                        ValidateFinite(newCost, "LM cost");

                        if (newCost < cost)
                        {
                            if (useBroydenUpdate)
                            {
                                double[] dx = new double[nParams];
                                for (int i = 0; i < nParams; i++) dx[i] = xNew[i] - x[i];
                                double dxTdx = DotProduct(dx, dx);
                                if (dxTdx > 0)
                                {
                                    double[] dr = new double[nResiduals];
                                    for (int i = 0; i < nResiduals; i++)
                                    {
                                        double jdx = 0;
                                        for (int p = 0; p < nParams; p++) jdx += jacobian![i, p] * dx[p];
                                        dr[i] = (newResiduals[i] - residuals[i]) - jdx;
                                    }
                                    for (int i = 0; i < nResiduals; i++)
                                        for (int p = 0; p < nParams; p++)
                                            jacobian![i, p] += dr[i] * dx[p] / dxTdx;
                                }
                            }
                            else
                            {
                                needFullJacobian = true;
                            }

                            x = xNew;
                            residuals = newResiduals;
                            meritRows = newRows;
                            if (Math.Abs(cost - newCost) < functionTolerance * Math.Max(cost, 1e-30) && iterationsThisRun > 1)
                            {
                                cost = newCost;
                                exitedEarly = true;
                                stepAccepted = true;
                                break;
                            }

                            cost = newCost;
                            mu = Math.Max(mu * 0.3333, 1e-15);
                            stepAccepted = true;
                            double iterMerit = Math.Sqrt(cost / SumWeights(newRows));
                            onIterationProgress?.Invoke(iterations, maxIterations, iterMerit);
                            break;
                        }

                        mu = Math.Min(mu * 3.0, 1e15);
                        for (int i = 0; i < nParams; i++)
                            ZosVariableAccessor.SetVariableValue(system, variables[i], x[i]);
                    }

                    if (!stepAccepted || exitedEarly)
                    {
                        exitedEarly = true;
                        break;
                    }
                }

                if (exitedEarly && restarts < effectiveMaxRestarts && iterations < maxIterations)
                {
                    restarts++;
                    mu = initialMu;
                    runAgain = true;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < nParams; i++)
            {
                ZosVariableAccessor.SetVariableValue(system, variables[i], x[i]);
                variables[i].Value = x[i];
            }
            system.MFE.CalculateMeritFunction();
            cancellationToken.ThrowIfCancellationRequested();

            double finalMerit = Math.Sqrt(cost / SumWeights(meritRows));
            ValidateFinite(finalMerit, "final merit function");
            result.FinalMerit = finalMerit;
            result.Iterations = iterations;
            result.Success = true;
            result.Restarts = restarts;
            result.Message = $"Optimization completed ({iterations} iter{(effectiveMaxRestarts > 0 ? $", {restarts} restart{(restarts != 1 ? "s" : "")}" : "")}). Merit: {result.InitialMerit:F6} -> {result.FinalMerit:F6}";
            return result;
        }
        catch (OperationCanceledException)
        {
            RestoreValues(system, variables, x, nParams);
            throw;
        }
        catch (Exception ex)
        {
            RestoreValues(system, variables, x, nParams);
            double weightSum = meritRows != null ? SumWeights(meritRows) : 1.0;
            result.FinalMerit = weightSum > 0 ? Math.Sqrt(Math.Max(cost, 0) / weightSum) : 0;
            result.Iterations = iterations;
            result.Restarts = restarts;
            result.Success = false;
            result.Message = $"Optimization failed ({iterations} iter): {ex.Message}. Merit: {result.InitialMerit:F6} -> {result.FinalMerit:F6}";
            return result;
        }
    }

    private double[,] ComputeJacobian(
        IOpticalSystem system,
        List<OptVariable> variables,
        double[] x,
        double[] residuals,
        int nResiduals,
        int nParams,
        double delta,
        CancellationToken cancellationToken)
    {
        var jacobian = new double[nResiduals, nParams];
        for (int p = 0; p < nParams; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double original = x[p];
            double step = Math.Max(delta, Math.Abs(original) * delta);
            ZosVariableAccessor.SetVariableValue(system, variables[p], original + step);
            system.MFE.CalculateMeritFunction();
            cancellationToken.ThrowIfCancellationRequested();

            var perturbedRows = _meritReader.ReadMeritRows(system);
            if (perturbedRows.Count != nResiduals)
                throw new InvalidOperationException($"Merit-function residual count changed while computing the Jacobian ({nResiduals} -> {perturbedRows.Count}).");
            double[] perturbedResiduals = ComputeResiduals(perturbedRows);
            for (int i = 0; i < nResiduals; i++)
                jacobian[i, p] = (perturbedResiduals[i] - residuals[i]) / step;
            ZosVariableAccessor.SetVariableValue(system, variables[p], original);
        }
        return jacobian;
    }

    private static void RestoreValues(IOpticalSystem system, List<OptVariable> variables, double[]? x, int nParams)
    {
        if (x == null) return;
        try
        {
            for (int i = 0; i < nParams; i++)
            {
                ZosVariableAccessor.SetVariableValue(system, variables[i], x[i]);
                variables[i].Value = x[i];
            }
            system.MFE.CalculateMeritFunction();
        }
        catch { }
    }

    private static double[] ComputeResiduals(List<MeritRow> rows)
    {
        var residuals = new double[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Weight < 0 || double.IsNaN(rows[i].Weight) || double.IsInfinity(rows[i].Weight))
                throw new InvalidOperationException($"Merit row {rows[i].RowNumber} has invalid weight {rows[i].Weight}.");
            residuals[i] = Math.Sqrt(rows[i].Weight) * (rows[i].Value - rows[i].Target);
            ValidateFinite(residuals[i], $"residual for merit row {rows[i].RowNumber}");
        }
        return residuals;
    }

    private static double SumWeights(List<MeritRow> rows)
    {
        double sum = 0;
        for (int i = 0; i < rows.Count; i++) sum += rows[i].Weight;
        return sum > 0 ? sum : 1.0;
    }

    private static double DotProduct(double[] a, double[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    private static void ValidatePositive(int value, string name)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(name, $"{name} must be > 0.");
    }

    private static void ValidatePositiveFinite(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite and > 0.");
    }

    private static void ValidateNonNegativeFinite(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite and >= 0.");
    }

    private static void ValidateFinite(double value, string label)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidOperationException($"Constrained optimization produced a non-finite {label}.");
    }
}
