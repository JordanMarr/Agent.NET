using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows;
using Microsoft.DurableTask;

namespace AgentNet.Interop;

// ============================================================================
// DURABLE EXECUTOR MODEL
// ----------------------------------------------------------------------------
// These types are used for durable workflow execution where TaskOrchestrationContext
// must be passed at execution time (not captured in closures).
// ============================================================================

/// <summary>
/// Interface for durable workflow executors.
/// Executors receive TaskOrchestrationContext at execution time - they must NOT capture it.
/// </summary>
public interface IExecutor
{
    /// <summary>
    /// Unique identifier for this executor.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Executes the step with the given orchestration context and input.
    /// The context is passed at execution time to avoid capturing it in closures.
    /// </summary>
    Task<object?> ExecuteAsync(TaskOrchestrationContext ctx, object? input);
}

/// <summary>
/// Durable step executor using primary constructor pattern.
/// Does NOT capture TaskOrchestrationContext - receives it at execution time.
/// </summary>
public sealed class DurableStepExecutor(
    string id,
    Func<TaskOrchestrationContext, object?, Task<object?>> fn
) : IExecutor
{
    /// <inheritdoc/>
    public string Id { get; } = id;

    /// <inheritdoc/>
    public Task<object?> ExecuteAsync(TaskOrchestrationContext ctx, object? input)
        => fn(ctx, input);
}

// ============================================================================
// LEGACY MAF EXECUTOR MODEL
// ----------------------------------------------------------------------------
// These types are used for in-process workflow execution via MAF.
// They do NOT interact with DurableTask.
// ============================================================================

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

// ============================================================================
// EXECUTOR FACTORY
// ============================================================================

/// <summary>
/// Factory methods to create executors.
/// </summary>
public static class ExecutorFactory
{
    // ============ LEGACY MAF METHODS (for in-process execution) ============

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

    // ============ DURABLE EXECUTOR METHODS ============
    // These methods create IExecutor instances that receive ctx at execution time.
    // No executor captures TaskOrchestrationContext - all durable primitives are
    // invoked inside ExecuteAsync.

    /// <summary>
    /// Creates a step executor that runs a pure .NET function.
    /// The execute function should already be wrapped with WorkflowContext by F#.
    /// </summary>
    public static IExecutor CreateStepExecutor(
        string durableId,
        int stepIndex,
        Func<object?, Task<object?>> execute)
    {
        var id = $"{durableId}_{stepIndex}";

        return new DurableStepExecutor(
            id,
            async (ctx, input) => await execute(input));
    }

    /// <summary>
    /// Creates a parallel executor that runs multiple branches concurrently.
    /// Branch functions should already be wrapped with WorkflowContext by F#.
    /// </summary>
    public static IExecutor CreateParallelExecutor(
        string durableId,
        int stepIndex,
        IReadOnlyList<Func<object?, Task<object?>>> branches)
    {
        var id = $"{durableId}_{stepIndex}";

        return new DurableStepExecutor(
            id,
            async (ctx, input) =>
            {
                var tasks = branches.Select(b => b(input)).ToArray();
                await Task.WhenAll(tasks);
                return tasks.Select(t => t.Result).ToArray();
            });
    }

    /// <summary>
    /// Creates an executor that waits for an external event.
    /// The durable primitive is invoked inside ExecuteAsync, not in a closure.
    /// </summary>
    public static IExecutor CreateAwaitEventExecutor<TOutput>(
        string durableId,
        string eventName,
        int stepIndex)
    {
        var id = $"{durableId}_{stepIndex}";

        return new DurableStepExecutor(
            id,
            async (ctx, _) =>
            {
                var result = await ctx.WaitForExternalEvent<TOutput>(eventName);
                return (object?)result;
            });
    }

    /// <summary>
    /// Creates an executor that delays for a duration using a durable timer.
    /// The durable primitive is invoked inside ExecuteAsync, not in a closure.
    /// </summary>
    public static IExecutor CreateDelayExecutor(
        string durableId,
        TimeSpan duration,
        int stepIndex)
    {
        var id = $"{durableId}_{stepIndex}";

        return new DurableStepExecutor(
            id,
            async (ctx, input) =>
            {
                var fireAt = ctx.CurrentUtcDateTime.Add(duration);
                await ctx.CreateTimer(fireAt, CancellationToken.None);
                return input;
            });
    }

    /// <summary>
    /// Creates an executor with retry logic.
    /// The inner function should already be wrapped with WorkflowContext by F#.
    /// </summary>
    public static IExecutor CreateRetryExecutor(
        string durableId,
        int maxRetries,
        int stepIndex,
        Func<object?, Task<object?>> inner)
    {
        var id = $"{durableId}_{stepIndex}_retry{maxRetries}";

        return new DurableStepExecutor(
            id,
            async (ctx, input) =>
            {
                async Task<object?> Retry(int attempt)
                {
                    try
                    {
                        return await inner(input);
                    }
                    catch when (attempt < maxRetries)
                    {
                        return await Retry(attempt + 1);
                    }
                }

                return await Retry(0);
            });
    }

    /// <summary>
    /// Creates an executor with timeout logic using a durable timer.
    /// The durable timer is invoked inside ExecuteAsync, not in a closure.
    /// The inner function should already be wrapped with WorkflowContext by F#.
    /// </summary>
    public static IExecutor CreateTimeoutExecutor(
        string durableId,
        TimeSpan timeout,
        int stepIndex,
        Func<object?, Task<object?>> inner)
    {
        var id = $"{durableId}_{stepIndex}_timeout{(int)timeout.TotalMilliseconds}";

        return new DurableStepExecutor(
            id,
            async (ctx, input) =>
            {
                var fireAt = ctx.CurrentUtcDateTime.Add(timeout);
                var timerTask = ctx.CreateTimer(fireAt, CancellationToken.None);
                var stepTask = inner(input);

                var winner = await Task.WhenAny(stepTask, timerTask);

                if (ReferenceEquals(winner, timerTask))
                    throw new TimeoutException($"Step timed out after {timeout}");

                return await stepTask;
            });
    }

    /// <summary>
    /// Creates an executor with fallback logic.
    /// The inner and fallback functions should already be wrapped with WorkflowContext by F#.
    /// </summary>
    public static IExecutor CreateFallbackExecutor(
        string durableId,
        string fallbackId,
        int stepIndex,
        Func<object?, Task<object?>> inner,
        Func<object?, Task<object?>> fallback)
    {
        var id = $"{durableId}_{stepIndex}_fallback_{fallbackId}";

        return new DurableStepExecutor(
            id,
            async (ctx, input) =>
            {
                try
                {
                    return await inner(input);
                }
                catch
                {
                    return await fallback(input);
                }
            });
    }
}
