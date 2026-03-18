# CLAUDE.md

This file provides core guidance to Claude Code.

## Language & Toolchain

C# on .NET 10. Azure Functions v4 isolated worker model. SSH.NET for SFTP. Namespace: `AzFunctions`.

## Architecture

Two-app architecture for batch payment processing using HTTP + Storage Queue + callback:

**App 1: Coordinator** (`SftpDataFeed.cs`)
1. `RunDataFeed` (Timer) / `TriggerDataFeed` (HTTP) — generates 10 fake ACH payments via Bogus, creates batch + 2 file entities + 10 payment entities in Table Storage, POSTs entire batch to App 2 in one request
2. `BatchCompleted` (HTTP callback) — receives single completion callback with per-file results, updates file entities, payment entities, and batch status via `CompleteBatchFromResultsAsync`. Logs warning on file failures (TODO: send alert email to users)
3. `GetBatchStatus` (HTTP GET) — returns batch + file + payment statuses from Table Storage
4. `ClearBatchData` (HTTP DELETE) — clears BatchTracking table (test cleanup)

**App 2: SFTP Processor** (`SftpProcessor.cs` + `SftpOrchestration.cs`)
1. `ReceiveSftpRequest` (HTTP) — validates `SftpBatchRequest` (BatchId, Payments, CallbackUrl), drops onto Storage Queue via `IMessageQueue`, returns 202
2. `ProcessSftpQueue` (Queue Trigger) — starts Durable Functions orchestration with deterministic ID (`sftp-{batchId}`)
3. `SftpOrchestration` (Orchestrator) — generates payment + GL CSV files from all batch payments in parallel, uploads each to SFTP from memory with retry (per-file try/catch), sends callback with `List<FileResult>`. Output files: `payment_{batchId}.csv` (full payment details) and `gl_{batchId}.csv` (omits sensitive banking fields). Callback failure is isolated — files stay uploaded even if callback fails

**Batch tracking**: Azure Table Storage (`BatchTracking` table) via `IBatchTracker` / `TableBatchTracker`. Three entity levels: batch (PK: "batch", RK: batchId), file (PK: batchId, RK: fileType, EntityType: "file"), and payment (PK: batchId, RK: paymentId, EntityType: "payment"). Each batch has exactly 2 file entities (payment, gl) and 10 payment entities (pmt-000 through pmt-009). Single callback per batch updates all entities via `CompleteBatchFromResultsAsync`. Batch status derived from file results via pattern match: `Processed` (both succeed), `PaymentFileFailed` (payment fails), `GLFileFailed` (GL fails), `Failed` (both fail). Entity creation is idempotent (409 Conflict ignored).

**Storage**: All services (Table Storage, Queue, Durable Functions state) use the single `AzureWebJobsStorage` connection string. Locally → Azurite. In production → dedicated storage account separate from the function app's built-in storage.

**SFTP**: SSH.NET (`Renci.SshNet`) via `ISftpClientFactory` / `SftpClientFactory`. Connections are async (`ConnectAsync`). Each activity creates a new connection — pooling is not feasible across Durable Functions activities. Local server via OpenSSH Docker container (port 2222).

**File layout**:
```
Program.cs                    Entry point and DI configuration
Models.cs                     Shared records, DTOs, and BatchStatus constants
BatchTracking.cs              IBatchTracker interface + TableBatchTracker implementation
SftpClientFactory.cs          ISftpClientFactory interface + SftpClientFactory implementation
MessageQueue.cs               IMessageQueue interface + StorageQueueClient implementation
SftpProcessor.cs              HTTP receiver + queue trigger (App 2 entry points)
SftpOrchestration.cs          Orchestrator, file creation/upload activities, callback, SFTP endpoints
SftpDataFeed.cs               Timer/HTTP triggers, batch status, callback webhook (App 1)
host.json                     Azure Functions, durable task, and queue config
docker-compose.yml            Azurite + SFTP containers for local dev
test-sftp-orchestration.sh    E2E test script
test-sftp-retry.sh            Stale — references non-existent endpoints from a previous architecture
tests/AzFunctions.Tests/      xUnit + NSubstitute unit tests (see Testing section)
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
- `ReceiveSftpRequestTests.cs` — input validation (null body, missing fields, empty payments, valid request)
- `BatchCompletedTests.cs` — callback handling (valid callback, null request, GL file failure)
- `GenerateBatchTests.cs` — batch submission (submit succeeds with payment entity creation, submit fails marks files failed)
- `SftpOrchestrationTests.cs` — orchestrator error isolation (callback failure, single file failure)
- `Helpers/` — `FakeHttpRequestData`, `FakeHttpResponseData`, `FakeFunctionContext` for Azure Functions isolated worker model

Tests mock `IBatchTracker`, `IMessageQueue`, `ISftpClientFactory`, and `IHttpClientFactory`. `TableBatchTracker` is tested via E2E against Azurite (not unit-tested — `TableClient` has no interface).

**E2E tests**: `./test-sftp-orchestration.sh` (requires Docker services + `func start`).

**Coverage**: Unit tests cover business logic callers (validation, error handling, callback processing). Infrastructure classes (`TableBatchTracker`, `SftpClientFactory`, `StorageQueueClient`) are covered by E2E.

## Auto-Loaded Rules (`.claude/rules/`)

These load automatically based on context — no action needed:

- `dotnet-coding.md` — loaded when editing `.cs` files. Null safety, naming, Durable Functions patterns, logging.
- `testing.md` — loaded when working with test files. E2E test flow, verification steps.
- `git-workflow.md` — loaded for git operations. Branch naming, commit format, PR guidelines.

## Useful Resources

- TODO: As necessary
