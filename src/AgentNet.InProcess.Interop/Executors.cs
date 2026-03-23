using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows;

namespace AgentNet.Interop;

// ============================================================================
// IN-PROCESS EARLY-EXIT EVENT
// ============================================================================

/// <summary>
/// Event indicating early exit from an executor due to an F# Result type Error.
/// </summary>
public class ExecutorEarlyExitEvent(string executorId, object error)
    : ExecutorEvent(executorId, error)
{
    /// <summary>
    /// The error object associated with the early exit.
    /// This is already exposed via base.Data, but we add a typed property for convenience.
    /// </summary>
    public object Error { get; } = error;
}

// ============================================================================
// IN-PROCESS MAF EXECUTOR MODEL
// ============================================================================

/// <summary>
/// Custom executor that wraps an async step function.
/// Uses AddCatchAll to handle any input type.
/// </summary>
public class StepExecutor(string name, Func<object, Task<object>> execute, Type? outputType = null) : Executor(name)
{
    /// <inheritdoc/>
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        protocolBuilder.RouteBuilder.AddCatchAll(HandleInputAsync);
        if (outputType is not null)
        {
            protocolBuilder.SendsMessageType(outputType);
            protocolBuilder.YieldsOutputType(outputType);
        }
        return protocolBuilder;
    }

    private async ValueTask<object?> HandleInputAsync(PortableValue input, IWorkflowContext context, CancellationToken ct)
    {
        var actualInput = input.As<object>();
        if (actualInput is null)
        {
            throw new ArgumentNullException(nameof(input), "StepExecutor received null input");
        }
        var result = await execute(actualInput);

        if (result is EarlyExitSignal signal)
        {
            await context.AddEventAsync(new ExecutorEarlyExitEvent(this.Id, signal.Error), ct);
            throw new EarlyExitException(signal.Error);
        }

        return result;
    }
}

/// <summary>
/// Custom executor that runs multiple branches in parallel.
/// </summary>
public class ParallelExecutor(string name, IReadOnlyList<Func<object, Task<object>>> branches, Type? outputType = null) : Executor(name)
{
    /// <inheritdoc/>
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
    {
        protocolBuilder.RouteBuilder.AddCatchAll(HandleInputAsync);
        if (outputType is not null)
        {
            protocolBuilder.SendsMessageType(outputType);
            protocolBuilder.YieldsOutputType(outputType);
        }
        return protocolBuilder;
    }

    private async ValueTask<object?> HandleInputAsync(PortableValue input, IWorkflowContext context, CancellationToken ct)
    {
        var actualInput = input.As<object>();
        if (actualInput is null)
        {
            throw new ArgumentNullException(nameof(input), "ParallelExecutor received null input");
        }

        var tasks = branches.Select(b => b(actualInput)).ToArray();
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }
}

// ============================================================================
// IN-PROCESS EXECUTOR FACTORY
// ============================================================================

/// <summary>
/// Factory methods to create in-process MAF executors.
/// </summary>
public static class ExecutorFactory
{
    /// <summary>
    /// Creates an executor that wraps an async step function.
    /// </summary>
    public static Executor CreateStep(
        string name,
        Func<object, Task<object>> execute,
        Type? outputType = null)
    {
        return new StepExecutor(name, execute, outputType);
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
