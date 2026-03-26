# az-functions

Azure Functions v4 project (.NET 10, isolated worker model) demonstrating a two-app batch payment processing architecture with Durable Functions, Storage Queues, Table Storage, and SFTP upload.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local)
- [Docker](https://www.docker.com/products/docker-desktop)

## Project Setup

### 1. Clone and restore

```bash
git clone <repo-url>
cd az-functions
dotnet restore
```

### 2. Create `local.settings.json`

This file is gitignored. Create it in the project root:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "SFTP_HOST": "localhost",
    "SFTP_PORT": "2222",
    "SFTP_USERNAME": "testuser",
    "SFTP_PASSWORD": "testpass",
    "SFTP_REMOTE_PATH": "/config/upload",
    "PROCESSOR_BASE_URL": "http://localhost:7071",
    "COORDINATOR_BASE_URL": "http://localhost:7071",
    "BatchStorageConnection": "UseDevelopmentStorage=true"
  }
}
```

### 3. Start Docker services

The project uses Docker Compose for local dependencies:

- **Azurite** — Azure Storage emulator (required for Durable Functions, Table Storage, and queues)
- **OpenSSH Server** — local SFTP server for testing file uploads

```bash
docker compose up -d
```

### 4. Run the function app

```bash
func start
```

The app runs on port 7071.

## Architecture

In production, this will be two separate function apps. They are combined into a single app for this POC.

- **App 1 — GM (Coordinator)** (`GM/BatchCoordinator.cs`, `GM/BatchTracking.cs`): Generates ACH payment batches, submits them to the processor, tracks batch progress via callbacks.
- **App 2 — Fin (Batch Processor)** (`Fin/BatchProcessor.cs`, `Fin/BatchOrchestration.cs`, `Fin/BatchPaymentStore.cs`): Receives a batch of payments, stores payment data in Table Storage, enqueues a lightweight reference to trigger processing, creates CSV files (payment + GL) by reading from the table, uploads to SFTP, and calls back to App 1 with the result.

Communication between the two apps is HTTP + callback. App 1 POSTs the entire batch to App 2 in a single request and receives a status callback when processing completes or fails.

```
Step 1: App 1 submits a batch to App 2 (GM/BatchCoordinator.cs)
─────────────────────────────────────────────────────────────────

  RunDataFeed (Timer, daily) / TriggerDataFeed (HTTP POST /api/datafeed/trigger)
    1. Generate 10 fake ACH payments (Bogus)
    2. Create batch + 10 payment entities in BatchTracking table via IBatchTracker (GM/BatchTracking.cs)
    3. Query all Queued payments for the batch from Table Storage (picks up orphaned payments from a prior failed run)
    4. POST entire batch to ReceiveBatchRequest (Fin/BatchProcessor.cs)
    5. On success, set batch status to Processing

Step 2: App 2 stores, queues, and processes the batch
─────────────────────────────────────────────────────────

  ReceiveBatchRequest (HTTP POST /api/batch/process) — Fin/BatchProcessor.cs
    1. Validate BatchRequest (BatchId, CallbackUrl, Payments)
    2. Store payment data + callback URL in BatchPayments table via IBatchPaymentStore (Fin/BatchPaymentStore.cs)
    3. Enqueue lightweight BatchQueueMessage (BatchId only) onto batch-processing-queue via IMessageQueue
    4. Return 202 Accepted

  ProcessBatchQueue (Queue Trigger: batch-processing-queue) — Fin/BatchProcessor.cs
    1. Deserialize BatchQueueMessage to get BatchId
    2. Read callback URL from BatchPayments table via IBatchPaymentStore.GetCallbackUrlAsync
    3. Start BatchOrchestration with BatchOrchestrationInput (BatchId, CallbackUrl)
       Deterministic instance ID: batch-{batchId}

  BatchOrchestration (Durable Functions Orchestrator) — Fin/BatchOrchestration.cs
    1. CreatePaymentFile activity — reads payments from BatchPayments table, builds payment CSV (all fields)
    2. UploadFile activity — uploads payment CSV to SFTP (with retry: 3 attempts, 5s backoff)
       - If fails → SendCallback activity (Error), stop (no GL attempt)
    3. CreateGLFile activity — reads payments from BatchPayments table, builds GL CSV (excludes sensitive fields)
    4. UploadFile activity — uploads GL CSV to SFTP (with retry)
       - If fails → SendToGLErrorQueue activity, no callback
    5. If both succeed → SendCallback activity (Processed)

