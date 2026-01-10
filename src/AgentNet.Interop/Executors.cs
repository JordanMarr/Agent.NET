using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows;

namespace AgentNet.Interop;

/// <summary>
/// Custom executor that wraps an async step function.
/// Uses AddCatchAll to handle any input type.
/// </summary>
public class StepExecutor(string name, Func<object, Task<object>> execute) : Executor(name)
{
    /// <inheritdoc/>
    protected override RouteBuilder ConfigureRoutes(RouteBuilder routeBuilder)
    {
        // Use AddCatchAll to catch any input type
        return routeBuilder.AddCatchAll(HandleInputAsync);
    }

    private async ValueTask<object?> HandleInputAsync(PortableValue input, IWorkflowContext context, CancellationToken ct)
    {
        // Extract the actual value from PortableValue using As<object>()
        var actualInput = input.As<object>();
        if (actualInput is null)
        {
            throw new ArgumentNullException(nameof(input), "StepExecutor received null input");
        }
        var result = await execute(actualInput);
        return result;
    }
}

/// <summary>
/// Custom executor that runs multiple branches in parallel.
/// </summary>
public class ParallelExecutor(string name, IReadOnlyList<Func<object, Task<object>>> branches) : Executor(name)
{
    /// <inheritdoc/>
    protected override RouteBuilder ConfigureRoutes(RouteBuilder routeBuilder)
    {
        // Use AddCatchAll to catch any input type
        return routeBuilder.AddCatchAll(HandleInputAsync);
    }

    private async ValueTask<object?> HandleInputAsync(PortableValue input, IWorkflowContext context, CancellationToken ct)
    {
        // Extract the actual value from PortableValue using As<object>()
        var actualInput = input.As<object>();
        if (actualInput is null)
        {
            throw new ArgumentNullException(nameof(input), "ParallelExecutor received null input");
        }
        var tasks = branches.Select(b => b(actualInput)).ToArray();
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
