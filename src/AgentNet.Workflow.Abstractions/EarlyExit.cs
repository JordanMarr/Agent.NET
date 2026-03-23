namespace AgentNet.Interop;

// ============================================================================
// CROSS-RUNTIME EARLY-EXIT SEMANTICS
// ----------------------------------------------------------------------------
// Pure POCOs with no runtime dependencies.
// Used by both InProcess and Durable runtimes to signal early workflow exit.
// ============================================================================

/// <summary>
/// Signal to indicate early exit from an executor.
/// </summary>
/// <param name="error"></param>
public sealed class EarlyExitSignal(object error)
{
    /// <summary>
    /// The error object associated with the early exit.
    /// </summary>
    public object Error { get; } = error;
}

/// <summary>
/// Required for early return from durable executor.
/// </summary>
/// <param name="error"></param>
public class EarlyExitReturn(object error)
{
    /// <summary>
    /// The error object associated with the early exit.
    /// </summary>
    public object Error { get; } = error;
}

/// <summary>
/// Thrown from StepExecutor to prevent further execution.
/// </summary>
/// <param name="error"></param>
public class EarlyExitException(object error) : Exception("Executor signaled early exit")
{
    /// <summary>
    /// The error object associated with the early exit.
    /// </summary>
    public object Error { get; } = error;
}