Step 3: App 1 receives callbacks (GM/BatchCoordinator.cs)
──────────────────────────────────────────────────────────

  BatchCompleted (HTTP POST /api/batch/callback)
    1. Receive BatchCallback (BatchId, Status)
    2. Update batch status in BatchTracking table via IBatchTracker
    3. Processed → log (TODO: notify third party)
    4. Error → log warning (TODO: send alert email)
```

### Status flow

```
Queued → Processing → Processed   (happy path)
Queued → Processing → Error       (payment SFTP failure)
Queued → Processing → (stuck)     (GL SFTP failure — no callback, stays Processing)
Queued → Error                    (batch submission failure)
```

### Error handling

- **Payment `UploadFile` failure**: Activity retry policy (3 attempts, 5s backoff). If all retries fail, `SendCallback` sends Error to App 1 and the orchestrator returns early — `CreateGLFile` is never called.
- **GL `UploadFile` failure**: Same retry policy. If all retries fail, `SendToGLErrorQueue` queues a `GLErrorMessage` to `gl-error-queue`. No callback is sent — batch stays in Processing until manual retry.
- **`SendCallback` failure**: Has its own retry policy (3 attempts, 5s backoff). If the callback fails after retries, the batch gets stuck in Processing (see open TODOs in CLAUDE.md).
- **Queue delivery failure**: `ProcessBatchQueue` retries up to 5x (`maxDequeueCount` in `host.json`), then Azure moves the message to `batch-processing-queue-poison`.
- **Duplicate orchestration**: `ProcessBatchQueue` sets the instance ID to `batch-{batchId}`, making duplicate starts a no-op.
- **Idempotent batch status**: `UpdateBatchStatusAsync` in `TableBatchTracker` skips updates if the batch is already in a terminal state.

## Data Model (Table Storage)

### BatchTracking table (App 1 — `GM/BatchTracking.cs`)

Used by `IBatchTracker` / `TableBatchTracker`. Connection: `AzureWebJobsStorage`.

**Batch entity** (`PartitionKey: "batch"`, `RowKey: "{batchId}"`):
- Status: `Queued` | `Processing` | `Processed` | `Error`
- PaymentCount, CreatedAt, CompletedAt

**Payment entity** (`PartitionKey: "{batchId}"`, `RowKey: "{paymentId}"`, `EntityType: "payment"`):
- All `PaymentData` fields (PaymentId, PayorName, PayeeName, Amount, AccountNumber, RoutingNumber, PaymentDate)
- Status: `Queued` | `Processed` | `Error`
- CreatedAt

### BatchPayments table (App 2 — `Fin/BatchPaymentStore.cs`)

Used by `IBatchPaymentStore` / `TableBatchPaymentStore`. Connection: `BatchStorageConnection`.

**Metadata entity** (`PartitionKey: "metadata"`, `RowKey: "{batchId}"`):
- CallbackUrl, PaymentCount, CreatedAt

**Payment entity** (`PartitionKey: "{batchId}"`, `RowKey: "{paymentId}"`, `EntityType: "payment"`):
- All `PaymentData` fields (PaymentId, PayorName, PayeeName, Amount, AccountNumber, RoutingNumber, PaymentDate)
- CreatedAt

## Storage

The POC uses a single `AzureWebJobsStorage` connection string for simplicity. In production, application data should be separated from infrastructure state:

| Service | POC | Production | Purpose |
|---|---|---|---|
| **Durable Functions** (blob/table) | `AzureWebJobsStorage` | `AzureWebJobsStorage` | Orchestration state, history, checkpoints — managed by the runtime |
| **Table Storage** (`BatchTracking`) | `AzureWebJobsStorage` | Dedicated storage account | App 1 batch metadata and payment status tracking |
| **Table Storage** (`BatchPayments`) | `BatchStorageConnection` | Dedicated storage account | App 2 payment data for file generation |
| **Queue Storage** (`batch-processing-queue`, `gl-error-queue`) | `BatchStorageConnection` | Dedicated storage account | Decouples HTTP acceptance from orchestration |

**Why separate?** `AzureWebJobsStorage` is the function app's internal storage — it holds lease blobs, host IDs, durable task history, and other runtime state. Mixing application tables and queues into this account couples your data to the runtime's lifecycle and makes independent management (backup, scaling, access control) harder.

**Local development**: All services point to Azurite (`UseDevelopmentStorage=true`) on ports 10000 (blob), 10001 (queue), 10002 (table).

## Functions

### App 1 — GM Coordinator (`GM/BatchCoordinator.cs`)

| Function | Trigger | Description |
|---|---|---|
| `RunDataFeed` | Timer (daily) | Generates batch of 10 fake ACH payments, creates entities via `IBatchTracker`, POSTs batch to App 2 |
| `TriggerDataFeed` | HTTP POST `/api/datafeed/trigger` | Same as `RunDataFeed` but returns `{ batchId }` — used by E2E test script |
| `BatchCompleted` | HTTP POST `/api/batch/callback` | Receives `BatchCallback`, updates batch status via `IBatchTracker` |
| `GetBatchStatus` | HTTP GET `/api/batch/{batchId}` | Returns batch + payment statuses from `BatchTracking` table |
| `ClearBatchData` | HTTP DELETE `/api/batch` | Deletes all entities in `BatchTracking` table |

### App 2 — Fin Entry Points (`Fin/BatchProcessor.cs`)

| Function | Trigger | Description |
|---|---|---|
| `ReceiveBatchRequest` | HTTP POST `/api/batch/process` | Validates request, stores in `BatchPayments` table, enqueues `BatchQueueMessage`, returns 202 |
| `ProcessBatchQueue` | Queue `batch-processing-queue` | Reads callback URL from `BatchPayments` table, starts `BatchOrchestration` with `BatchOrchestrationInput` |
| `ProcessGLErrorQueue` | Queue `gl-error-queue` | Logs failed GL uploads (TODO: error email, manual retry endpoint) |

### App 2 — Fin Orchestrator + Activities (`Fin/BatchOrchestration.cs`)

| Function | Type | Description |
|---|---|---|
| `BatchOrchestration` | Orchestrator | Receives `BatchOrchestrationInput(BatchId, CallbackUrl)`. Calls activities sequentially: payment file → upload → GL file → upload → callback |
| `CreatePaymentFile` | Activity | Reads payments from `BatchPayments` table via `IBatchPaymentStore`, builds payment CSV (all fields including account/routing) |
| `CreateGLFile` | Activity | Reads payments from `BatchPayments` table via `IBatchPaymentStore`, builds GL CSV (excludes sensitive banking fields) |
| `UploadFile` | Activity | Connects to SFTP via `ISftpClientFactory`, uploads file content. Used for both payment and GL files |
| `SendCallback` | Activity | POSTs `BatchCallback` to the `CallbackUrl` via `IHttpClientFactory` |
| `SendToGLErrorQueue` | Activity | Queues `GLErrorMessage` to `gl-error-queue` via `IGLErrorQueue` |

### Testing / Debug Endpoints (`Fin/SftpEndpoints.cs`)

| Function | Trigger | Description |
|---|---|---|
| `Sftp_ListFiles` | HTTP GET `/api/sftp/files` | Lists files on the SFTP server via `ISftpClientFactory` |
| `Sftp_GetFile` | HTTP GET `/api/sftp/files/{fileName}` | Returns contents of a file from the SFTP server |
| `Sftp_DeleteAllFiles` | HTTP DELETE `/api/sftp/files` | Deletes all files on SFTP server |

## API Reference

### POST /api/batch/process

Handled by `ReceiveBatchRequest` (`Fin/BatchProcessor.cs`). Body is `BatchRequest` (`Models/Models.cs`):

```json
{
  "batchId": "string",
  "payments": [
    {
      "paymentId": "string",
      "payorName": "string",
      "payeeName": "string",
      "amount": 0.00,
      "accountNumber": "string",
      "routingNumber": "string",
      "paymentDate": "string"
    }
  ],
  "callbackUrl": "string"
}
```

### POST /api/batch/callback

Sent by `SendCallback` (`Fin/BatchOrchestration.cs`) to `BatchCompleted` (`GM/BatchCoordinator.cs`). Body is `BatchCallback`:

```json
{
  "batchId": "string",
  "status": "Processed | Error"
}
```

## Configuration

| Variable | Required | Default | Used by |
|---|---|---|---|
| `PROCESSOR_BASE_URL` | Yes | — | App 1 — base URL of the batch processor |
| `COORDINATOR_BASE_URL` | Yes | — | App 1 — used to build callback URLs |
| `SFTP_HOST` | Yes | — | App 2 — SFTP server hostname |
| `SFTP_PORT` | No | `22` | App 2 — SFTP server port |
| `SFTP_USERNAME` | Yes | — | App 2 — login username |
| `SFTP_PASSWORD` | Yes | — | App 2 — login password |
| `SFTP_REMOTE_PATH` | No | `/upload` | App 2 — remote directory for uploads (local dev uses `/config/upload`) |

## Testing

### Unit tests

```bash
dotnet test tests/AzFunctions.Tests/                                    # run tests
dotnet test tests/AzFunctions.Tests/ --collect:"XPlat Code Coverage"    # with coverage
```

### Manual testing

**Option A: Trigger a batch and check status**

```bash
# 1. TriggerDataFeed — returns the batchId
curl -s -X POST http://localhost:7071/api/datafeed/trigger | python3 -m json.tool

