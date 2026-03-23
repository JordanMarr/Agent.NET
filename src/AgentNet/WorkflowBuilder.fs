namespace AgentNet

open System
open System.Threading.Tasks

/// Builder for the workflow computation expression
type WorkflowBuilder() =

    // ============ CE CORE MEMBERS (per DESIGN_CE_TYPE_THREADING.md) ============
    // These members implement the spec-compliant type threading invariants.
    // No member may use wildcards (_) or erased types (obj) in WorkflowState.

    /// Identity workflow - produces input unchanged, with no error type (unit)
    member _.Zero() : WorkflowState<'input, 'input, unit> = { Name = None; PackedSteps = [] }

    /// Yields a value - for CE compatibility (workflow uses Zero primarily)
    member _.Yield(_: unit) : WorkflowState<'input, 'input, unit> = { Name = None; PackedSteps = [] }

    /// Delays evaluation - preserves phantom types exactly
    member _.Delay(f: unit -> WorkflowState<'input, 'a, 'e>) : WorkflowState<'input, 'a, 'e> = f()

    /// Combines two workflow states - output type comes from second state, error type is preserved
    member _.Combine(s1: WorkflowState<'input, 'a, 'error>, s2: WorkflowState<'input, 'b, 'error>) : WorkflowState<'input, 'b, 'error> =
        { Name = s2.Name |> Option.orElse s1.Name; PackedSteps = s1.PackedSteps @ s2.PackedSteps }

    /// Sets the name of the workflow (used for MAF compilation and durable function registration)
    [<CustomOperation("name")>]
    member _.Name(state: WorkflowState<'input, 'output, 'error>, name: string) : WorkflowState<'input, 'output, 'error> =
        { state with Name = Some name }

    // ============ STEP OPERATIONS ============
    // Type threading invariant: WorkflowState<'input,'a,'e> -> Executor<'a,'b> -> WorkflowState<'input,'b,'e>
    // Uses inline SRTP to accept Task fn, Async fn, TypedAgent, Executor, Workflow, or Step directly.
    // All operations create TypedWorkflowStep nodes and pack them for storage.
    // Durable IDs are auto-generated; display names come from Executor.Name if available.
    // NO WILDCARDS OR ERASED TYPES - enforced by single Step member with Zero providing initial state.

    /// Adds a step to the workflow - threads type through from 'a to 'b (uses SRTP for type resolution).
    /// For the first step, 'a unifies with 'input from Zero().
    /// Does NOT change the error type.
    /// INVARIANT: WorkflowState<'input,'a,'e> + Step<'a,'b> -> WorkflowState<'input,'b,'e>
    [<CustomOperation("step")>]
    member inline _.Step(state: WorkflowState<'input, 'a, 'error>, x: ^T) : WorkflowState<'input, 'b, 'error> =
        let step : Step<'a, 'b> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'a, 'b>) (StepConv, x))
        let durableId = WorkflowInternal.getDurableId step
        let displayName = WorkflowInternal.getDisplayName step
        WorkflowInternal.warnIfLambda step durableId
        let typedStep = WorkflowInternal.toTypedStep durableId displayName step
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    /// First tryStep: workflow has no error type yet ('error = unit).
    /// This call sets the workflow's error type to 'eStep.
    [<CustomOperation("tryStep")>]
    member inline _.TryStep
        (state: WorkflowState<'input,'a, unit>, x: ^T)
            : WorkflowState<'input,'b,'eStep> =

        // Convert x to Step<'a, Result<'b,'eStep>>
        let step : Step<'a, Result<'b,'eStep>> =
            ((^T or StepConv) :
                (static member ToStep : StepConv * ^T -> Step<'a, Result<'b,'eStep>>)
                    (StepConv, x))

        let durableId = WorkflowInternal.getDurableId step
        let displayName = WorkflowInternal.getDisplayName step
        WorkflowInternal.warnIfLambda step durableId

        // Pack into TypedWorkflowStep.TryStep using your existing logic
        let typedStep =
            match step with
            | NestedWorkflow wf ->
                TypedWorkflowStep.TryStep (durableId, displayName, fun input ctx -> task {
                    let mutable current: obj = box input
                    for packedStep in wf.TypedSteps do
                        let! result = packedStep.ExecuteInProcess current ctx
                        current <- result
                    let typedResult = current :?> Result<'b,'eStep>
                    match typedResult with
                    | Ok v -> return Ok v
                    | Error e -> return Error (box e)
                })
            | _ ->
                let exec = WorkflowInternal.stepToExecutor displayName step
                TypedWorkflowStep.TryStep (durableId, displayName, fun input ctx -> task {
                    let! typedResult = exec.Execute input ctx
                    match typedResult with
                    | Ok v -> return Ok v
                    | Error e -> return Error (box e)
                })

        { Name = state.Name
          PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    /// Subsequent tryStep: workflow already has an error type 'eExisting.
    /// This call enforces that the step must return Result<'b,'eExisting>.
    [<CustomOperation("tryStep")>]
    member inline _.TryStep
        (state: WorkflowState<'input,'a,'eExisting>, x: ^T)
            : WorkflowState<'input,'b,'eExisting> =

        // Convert x to Step<'a, Result<'b,'eExisting>>
        let step : Step<'a, Result<'b,'eExisting>> =
            ((^T or StepConv) :
                (static member ToStep : StepConv * ^T -> Step<'a, Result<'b,'eExisting>>)
                    (StepConv, x))

        let durableId = WorkflowInternal.getDurableId step
        let displayName = WorkflowInternal.getDisplayName step
        WorkflowInternal.warnIfLambda step durableId

        // Pack into TypedWorkflowStep.TryStep using your existing logic
        let typedStep =
            match step with
            | NestedWorkflow wf ->
                TypedWorkflowStep.TryStep (durableId, displayName, fun input ctx -> task {
                    let mutable current: obj = box input
                    for packedStep in wf.TypedSteps do
                        let! result = packedStep.ExecuteInProcess current ctx
                        current <- result
                    let typedResult = current :?> Result<'b,'eExisting>
                    match typedResult with
                    | Ok v -> return Ok v
                    | Error e -> return Error (box e)
                })
            | _ ->
                let exec = WorkflowInternal.stepToExecutor displayName step
                TypedWorkflowStep.TryStep (durableId, displayName, fun input ctx -> task {
                    let! typedResult = exec.Execute input ctx
                    match typedResult with
                    | Ok v -> return Ok v
                    | Error e -> return Error (box e)
                })

        { Name = state.Name
          PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    // ============ ROUTING ============

    /// Routes to different executors based on the previous step's output.
    /// Uses SRTP to accept Task fn, Async fn, TypedAgent, Executor, or Step directly.
    /// Durable IDs are auto-generated for each branch.
    /// Does NOT change the error type.
    [<CustomOperation("route")>]
    member inline _.Route(state: WorkflowState<'input, 'middle, 'error>, router: 'middle -> ^T) : WorkflowState<'input, 'output, 'error> =
        // Generate a durable ID for the route decision point based on input/output types
        let routeDurableId = DurableId.forRoute router
        // Create a typed router that wraps the user's routing function
        let typedRouterFn = fun (input: 'middle) (ctx: WorkflowContext) ->
            let result = router input
            let step : Step<'middle, 'output> =
                ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'middle, 'output>) (StepConv, result))
            let displayName = WorkflowInternal.getDisplayName step
            WorkflowInternal.warnIfLambda step (WorkflowInternal.getDurableId step)
            let exec = WorkflowInternal.stepToExecutor displayName step
            exec.Execute input ctx
        let typedStep = TypedWorkflowStep.Route (routeDurableId, typedRouterFn)
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    // ============ FANOUT OPERATIONS ============
    // SRTP overloads for 2-5 arguments - no wrapper needed!
    // For 6+ branches, use: fanOut [+fn1; +fn2; ...]
    // Durable IDs are auto-generated for each parallel branch
    // Does NOT change the error type.

    /// Runs 2 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle, 'error>, x1: ^A, x2: ^B) : WorkflowState<'input, 'o list, 'error> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let typedStep = WorkflowInternal.toTypedStepParallel [s1; s2]
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    /// Runs 3 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle, 'error>, x1: ^A, x2: ^B, x3: ^C) : WorkflowState<'input, 'o list, 'error> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        let typedStep = WorkflowInternal.toTypedStepParallel [s1; s2; s3]
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    /// Runs 4 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle, 'error>, x1: ^A, x2: ^B, x3: ^C, x4: ^D) : WorkflowState<'input, 'o list, 'error> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        let s4 : Step<'middle, 'o> = ((^D or StepConv) : (static member ToStep: StepConv * ^D -> Step<'middle, 'o>) (StepConv, x4))
        let typedStep = WorkflowInternal.toTypedStepParallel [s1; s2; s3; s4]
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    /// Runs 5 steps in parallel (fan-out) - SRTP resolves each argument type
    [<CustomOperation("fanOut")>]
    member inline _.FanOut(state: WorkflowState<'input, 'middle, 'error>, x1: ^A, x2: ^B, x3: ^C, x4: ^D, x5: ^E) : WorkflowState<'input, 'o list, 'error> =
        let s1 : Step<'middle, 'o> = ((^A or StepConv) : (static member ToStep: StepConv * ^A -> Step<'middle, 'o>) (StepConv, x1))
        let s2 : Step<'middle, 'o> = ((^B or StepConv) : (static member ToStep: StepConv * ^B -> Step<'middle, 'o>) (StepConv, x2))
        let s3 : Step<'middle, 'o> = ((^C or StepConv) : (static member ToStep: StepConv * ^C -> Step<'middle, 'o>) (StepConv, x3))
        let s4 : Step<'middle, 'o> = ((^D or StepConv) : (static member ToStep: StepConv * ^D -> Step<'middle, 'o>) (StepConv, x4))
        let s5 : Step<'middle, 'o> = ((^E or StepConv) : (static member ToStep: StepConv * ^E -> Step<'middle, 'o>) (StepConv, x5))
        let typedStep = WorkflowInternal.toTypedStepParallel [s1; s2; s3; s4; s5]
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    /// Runs multiple steps in parallel (fan-out) - for 6+ branches, use '+' operator
    [<CustomOperation("fanOut")>]
    member _.FanOut(state: WorkflowState<'input, 'middle, 'error>, steps: Step<'middle, 'o> list) : WorkflowState<'input, 'o list, 'error> =
        let typedStep = WorkflowInternal.toTypedStepParallel steps
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    // ============ FANIN OPERATIONS ============
    // Uses inline SRTP to accept Task fn, Async fn, TypedAgent, Executor, or Step directly
    // Durable IDs are auto-generated
    // Does NOT change the error type.

    /// Aggregates parallel results with any supported step type (uses SRTP for type resolution)
    [<CustomOperation("fanIn")>]
    member inline _.FanIn(state: WorkflowState<'input, 'elem list, 'error>, x: ^T) : WorkflowState<'input, 'output, 'error> =
        let step : Step<'elem list, 'output> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'elem list, 'output>) (StepConv, x))
        let durableId = WorkflowInternal.getDurableId step
        let displayName = WorkflowInternal.getDisplayName step
        WorkflowInternal.warnIfLambda step durableId
        let typedStep = WorkflowInternal.toTypedStepFanIn durableId displayName step
        { Name = state.Name; PackedSteps = state.PackedSteps @ [PackedTypedStep.pack typedStep] }

    // ============ RESILIENCE OPERATIONS ============
    // These wrap the preceding step with retry, timeout, or fallback behavior
    // Resilience wrappers create new packed steps that wrap the inner step's execution
    // Does NOT change the error type.

    /// Wraps the previous step with retry logic. On failure, retries up to maxRetries times.
    [<CustomOperation("retry")>]
    member _.Retry(state: WorkflowState<'input, 'output, 'error>, maxRetries: int) : WorkflowState<'input, 'output, 'error> =
        match state.PackedSteps with
        | [] -> failwith "retry requires a preceding step"
        | packedSteps ->
            let allButLast = packedSteps |> List.take (packedSteps.Length - 1)
            let innerPacked = packedSteps |> List.last
            // Create a wrapped packed step with retry logic
            let wrappedPacked = {
                DurableId = $"WithRetry_{innerPacked.DurableId}"
                Name = $"Retry({maxRetries})"
                Kind = Resilience (Retry maxRetries)
                OutputType = innerPacked.OutputType
                ExecuteInProcess = fun input ctx ->
                    let rec retry attempt =
                        task {
                            try
                                return! innerPacked.ExecuteInProcess input ctx
                            with _ when attempt < maxRetries ->
                                return! retry (attempt + 1)
                        }
                    retry 0
                CreateDurableExecutor = fun stepIndex ->
                    let innerFunc = Func<obj, Task<obj>>(fun input ->
                        let ctx = WorkflowContext.create()
                        innerPacked.ExecuteInProcess input ctx)
                    Interop.DurableExecutorFactory.CreateRetryExecutor($"Retry_{stepIndex}", maxRetries, stepIndex, innerFunc)
                ActivityInfo = innerPacked.ActivityInfo
                InnerStep = Some innerPacked
                FallbackStep = None
            }
            { Name = state.Name; PackedSteps = allButLast @ [wrappedPacked] }

    /// Wraps the previous step with a timeout. Fails with TimeoutException if duration exceeded.
    [<CustomOperation("timeout")>]
    member _.Timeout(state: WorkflowState<'input, 'output, 'error>, duration: TimeSpan) : WorkflowState<'input, 'output, 'error> =
        match state.PackedSteps with
        | [] -> failwith "timeout requires a preceding step"
        | packedSteps ->
            let allButLast = packedSteps |> List.take (packedSteps.Length - 1)
            let innerPacked = packedSteps |> List.last
            // Create a wrapped packed step with timeout logic
            let wrappedPacked = {
                DurableId = $"WithTimeout_{innerPacked.DurableId}"
                Name = $"Timeout({duration})"
                Kind = Resilience (Timeout duration)
                OutputType = innerPacked.OutputType
                ExecuteInProcess = fun input ctx ->
                    task {
                        let execution = innerPacked.ExecuteInProcess input ctx
                        let timeoutTask = Task.Delay(duration)
                        let! winner = Task.WhenAny(execution, timeoutTask)
                        if obj.ReferenceEquals(winner, timeoutTask) then
                            return raise (TimeoutException($"Step timed out after {duration}"))
                        else
                            return! execution
                    }
                CreateDurableExecutor = fun stepIndex ->
                    let innerFunc = Func<obj, Task<obj>>(fun input ->
                        let ctx = WorkflowContext.create()
                        innerPacked.ExecuteInProcess input ctx)
                    Interop.DurableExecutorFactory.CreateTimeoutExecutor($"Timeout_{stepIndex}", duration, stepIndex, innerFunc)
                ActivityInfo = innerPacked.ActivityInfo
                InnerStep = Some innerPacked
                FallbackStep = None
            }
            { Name = state.Name; PackedSteps = allButLast @ [wrappedPacked] }

    /// Wraps the previous step with a fallback. On failure, executes the fallback step instead.
    [<CustomOperation("fallback")>]
    member inline _.Fallback(state: WorkflowState<'input, 'output, 'error>, x: ^T) : WorkflowState<'input, 'output, 'error> =
        let step : Step<'output, 'output> = ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'output, 'output>) (StepConv, x))
        let durableId = WorkflowInternal.getDurableId step
        let displayName = WorkflowInternal.getDisplayName step
        WorkflowInternal.warnIfLambda step durableId
        // Create a typed step for the fallback and pack it
        let fallbackTypedStep = WorkflowInternal.toTypedStep durableId displayName step
        let fallbackPacked = PackedTypedStep.pack fallbackTypedStep
        match state.PackedSteps with
        | [] -> failwith "fallback requires a preceding step"
        | packedSteps ->
            let allButLast = packedSteps |> List.take (packedSteps.Length - 1)
            let innerPacked = packedSteps |> List.last
            // Create a wrapped packed step with fallback logic
            let wrappedPacked = {
                DurableId = $"WithFallback_{innerPacked.DurableId}"
                Name = $"Fallback({durableId})"
                Kind = Resilience (Fallback durableId)
                OutputType = innerPacked.OutputType
                ExecuteInProcess = fun input ctx ->
                    task {
                        try
                            return! innerPacked.ExecuteInProcess input ctx
                        with _ ->
                            return! fallbackPacked.ExecuteInProcess input ctx
                    }
                CreateDurableExecutor = fun stepIndex ->
                    let innerFunc = Func<obj, Task<obj>>(fun input ->
                        let ctx = WorkflowContext.create()
                        innerPacked.ExecuteInProcess input ctx)
                    let fallbackFunc = Func<obj, Task<obj>>(fun input ->
                        let ctx = WorkflowContext.create()
                        fallbackPacked.ExecuteInProcess input ctx)
                    Interop.DurableExecutorFactory.CreateFallbackExecutor($"Fallback_{stepIndex}", durableId, stepIndex, innerFunc, fallbackFunc)
                ActivityInfo = innerPacked.ActivityInfo
                InnerStep = Some innerPacked
                FallbackStep = Some fallbackPacked
            }
            { Name = state.Name; PackedSteps = allButLast @ [wrappedPacked] }

    /// Builds the final workflow definition
    member _.Run(state: WorkflowState<'input, 'output, 'error>) : WorkflowDef<'input, 'output, 'error> =
        { Name = state.Name; TypedSteps = state.PackedSteps }



/// The workflow computation expression builder instance
[<AutoOpen>]
module WorkflowCE =
    let workflow = WorkflowBuilder()

    /// Prefix operator to convert any supported type to Step<'i, 'o>.
    /// Supports: Task fn, Async fn, TypedAgent, Executor, or Step passthrough.
    /// Used for fanOut lists with 6+ branches: fanOut [+fn1; +fn2; +fn3; +fn4; +fn5; +fn6]
    let inline (~+) (x: ^T) : Step<'i, 'o> =
        ((^T or StepConv) : (static member ToStep: StepConv * ^T -> Step<'i, 'o>) (StepConv, x))
