using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows;

namespace AgentNet.Durable.Interop;

/// <summary>
/// Custom executor that wraps an async step function.
/// Uses the non-generic Executor base class with ConfigureRoutes.
/// </summary>
public class StepExecutor : Executor
{
    private readonly Func<object, Task<object>> _execute;

    public StepExecutor(string name, Func<object, Task<object>> execute)
        : base(name)
    {
        _execute = execute;
    }

    protected override RouteBuilder ConfigureRoutes(RouteBuilder routeBuilder)
    {
        return routeBuilder.AddHandler<object, object>(HandleInputAsync);
    }

    private async ValueTask<object> HandleInputAsync(object input, IWorkflowContext context, CancellationToken ct)
    {
        return await _execute(input);
    }
}

/// <summary>
/// Custom executor that runs multiple branches in parallel.
/// </summary>
public class ParallelExecutor : Executor
{
    private readonly IReadOnlyList<Func<object, Task<object>>> _branches;

    public ParallelExecutor(string name, IReadOnlyList<Func<object, Task<object>>> branches)
        : base(name)
    {
        _branches = branches;
    }

    protected override RouteBuilder ConfigureRoutes(RouteBuilder routeBuilder)
    {
        return routeBuilder.AddHandler<object, object>(HandleInputAsync);
    }

    private async ValueTask<object> HandleInputAsync(object input, IWorkflowContext context, CancellationToken ct)
    {
        var tasks = _branches.Select(b => b(input)).ToArray();
        var results = await System.Threading.Tasks.Task.WhenAll(tasks);
        return results.ToList();
    }
}

/// <summary>
/// Factory methods to create MAF executors from step functions.
/// </summary>
public static class ExecutorFactory
{
    /// <summary>
    /// Creates an executor that wraps an async step function.
    /// </summary>
    public static Executor CreateStep(
        string name,
        Func<object, Task<object>> execute)
    {
        return new StepExecutor(name, execute);
    }

    /// <summary>
    /// Creates an executor that runs multiple branches in parallel.
    /// </summary>
    public static Executor CreateParallel(
        string name,
        IReadOnlyList<Func<object, Task<object>>> branches)
    {
        return new ParallelExecutor(name, branches);
    }
}