# 2. GetBatchStatus — replace BATCH_ID
curl -s http://localhost:7071/api/batch/BATCH_ID | python3 -m json.tool

# 3. Sftp_ListFiles — check uploaded files
curl -s http://localhost:7071/api/sftp/files | python3 -m json.tool

# 4. Sftp_GetFile — read a specific file
curl -s http://localhost:7071/api/sftp/files/payment_BATCH_ID.csv
```

**Option B: Submit a batch directly to App 2**

```bash
curl -s -X POST http://localhost:7071/api/batch/process \
  -H "Content-Type: application/json" \
  -d '{
    "batchId": "test01",
    "payments": [
      {"paymentId":"pmt-000","payorName":"John Doe","payeeName":"Acme Corp","amount":1500.00,"accountNumber":"1234567890","routingNumber":"021000021","paymentDate":"2026-03-20"}
    ],
    "callbackUrl": "http://localhost:7071/api/batch/callback"
  }' | python3 -m json.tool
```

**Cleanup endpoints** (useful between manual test runs):

```bash
# ClearBatchData — clear batch tracking data
curl -s -X DELETE http://localhost:7071/api/batch

# Sftp_DeleteAllFiles — clear SFTP files
curl -s -X DELETE http://localhost:7071/api/sftp/files
```

### Viewing SFTP files

**Via the API** — uses `Sftp_ListFiles` and `Sftp_GetFile` (`Fin/SftpEndpoints.cs`):

```bash
curl http://localhost:7071/api/sftp/files
curl http://localhost:7071/api/sftp/files/{fileName}
```

**On the host filesystem** (volume mount):

```bash
ls ./sftp-data/
cat ./sftp-data/payment_abc12345.csv
```

**Via SSH into the container:**

```bash
ssh -p 2222 testuser@localhost
# password: testpass
ls /config/upload/
```

### End-to-end test

```bash
# Make sure Docker services and func are running first
docker compose up -d
func start  # in another terminal

