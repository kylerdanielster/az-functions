# CLAUDE.md

This file provides core guidance to Claude Code.

## Language & Toolchain

C# on .NET 10. Azure Functions v4 isolated worker model. SSH.NET for SFTP. Namespace: `AzFunctions`.

## Architecture

Two-app architecture for batch payment processing using HTTP + Storage Queue + callbacks:

**App 1: Coordinator** (`BatchCoordinator.cs`)
1. `RunDataFeed` (Timer) / `TriggerDataFeed` (HTTP) — generates 10 fake ACH payments via Bogus, creates batch + 10 payment entities in Table Storage (all fields stored), queries all Queued payments from Table Storage (picks up orphans from prior failed runs), POSTs entire batch to App 2 in one request, sets `Processing` status on successful submit
2. `BatchCompleted` (HTTP callback) — receives `BatchCallback(BatchId, Status)` callbacks at each stage, calls `UpdateBatchStatusAsync`. Logs warning on Error (TODO: send alert email), logs info on Processed (TODO: notify third party)
3. `GetBatchStatus` (HTTP GET) — returns batch + payment statuses from Table Storage
4. `ClearBatchData` (HTTP DELETE) — clears BatchTracking table (test cleanup)

**App 2: Batch Processor** (`BatchProcessor.cs` + `BatchOrchestration.cs`)
1. `ReceiveBatchRequest` (HTTP) — validates `BatchRequest` (BatchId, Payments, CallbackUrl), stores payment data in `BatchPayments` table via `IBatchPaymentStore`, enqueues lightweight `BatchQueueMessage` (BatchId only), returns 202
2. `ProcessBatchQueue` (Queue Trigger) — reads callback URL from `BatchPayments` table, starts Durable Functions orchestration with `BatchOrchestrationInput` (BatchId, CallbackUrl) and deterministic ID (`batch-{batchId}`)
3. `BatchOrchestration` (Orchestrator) — processes files **sequentially**: payment file first, then GL only if payment succeeds. Activities read payment data from `BatchPayments` table via `IBatchPaymentStore`. Callbacks for terminal states only:
   - Payment SFTP fails → callback `Error`, return early (no GL attempt)
   - GL SFTP succeeds → callback `Processed`
   - GL SFTP fails → queue to `gl-error-queue`, NO callback (App 1 stays in `Processing`)
4. `ProcessGLErrorQueue` (Queue Trigger) — logs failed GL uploads (TODO: error email, manual retry endpoint)

**Status flow**: `Queued` → _(App 1 sets)_ `Processing` → _(callback)_ `Processed` (success) or `Queued` → _(callback)_ `Error` (payment failure). GL failure leaves batch in `Processing` until manual retry.

**Batch tracking**: Azure Table Storage (`BatchTracking` table) via `IBatchTracker` / `TableBatchTracker`. Two entity levels: batch (PK: "batch", RK: batchId) and payment (PK: batchId, RK: paymentId, EntityType: "payment"). Each batch has 10 payment entities (pmt-000 through pmt-009) with all PaymentData fields stored. `UpdateBatchStatusAsync` handles status transitions — when terminal (Processed/Error), sets `CompletedAt` and bulk-updates all payment entities. Idempotent: skips if already terminal. Entity creation is idempotent (409 Conflict ignored). Batch statuses: `Queued`, `Processing`, `Processed`, `Error`.

**Batch payment data**: Azure Table Storage (`BatchPayments` table) via `IBatchPaymentStore` / `TableBatchPaymentStore`. Two entity levels: metadata (PK: "metadata", RK: batchId, stores CallbackUrl and PaymentCount) and payment (PK: batchId, RK: paymentId, EntityType: "payment", all PaymentData fields). Written by `ReceiveBatchRequest`, read by orchestration activities (`CreatePaymentFile`, `CreateGLFile`). Uses `BatchStorageConnection`.

**Storage**: Durable Functions and App 1's Table Storage use the `AzureWebJobsStorage` connection string. Application queues (`batch-processing-queue`, `gl-error-queue`) and the `BatchPayments` table use the `BatchStorageConnection` connection string, keeping App 2's application data separate from the function runtime's internal storage. Locally both point to Azurite. In production: App 1 uses a dedicated storage account for Table Storage (batch/payment tracking); App 2 uses a dedicated storage account for its queues, BatchPayments table, and the function app's built-in storage for Durable Functions state.

**SFTP**: SSH.NET (`Renci.SshNet`) via `ISftpClientFactory` / `SftpClientFactory`. Connections are async (`ConnectAsync`). Each activity creates a new connection — pooling is not feasible across Durable Functions activities. Local server via OpenSSH Docker container (port 2222).

**File layout**:
```
Program.cs                         Entry point and DI configuration
Models/
  Models.cs                        Shared records, DTOs, and BatchStatus constants
GM/                                App 1 — Coordinator
  BatchCoordinator.cs              Timer/HTTP triggers, batch status, callback webhook
  BatchTracking.cs                 IBatchTracker interface + TableBatchTracker implementation
Fin/                               App 2 — Batch Processor
  BatchProcessor.cs                HTTP receiver + queue triggers (entry points)
  BatchOrchestration.cs            Orchestrator, file creation/upload activities, callback, GL error queue
  BatchPaymentStore.cs             IBatchPaymentStore interface + TableBatchPaymentStore implementation
  SftpClientFactory.cs             ISftpClientFactory interface + SftpClientFactory implementation
  MessageQueue.cs                  IMessageQueue + IGLErrorQueue interfaces + implementations
  SftpEndpoints.cs                 SFTP test/debug endpoints (ListFiles, GetFile, DeleteAllFiles)
host.json                          Azure Functions, durable task, and queue config
docker-compose.yml                 Azurite + SFTP containers for local dev
test-batch-orchestration.sh        E2E test script
tests/AzFunctions.Tests/           xUnit + NSubstitute unit tests (see Testing section)
```

