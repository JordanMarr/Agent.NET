/// Durable OCR sample.
///
/// Models a "download PDF -> send to OCR -> await callback -> store" pipeline and shows MAF-native
/// durable suspend/resume: the workflow checkpoints to disk and SUSPENDS at the awaitEvent until the
/// (here, simulated) OCR service calls back. In production the process could exit at the suspension,
/// and a *different* process would resume the run when the callback arrives — that's the whole point.
///
/// Run with: dotnet run
module Samples.DurableOcr.Program

open System
open System.IO
open AgentNet
open AgentNet.InProcess

// ---- Domain ----
type PdfRef = { Project: string; FileId: string }
type OcrResult = { Text: string; IsValid: bool }   // public: awaitEvent payload types must be public

// ---- Steps ----

/// Download the PDF (simulated).  PdfRef -> byte[]
let downloadPdf (pdf: PdfRef) =
    task {
        printfn $"  [download] fetching {pdf.FileId} from project {pdf.Project}"
        return System.Text.Encoding.UTF8.GetBytes "%PDF-1.4 (pretend bytes)"
    }

/// Fire the OCR request (simulated).  byte[] -> unit
/// Returns unit ON PURPOSE: this step FIRES the async request and yields the process — it does not
/// compute the result. It hands the OCR service ctx.CorrelationId so the service's later callback can
/// resume *this* run. (If it returned OcrResult, it would be a normal held-in-process step instead.)
let requestOcr =
    Executor.create "requestOcr" (fun (bytes: byte[]) (ctx: WorkflowContext) ->
        task {
            printfn $"  [ocr] sent {bytes.Length} bytes; OCR will call back with correlationId={ctx.CorrelationId}"
            return ()
        })

/// Stands in for a database (a unique key would do this for real). See storeResult.
let private storedCorrelations = System.Collections.Concurrent.ConcurrentDictionary<string, byte>()

/// Store the parsed result (simulated).  OcrResult -> string
///
/// IMPORTANT: durable resume delivers the step *after* an awaitEvent **at-least-once** — MAF may
/// re-execute it when the run resumes (just like a Durable Functions activity). So any side-effecting
/// step here MUST be idempotent. We dedup on the run's correlation id; in production this is an upsert
/// or a unique-key write in your store.
let storeResult =
    Executor.create "storeResult" (fun (result: OcrResult) (ctx: WorkflowContext) ->
        task {
            if storedCorrelations.TryAdd(ctx.CorrelationId, 0uy) then
                printfn $"  [store] persisting parsed text (valid={result.IsValid}): \"{result.Text}\""
            else
                printfn $"  [store] idempotent no-op (already stored for {ctx.CorrelationId})"
            return (if result.IsValid then "stored" else "flagged-invalid")
        })

// ---- Workflow: the honest, explicit shape ----
let ocrWorkflow =
    workflow {
        name "PdfOcr"
        step downloadPdf                              // PdfRef    -> byte[]
        step requestOcr                               // byte[]    -> unit       (fire; yields the process)
        awaitEvent "OcrComplete" eventOf<OcrResult>   // unit      -> OcrResult   (SUSPEND until callback)
        step storeResult                              // OcrResult -> string
    }

[<EntryPoint>]
let main _ =
    let dir = Path.Combine(Path.GetTempPath(), "agentnet-ocr-sample")
    if Directory.Exists dir then Directory.Delete(dir, true)
    let cm = Workflow.Durable.fileSystemJsonCheckpoints dir

    // In production, derive the session id from the inbound event id (idempotency + correlation).
    let sessionId = "doc-" + Guid.NewGuid().ToString("N").Substring(0, 8)
    let pdf = { Project = "ACC-123"; FileId = "drawing-A101.pdf" }

    printfn $"START    session={sessionId}"
    match (Workflow.Durable.start cm sessionId pdf ocrWorkflow).GetAwaiter().GetResult() with
    | Workflow.Durable.Completed output ->
        printfn $"completed without suspending: {output}"
        0
    | Workflow.Durable.Suspended (awaiting, checkpoint) ->
        let events = awaiting |> List.map (fun r -> r.EventName) |> String.concat ", "
        printfn $"SUSPEND  awaiting [{events}] — checkpoint persisted under {dir}"
        printfn  "         (the process could exit here; a fresh process would resume when OCR calls back)"
        printfn ""

        // --- Simulate the OCR service calling back later, as if in a brand-new process ---
        printfn $"CALLBACK OCR finished for correlationId={sessionId}"
        let respond (req: PendingRequest) : obj option =
            if req.EventName = "OcrComplete"
            then Some (box { Text = "Sheet A-101 — Ground Floor Plan"; IsValid = true })
            else None

        match (Workflow.Durable.resume cm ocrWorkflow checkpoint respond).GetAwaiter().GetResult() with
        | Workflow.Durable.Completed output ->
            printfn $"RESUME   -> completed: {output}"
            0
        | Workflow.Durable.Suspended _ ->
            printfn "unexpectedly still suspended"
            1