# Run the test
./test-batch-orchestration.sh
```

> **Note:** `test-sftp-retry.sh` is stale — it references endpoints from a previous architecture revision and will not work.

The script will:
1. Clean up previous test data (batch tracking entities + SFTP files)
2. Trigger a batch of 10 payments and capture the batchId
3. Poll batch status until terminal (Processed or Error), timeout after 3 minutes
4. Display payment-by-payment status table
5. Verify SFTP files uploaded (payment + GL CSVs) and read a sample
6. Print pass/fail summary

### Expected test output

```
Step 1: Cleaning up previous test data...
  Batch tracking: cleared 11 entities
  SFTP files: cleared 2 files

Step 2: Triggering data feed...
  Started batch abc12345 with 10 payments

Step 3: Polling batch status from Table Storage...
  Poll 1: batch status=Processed

  Payment Status (from BatchTracking table):
  -------------------------------------------
  pmt-000      Processed
  pmt-001      Processed
  ...

Step 4: Verifying SFTP files...
  Total files: 2 (payment: 1, gl: 1)

Summary:
  Batch:  Processed
  Files (SFTP):  2 uploaded (1 payment + 1 gl)
  Payments tracked:  10

  Result: PASS
```

## Docker Services

| Service | Image | Ports | Purpose |
|---|---|---|---|
| azurite | `mcr.microsoft.com/azure-storage/azurite` | 10000, 10001, 10002 | Azure Storage emulator (Durable Functions state, Table Storage, queues) |
| sftp | `lscr.io/linuxserver/openssh-server` | 2222 | SFTP server for testing |

### SFTP server credentials

| Setting | Value |
|---|---|
| Host | `localhost` |
| Port | `2222` |
| Username | `testuser` |
| Password | `testpass` |
| Upload path | `/config/upload` |

---

# Reference: Durable Functions Internals

Everything below is reference material on how Azure Durable Functions work behind the scenes. It is not specific to this application's business logic.

## How Durable Functions Work Behind the Scenes

The orchestration in `Fin/BatchOrchestration.cs` uses Azure Durable Functions, which is built on an event-sourcing pattern backed by Azure Storage. Here's what happens internally.

> **Microsoft docs**: [Durable orchestrations (replay, event sourcing, deterministic constraints)](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-orchestrations) | [Task hubs (internal storage layout)](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-task-hubs) | [Performance and scale (dispatcher, partitions, concurrency)](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-perf-and-scale)

### The 202 Accepted is your code, not the framework

The Durable Functions framework does not automatically intercept HTTP requests. In this project, `ReceiveBatchRequest` (`Fin/BatchProcessor.cs`) is a regular HTTP-triggered function that validates the request, stores data in Table Storage, queues a reference message, and explicitly returns 202. The orchestrator and its activities don't return HTTP responses at all — they communicate through internal storage infrastructure.

### Starting an orchestration

When `ProcessBatchQueue` (`Fin/BatchProcessor.cs`) calls `ScheduleNewOrchestrationInstanceAsync`, the Durable Task Framework:

1. Writes an **ExecutionStarted** event to the **history table** in your storage account
2. Enqueues a message onto an **internal control queue** (e.g., `<taskhub>-control-00`)
3. Returns immediately — the orchestrator hasn't executed yet

### The dispatcher (invisible background loop)

The Durable Task Framework runs a **dispatcher** as a background service inside the function app host. It:

- **Polls internal control queues** continuously
- When it dequeues a message, it **replays** the orchestrator function (`BatchOrchestration.RunOrchestrator` in `Fin/BatchOrchestration.cs`) from the beginning
- Each `CallActivityAsync` is checked against the **history table**:
  - Result already in history → return cached result (replay, no execution)
  - Not in history → enqueue a **work item** to the internal work-item queue, then **suspend** the orchestrator
- When an activity completes, its result is written to history, and a control queue message wakes the orchestrator for another replay

### Internal storage layout

All of these are created automatically in your `AzureWebJobsStorage` account:

| Storage artifact | Purpose |
|---|---|
| `<taskhub>Instances` table | Orchestration metadata (status, input, output, timestamps) |
| `<taskhub>History` table | Event sourcing log — every activity scheduled, completed, failed |
| `<taskhub>-control-00` through `-03` queues | Control messages that wake up orchestrators |
| `<taskhub>-workitems` queue | Messages that trigger activity function execution |
| `<taskhub>-leases` blob container | Partition lease management for scale-out |

### The replay model

This is why orchestrator functions must be **deterministic** (no `DateTime.Now`, no random values, no direct I/O):

```
ProcessBatchQueue (Fin/BatchProcessor.cs)
    │
    ▼ ScheduleNewOrchestrationInstanceAsync()
    │
    ├── writes ExecutionStarted to History table
    └── enqueues to control queue
              │
              ▼
    [Dispatcher — invisible background loop]
              │
              ├── dequeues control message
              ├── replays BatchOrchestration.RunOrchestrator (Fin/BatchOrchestration.cs)
              ├── hits CallActivityAsync → checks history
              │       │
              │       ├── cached? → return result, continue
              │       └── not cached? → enqueue to workitems queue, suspend
              │                              │
              │                              ▼
              │                    [Activity runs as separate function invocation]
              │                    e.g. CreatePaymentFile, UploadFile (Fin/BatchOrchestration.cs)
              │                              │
              │                              └── writes result to History table
              │                                   enqueues control message
              │                                        │
              └────────────────────────────────────────┘
                        (loop until orchestrator completes)
