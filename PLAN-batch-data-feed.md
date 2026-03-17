# Plan: Batch Data Feed with Queue + Durable Functions + Callback

## Context

The SftpDataFeed needs to handle batches of person+address pairs (10 fake items for now). The architecture uses HTTP + Storage Queues + Durable Functions + callbacks. Zero wasted compute during SFTP transfers, with full retry/checkpoint resilience from Durable Functions.

## Architecture

```
App 1: Coordinator (Data Feed)
─────────────────────────────────────────────────
Function 1A: RunDataFeed (Timer, daily 6PM CST)
  1. Generate 10 fake person+address pairs (Bogus)
  2. Create batch record in Table Storage (Status=Processing)
  3. For each item:
     - POST /api/sftp/process { batchId, itemId, person, address, callbackUrl }
     - App 2 returns 202 immediately
     - Store item in Table Storage (Status=Queued)
  4. Function completes. Zero compute cost during SFTP work.

Function 1B: BatchItemCompleted (HTTP trigger, POST /api/sftp/callback)
  1. Receives callback from App 2: { batchId, itemId, succeeded, error? }
  2. Updates item in Table Storage (Completed or Failed)
  3. Checks if all items in batch are done
  4. If batch complete → notify third-party (mock) + update batch status
  5. Returns 200 OK

App 2: SFTP Processor (Durable Functions)
─────────────────────────────────────────────────
Function 2A: ReceiveSftpRequest (HTTP trigger, POST /api/sftp/process)
  1. Receives { batchId, itemId, person, address, callbackUrl }
  2. Drops message onto Storage Queue (sftp-processing-queue)
  3. Returns 202 Accepted

Function 2B: StartSftpOrchestration (Queue trigger)
  1. Reads message from queue
  2. Starts Durable Functions orchestration with full payload as input

SftpOrchestration (Orchestrator) — simplified from today:
  1. CallActivity: CreatePersonFile (with retry)
  2. CallActivity: CreateAddressFile (with retry)
  3. Task.WhenAll — parallel file creation
  4. CallActivity: UploadFile per file (with retry)
  5. CallActivity: SendCallback → POST callbackUrl with result

  Benefits preserved:
  - Activity retry policies (3 attempts, 5s backoff)
  - State checkpointing (survives crashes mid-upload)
  - Replay-safe execution
  - Built-in status monitoring endpoints

  Queue benefits layered on top:
  - Auto-retry if orchestration start fails (up to 5x)
  - Poison queue for permanently failed messages
  - Decoupled from HTTP connection lifetime
```

## What changes from today

- **External events removed**: No more separate /person and /address endpoints. All data arrives in one POST, passed directly as orchestration input.
- **Queue added in front**: HTTP receiver queues work instantly, queue trigger starts orchestration. Decouples HTTP from processing.
- **Callback added at end**: Orchestration calls back to App 1 when done, instead of App 1 polling.
- **Batch support**: Data feed sends 10 items, each tracked individually in Table Storage.
- **State tracking**: Table Storage tracks batch and item status.

## Data Model

**Table: BatchTracking** (Azure Table Storage, `AzureWebJobsStorage`)

Batch entity:
```
PartitionKey: "batch"
RowKey: "{batchId}"
Status: "Processing" | "Completed" | "PartialFailure"
ItemCount: int
CompletedCount: int
FailedCount: int
CreatedAt: DateTimeOffset
CompletedAt: DateTimeOffset?
```

Item entity:
```
PartitionKey: "{batchId}"
RowKey: "{itemId}"
Status: "Queued" | "Completed" | "Failed"
PersonFirstName, PersonLastName, PersonDateOfBirth: string
AddressStreet, AddressCity, AddressState, AddressZipCode: string
CreatedAt: DateTimeOffset
CompletedAt: DateTimeOffset?
ErrorMessage: string?
```

## New/Modified Records

```csharp
// Sent from App 1 → App 2 (and used as orchestration input)
record SftpProcessRequest(string BatchId, string ItemId, PersonData Person, AddressData Address, string CallbackUrl);

// Sent from App 2 → App 1 callback
record SftpProcessResult(string BatchId, string ItemId, bool Succeeded, string? ErrorMessage);
```

## Files

| File | Action |
|---|---|
| `az-functions.csproj` | Add `Azure.Data.Tables`, `Microsoft.Azure.Functions.Worker.Extensions.Storage.Queues` |
| `Models.cs` | New — `SftpProcessRequest`, `SftpProcessResult` records |
| `BatchTracking.cs` | New — table entities, `IBatchTracker` interface, `TableBatchTracker` impl |
| `ThirdPartyService.cs` | New — `IThirdPartyService` interface + mock impl |
| `SftpDataFeed.cs` | Rework — batch generation + `BatchItemCompleted` callback endpoint |
| `SftpOrchestration.cs` | Rework — remove external events, accept full payload as input, add `SendCallback` activity. Keep file creation + upload activities. |
| `SftpProcessor.cs` | New — `ReceiveSftpRequest` (HTTP→queue) + `StartSftpOrchestration` (queue→orchestration) |
| `Program.cs` | Register `IBatchTracker`, `IThirdPartyService` in DI |
| `CLAUDE.md` | Update architecture docs and file layout |

## Implementation Sequence

1. Add NuGet packages
2. Create `Models.cs`
3. Create `BatchTracking.cs`
4. Create `ThirdPartyService.cs`
5. Rework `SftpOrchestration.cs` — remove external events, accept `SftpProcessRequest` as input, add `SendCallback` final activity
6. Create `SftpProcessor.cs` — HTTP receiver + queue trigger
7. Rework `SftpDataFeed.cs` — batch generation + callback webhook
8. Update `Program.cs` — DI registrations
9. Update CLAUDE.md, README, test scripts

## Error Handling

- **SFTP failure**: Upload activity has retry policy (3 attempts, 5s backoff). If all retries fail, orchestration catches error, calls callback with `Succeeded=false`.
- **Queue failure**: If queue trigger can't start orchestration, Azure retries up to 5x, then poison queue.
- **Callback failure**: `SendCallback` activity has its own retry policy. If callback still fails after retries, the SFTP upload succeeded but App 1 won't know — item stays "Queued".
- **Partial batch**: Each item completes independently. Batch marked "PartialFailure" if any items failed.
- **Idempotent callback**: Updating an already-completed item is a no-op.

## Verification

1. `dotnet build` — zero warnings, zero errors
2. `func start` → trigger data feed via admin endpoint
3. Logs show: 10 items sent to App 2, queued, orchestrations run, SFTP uploads, callbacks fire
4. Table Storage shows batch + 10 items all Completed
5. Verify SFTP server has 20 new files (10 person + 10 address)