## DI Pattern

Use instance classes with constructor injection for Azure Function containers. The isolated worker model fully supports this pattern. Register dependencies in `Program.cs`. Keep orchestrator methods static (Durable Functions requirement) — activities and HTTP triggers are instance methods.

## AI Assistant Guidelines

### Context Awareness
- When implementing features, always check existing patterns first
- Use existing utilities before creating new ones
- Check for similar functionality in other functions/modules

### Common Pitfalls to Avoid
- Creating duplicate functionality
- Overwriting existing tests
- Modifying core frameworks without explicit instruction
- Adding dependencies without checking existing alternatives

## Important Notes

- **NEVER ASSUME OR GUESS** — When in doubt, ask for clarification
- **Always verify file paths and module names** before use
- **Keep CLAUDE.md updated** when adding new patterns or dependencies
- **Test your code** — No feature is complete without tests
- **Document your decisions** — Future developers (including yourself) will thank you

## Search Command Requirements

**CRITICAL**: Always use `rg` (ripgrep) instead of traditional `grep` and `find` commands:

```bash
# Don't use grep — use rg instead
rg "pattern"

# Don't use find -name — use rg --files instead
rg --files -g "*.cs"
```

## Build Commands

```bash
dotnet build          # build the main project
dotnet restore        # restore NuGet packages
func start            # run locally (requires Docker services)
docker compose up -d  # start Azurite + SFTP containers
```

## Testing

**Unit tests** (`tests/AzFunctions.Tests/`): xUnit + NSubstitute, targeting net10.0.

```bash
dotnet test tests/AzFunctions.Tests/  # run unit tests
dotnet test tests/AzFunctions.Tests/ --collect:"XPlat Code Coverage"  # with coverage
```

Test files:
- `ReceiveBatchRequestTests.cs` — input validation (null body, missing fields, empty payments, valid request, queue message content verification)
- `BatchCompletedTests.cs` — callback handling (Processed, Error, Processing callbacks, null request, tracker exception propagation)
- `GenerateBatchTests.cs` — batch submission (submit succeeds with payment entity creation, submit fails marks batch Error)
- `BatchOrchestrationTests.cs` — sequential flow (full success, payment failure stops GL, GL failure queues error, callback failure isolation, callback payload verification)
- `CreatePaymentFileTests.cs` — CSV generation (header, data formatting, escaping, empty list, amount formatting, sensitive field inclusion)
- `CreateGLFileTests.cs` — GL CSV generation (header, sensitive field exclusion, escaping, amount formatting)
- `GetBatchStatusTests.cs` — batch status endpoint (404 path, payment assembly/ordering, completed batch, error handling)
- `ProcessBatchQueueTests.cs` — queue trigger (valid message, null/malformed input, orchestration ID format)
- `ProcessGLErrorQueueTests.cs` — GL error queue trigger (valid message, null message, malformed input)
- `ClearBatchDataTests.cs` — clear endpoint (success with count, empty table, tracker exception)
- `SftpEndpointsTests.cs` — SFTP endpoints (path traversal rejection, connection failure handling)
- `SftpClientFactoryTests.cs` — env var validation (missing host/username/password, invalid port, default/custom remote path)
- `CsvEscapeTests.cs` — CSV escaping (null, empty, simple, comma, quotes, newline, mixed special chars)
- `Helpers/` — `FakeHttpRequestData`, `FakeHttpResponseData`, `FakeFunctionContext` for Azure Functions isolated worker model

Tests mock `IBatchTracker`, `IMessageQueue`, `IGLErrorQueue`, `ISftpClientFactory`, and `IHttpClientFactory`. `TableBatchTracker` is tested via E2E against Azurite (not unit-tested — `TableClient` has no interface).

**E2E tests**: `./test-batch-orchestration.sh` (requires Docker services + `func start`).

**Coverage**: Unit tests cover business logic callers (validation, error handling, callback processing). Infrastructure classes (`TableBatchTracker`, `SftpClientFactory`, `StorageQueueClient`, `GLErrorQueueClient`) are covered by E2E.

## Auto-Loaded Rules (`.claude/rules/`)

These load automatically based on context — no action needed:

- `dotnet-coding.md` — loaded when editing `.cs` files. Null safety, naming, Durable Functions patterns, logging.
- `testing.md` — loaded when working with test files. E2E test flow, verification steps.
- `git-workflow.md` — loaded for git operations. Branch naming, commit format, PR guidelines.

## Open TODOs

| Location | Description |
|----------|-------------|
| `BatchCoordinator.cs:45-52` | Callback failure resilience (Processed/Error only) — batch stuck in Processing if terminal callback fails all retries. Processing transition is handled locally. Needs reconciliation timer or timeout mechanism. |
| `BatchCoordinator.cs:82-83` | Notify third party when batch processing completes (Processed callback). |
| `BatchCoordinator.cs:87-88` | Send alert email when batch fails (Error callback). |
| `BatchProcessor.cs:115` | Send error email notification for GL upload failures. |
| `BatchProcessor.cs:116` | Implement manual retry endpoint for failed GL uploads. |

## Useful Resources

- TODO: As necessary