```

For example, when `BatchOrchestration.RunOrchestrator` hits `CallActivityAsync(nameof(CreatePaymentFile), ...)`:

- **First execution**: No history exists for this call → framework enqueues a work-item → orchestrator suspends → `CreatePaymentFile` runs as a separate function invocation → result saved to history → control queue message wakes the orchestrator
- **Replay**: History has the result → `CallActivityAsync` returns the cached result immediately → orchestrator continues to the next `CallActivityAsync` (e.g., `UploadFile`)

The orchestrator replays from the top every time it wakes up. Each completed activity is replayed from cache until the orchestrator reaches the next unfinished step.

## Activity Retry Policies

> **Microsoft docs**: [Handling errors and retries](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-error-handling)

When you pass a `RetryPolicy` to `CallActivityAsync`, the framework handles all retry logic automatically — your orchestrator code doesn't loop or sleep.

```csharp
private static readonly TaskOptions UploadRetryOptions = TaskOptions.FromRetryPolicy(new RetryPolicy(
    maxNumberOfAttempts: 3,
    firstRetryInterval: TimeSpan.FromSeconds(5)));
```

`RetryPolicy` supports these parameters:

| Parameter | Default | Description |
|---|---|---|
| `maxNumberOfAttempts` | (required) | Total attempts including the first try |
| `firstRetryInterval` | (required) | Delay before the first retry |
| `backoffCoefficient` | `1.0` | Multiplier applied to the interval after each retry (e.g., `2.0` = exponential backoff) |
| `maxRetryInterval` | none | Cap on the retry interval when using a backoff coefficient |
| `retryTimeout` | none | Total time budget — stops retrying even if attempts remain |

Here's what happens internally when an activity fails and has a retry policy:

1. The activity throws an exception → framework records a **TaskFailed** event in the history table
2. The framework checks the retry policy — attempts remaining? within timeout?
3. If yes → it creates a **durable timer** (a scheduled control queue message) for the retry interval
4. When the timer fires → the dispatcher replays the orchestrator, which re-dispatches the activity as a new work-item
5. Each retry is a **separate activity invocation** — the orchestrator stays suspended the entire time
6. If all retries are exhausted → the framework raises `TaskFailedException` back to the orchestrator

The orchestrator does not replay during retries. It only wakes up once the activity either succeeds or exhausts all attempts.

## Resilience and Crash Recovery

The Durable Task Framework provides **at-least-once execution** guarantees. Because all state lives in Azure Storage (not in memory), orchestrations survive host crashes, restarts, and scale events:

**Host crashes mid-activity**: The work-item queue message has a visibility timeout (default 5 minutes, configured in `host.json` under `extensions.queues.visibilityTimeout`). If the host crashes before the activity completes, the message becomes visible again after the timeout expires, and another host instance picks it up and re-executes the activity.

**Host crashes mid-orchestrator-replay**: Same mechanism via the control queue. The control queue message becomes visible again, and the orchestrator is replayed from the beginning — all previously completed activities return their cached results instantly.

**Host restarts**: When the function app restarts, the dispatcher resumes polling the control and work-item queues. Any in-flight orchestrations or activities continue from where they left off. No manual intervention needed.

**Durable timers survive restarts**: Timer-based delays (including retry backoff intervals) are stored as scheduled messages in the control queue. They fire on schedule regardless of whether the host was running when they were created.

**Duplicate execution protection**: Activity results are recorded in the history table before the orchestrator is notified. If an activity completes but the host crashes before the result is processed, the orchestrator will replay and see the cached result — it won't re-execute the activity.

## Persisted Orchestration Data

The original input passed to `ScheduleNewOrchestrationInstanceAsync` (called by `ProcessBatchQueue` in `Fin/BatchProcessor.cs`) is stored in the `Instances` table as `SerializedInput`. This means the `BatchOrchestrationInput` (`Models/Models.cs`) — batch ID and callback URL — is persisted for the lifetime of the orchestration instance. Payment data is stored separately in the `BatchPayments` table and read by activities on demand.

You can retrieve the orchestration input programmatically:

```csharp
var metadata = await durableClient.GetInstanceAsync("batch-{batchId}", getInputsAndOutputs: true);
var originalInput = metadata.ReadInputAs<BatchOrchestrationInput>();
// originalInput.BatchId, originalInput.CallbackUrl are available
// Payment data is in the BatchPayments table, not in the orchestration input
```

The `getInputsAndOutputs: true` flag is required — without it, `SerializedInput` is null and `ReadInputAs<T>()` throws `InvalidOperationException`.

`OrchestrationMetadata` also exposes:

| Property | Description |
|---|---|
| `InstanceId` | The orchestration instance ID (e.g., `batch-abc12345`) |
| `RuntimeStatus` | `Pending`, `Running`, `Completed`, `Failed`, `Terminated`, `Suspended` |
| `CreatedAt` | When the orchestration was scheduled |
| `LastUpdatedAt` | Last state change |
| `SerializedInput` | The raw JSON input (requires `getInputsAndOutputs: true`) |
| `SerializedOutput` | The raw JSON return value (requires `getInputsAndOutputs: true`) |
| `FailureDetails` | Exception info for `Failed` orchestrations |
| `IsRunning` / `IsCompleted` | Convenience booleans |

## Instance Management API (`DurableTaskClient`)

> **Microsoft docs**: [Manage orchestration instances](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-instance-management)

The `DurableTaskClient` (injected via `[DurableClient]`) exposes these management methods:

| Method | Description |
|---|---|
| `GetInstanceAsync(id, getInputsAndOutputs)` | Retrieve orchestration metadata, optionally with input/output data |
| `TerminateInstanceAsync(id, reason)` | Send a terminate message to a running orchestration. Does not cancel in-flight activities — they finish but their results are discarded |
| `SuspendInstanceAsync(id, reason)` | Pause a running orchestration — it stops processing control queue messages until resumed |
| `ResumeInstanceAsync(id, reason)` | Resume a suspended orchestration |
| `PurgeInstanceAsync(id)` | Delete all data (Instances + History) for a terminal orchestration (Completed, Failed, or Terminated). Required before reusing a deterministic instance ID |
| `PurgeAllInstancesAsync(filter)` | Bulk purge by status, date range, etc. |
| `GetAllInstancesAsync(query)` | Query instances by status, date range, instance ID prefix |

## Built-in HTTP Management Endpoints

> **Microsoft docs**: [HTTP APIs in Durable Functions](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-http-api)

The Durable Task Framework exposes management endpoints automatically — no custom code needed. Locally they're at `http://localhost:7071/runtime/webhooks/durableTask/instances/{instanceId}`.

```bash
# Check the status of an orchestration (add ?showInput=true for the original payload)
curl http://localhost:7071/runtime/webhooks/durableTask/instances/batch-{batchId}

# Check status with the full input/output
curl "http://localhost:7071/runtime/webhooks/durableTask/instances/batch-{batchId}?showInput=true&showOutput=true"

# Terminate a running orchestration
curl -X POST \
  "http://localhost:7071/runtime/webhooks/durableTask/instances/batch-{batchId}/terminate?reason=manual"

# Suspend a running orchestration (can be resumed later)
curl -X POST \
  "http://localhost:7071/runtime/webhooks/durableTask/instances/batch-{batchId}/suspend?reason=investigating"

# Resume a suspended orchestration
curl -X POST \
  "http://localhost:7071/runtime/webhooks/durableTask/instances/batch-{batchId}/resume?reason=issue+resolved"

# Purge a completed/failed/terminated instance
curl -X DELETE \
  http://localhost:7071/runtime/webhooks/durableTask/instances/batch-{batchId}
```

The purge deletes the instance from the `Instances` and `History` tables, so `ScheduleNewOrchestrationInstanceAsync` will accept the same ID again.

## Retrying a Failed Batch Manually

Because this project uses deterministic instance IDs (`batch-{batchId}`), you can't just re-submit a batch — the framework will reject the duplicate ID. There are two approaches:

**Option 1: Purge and re-submit (using the HTTP management API)**

This is the simplest approach and requires no code changes:

```bash
# 1. Check the current status
curl "http://localhost:7071/runtime/webhooks/durableTask/instances/batch-abc12345?showInput=true"

# 2. Purge the old instance
curl -X DELETE http://localhost:7071/runtime/webhooks/durableTask/instances/batch-abc12345

# 3. Re-submit (payment data is already in the BatchPayments table from the original request)
curl -X POST http://localhost:7071/api/batch/process \
  -H "Content-Type: application/json" \
  -d '{ "batchId": "abc12345", "payments": [...], "callbackUrl": "http://..." }'
```

Note: Step 3 re-submits a full `BatchRequest`. `ReceiveBatchRequest` will upsert the payment data in the `BatchPayments` table (idempotent) and enqueue a new `BatchQueueMessage`.

**Option 2: Custom retry endpoint (reads from Table Storage)**

A custom endpoint can read the callback URL from the `BatchPayments` table, purge the old orchestration instance, and re-start — all in one call:

```csharp
// POST /api/batch/retry/{batchId}
[Function(nameof(RetryBatch))]
public async Task<HttpResponseData> RetryBatch(
    [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "batch/retry/{batchId}")] HttpRequestData req,
    string batchId,
    [DurableClient] DurableTaskClient durableClient,
    FunctionContext executionContext)
{
    string instanceId = $"batch-{batchId}";
    var metadata = await durableClient.GetInstanceAsync(instanceId, getInputsAndOutputs: true);

    if (metadata is null)
        return /* 404: no orchestration found for this batch */;

    if (metadata.IsRunning)
        return /* 409: orchestration is still running */;

    // Read the callback URL from the BatchPayments table (payment data is already there)
    string callbackUrl = await batchPaymentStore.GetCallbackUrlAsync(batchId);

    // Purge the old instance so the ID can be reused
    await durableClient.PurgeInstanceAsync(instanceId);

    // Re-start with a fresh orchestration input
    var input = new BatchOrchestrationInput(batchId, callbackUrl);
    await durableClient.ScheduleNewOrchestrationInstanceAsync(
        nameof(BatchOrchestration), input,
        new StartOrchestrationOptions { InstanceId = instanceId });

    return /* 202: retrying batch {batchId} */;
}
```

This approach is simpler than the original because payment data is already persisted in the `BatchPayments` table — the retry endpoint only needs the callback URL to construct the orchestration input.

**Important note on orchestration status**: In this project, the orchestrator catches all `TaskFailedException` errors and returns a result string. This means even when uploads fail, the orchestration status is `Completed` (not `Failed`) — the error is handled as business logic. Both approaches above work regardless of whether the orchestration is `Completed` or `Failed`, since purge accepts any terminal status.
