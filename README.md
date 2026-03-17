# az-functions

Azure Functions v4 project (.NET 10, isolated worker model) demonstrating a two-app batch processing architecture with Durable Functions, Storage Queues, and SFTP upload.

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
    "COORDINATOR_BASE_URL": "http://localhost:7071"
  }
}
```

### 3. Start Docker services

The project uses Docker Compose for local dependencies:

- **Azurite** — Azure Storage emulator (required for durable functions, table storage, and queues)
- **OpenSSH Server** — local SFTP server for testing file uploads

```bash
docker compose up -d
```

### 4. Run the function app

```bash
func start
```

The app runs on port 7071.

## Storage

The POC uses a single `AzureWebJobsStorage` connection string for simplicity. In production, application data should be separated from infrastructure state:

| Service | POC | Production | Purpose |
|---|---|---|---|
| **Durable Functions** (blob/table) | `AzureWebJobsStorage` | `AzureWebJobsStorage` | Orchestration state, history, checkpoints — managed by the runtime |
| **Table Storage** (`BatchTracking`) | `AzureWebJobsStorage` | Dedicated storage account | Batch metadata and item status tracking — application data |
| **Queue Storage** (`sftp-processing-queue`) | `AzureWebJobsStorage` | Dedicated storage account | Decouples HTTP acceptance from orchestration — application data |

**Why separate?** `AzureWebJobsStorage` is the function app's internal storage — it holds lease blobs, host IDs, durable task history, and other runtime state. Mixing application tables and queues into this account couples your data to the runtime's lifecycle and makes independent management (backup, scaling, access control) harder.

**Local development**: All services point to Azurite (`UseDevelopmentStorage=true`) on ports 10000 (blob), 10001 (queue), 10002 (table). No separation needed locally.

## Architecture

In production, this will be two separate function apps. They are combined into a single app for this POC.

- **App 1 — Coordinator** (`SftpDataFeed.cs`, `BatchTracking.cs`): Generates payment data, tracks batch progress, and notifies the third party when complete.
- **App 2 — SFTP Processor** (`SftpProcessor.cs`, `SftpOrchestration.cs`): Receives individual payment items, creates files, uploads to SFTP, and calls back to App 1.

Communication between the two apps is HTTP + callback. App 1 POSTs items to App 2 and receives completion callbacks asynchronously.

```
Step 1: App 1 sends items to App 2
──────────────────────────────────

  RunDataFeed (Timer, daily)
    1. Generate 10 fake payments (Bogus)
    2. Create batch + items in Table Storage
    3. POST each item to ReceiveSftpRequest
    4. Get 202 back, function exits

Step 2: App 2 processes each item
─────────────────────────────────

  ReceiveSftpRequest (HTTP POST /api/sftp/process)
    1. Validate request
    2. Drop message onto Storage Queue
    3. Return 202 Accepted

  ProcessSftpQueue (Queue Trigger)
    1. Deserialize queue message
    2. Start SftpOrchestration (deterministic ID: sftp-{batchId}-{itemId})

  SftpOrchestration (Orchestrator)
    1. CreatePersonFile + CreateAddressFile (parallel)
    2. UploadFile for person (with retry, independent try/catch)
    3. UploadFile for address (with retry, independent try/catch)
    4. SendCallback — POST per-file results to App 1's callbackUrl

Step 3: App 1 tracks completion
───────────────────────────────

  BatchItemCompleted (HTTP POST /api/batch/callback)
    1. Update per-file statuses in Table Storage
    2. Derive item status from its files
    3. Check if all files are done
    4. If yes — mark batch complete + notify third party (TODO)
    5. Return 200 OK
```

### Key design decisions

- **Single-request flow**: No external events. Each item is a self-contained `SftpProcessRequest` with person data, address data, and callback URL.
- **Deterministic instance IDs**: `sftp-{batchId}-{itemId}` prevents duplicate orchestrations from at-least-once queue delivery.
- **Per-file tracking**: Each batch item has separate file entities (person + address) in Table Storage. Item status is derived from its files. If one file fails, the other can still succeed.
- **No counters on batch entity**: `BatchItemCompleted` queries all files by batchId to check completion, avoiding race conditions from concurrent counter updates.
- **Storage Queue**: Decouples HTTP acceptance from orchestration processing. Provides built-in retry with poison queue support.
- **Callback-driven completion**: App 2 doesn't know about batches — it processes individual items and calls back with success/failure. App 1 aggregates results.

### Data model (Table Storage)

**Table: BatchTracking**

Batch entity (`PartitionKey: "batch"`, `RowKey: "{batchId}"`):
- Status: `Processing` | `Completed` | `PartialFailure`
- ItemCount, FileCount (itemCount * 2), CreatedAt, CompletedAt

Item entity (`PartitionKey: "{batchId}"`, `RowKey: "{itemId}"`):
- Status: `Queued` | `Completed` | `Failed` (derived from file statuses)
- CreatedAt, CompletedAt, ErrorMessage

File entity (`PartitionKey: "{batchId}"`, `RowKey: "{itemId}_{fileType}"`):
- ItemId, FileType (`person` | `address`)
- Status: `Queued` | `Completed` | `Failed`
- CreatedAt, CompletedAt, ErrorMessage

## Functions

### Application

| Function | Trigger | Description |
|---|---|---|
| `RunDataFeed` | Timer (daily) | Generates batch of 10 fake payments, creates batch in Table Storage, POSTs each to processor |
| `BatchItemCompleted` | HTTP POST `/api/batch/callback` | Receives completion callbacks, updates Table Storage, detects batch completion |
| `ReceiveSftpRequest` | HTTP POST `/api/sftp/process` | Accepts processing request, drops onto Storage Queue, returns 202 |
| `ProcessSftpQueue` | Queue `sftp-processing-queue` | Starts Durable Functions orchestration with deterministic instance ID |
| `SftpOrchestration` | Orchestration | Creates files in parallel, uploads to SFTP with retry, sends callback |

### Testing / Verification

| Function | Trigger | Description |
|---|---|---|
| `TriggerDataFeed` | HTTP POST `/api/datafeed/trigger` | Same as RunDataFeed but returns `{ batchId }` — used by E2E test script |
| `GetBatchStatus` | HTTP GET `/api/batch/{batchId}` | Returns batch, item, and file statuses from Table Storage |
| `ClearBatchData` | HTTP DELETE `/api/batch` | Deletes all entities in BatchTracking table |
| `SftpOrchestration_ListFiles` | HTTP GET `/api/sftp/files` | Lists files on the SFTP server |
| `SftpOrchestration_GetFile` | HTTP GET `/api/sftp/files/{fileName}` | Returns the contents of a file from the SFTP server |
| `SftpOrchestration_DeleteAllFiles` | HTTP DELETE `/api/sftp/files` | Deletes all files on SFTP server |

## Data Feed

The `SftpDataFeed` class (`SftpDataFeed.cs`) acts as the coordinator. It generates fake payment data using [Bogus](https://github.com/bchavez/Bogus) and submits batches for SFTP processing.

### What it does

1. Generates 10 random person + address pairs using Bogus
2. Creates a batch entity, 10 item entities, and 20 file entities in Table Storage
3. POSTs each `SftpProcessRequest` to `POST /api/sftp/process`
4. Each request includes a `callbackUrl` pointing to `/api/batch/callback`
5. As callbacks arrive, updates per-file statuses, derives item status, and checks batch completion
6. When all items complete, logs "would notify third party"

### Triggering manually

```bash
curl -X POST http://localhost:7071/admin/functions/RunDataFeed \
  -H "Content-Type: application/json" -d '{}'
```

### Configuration

| Variable | Required | Description |
|---|---|---|
| `PROCESSOR_BASE_URL` | Yes | Base URL of the SFTP processor (e.g., `http://localhost:7071`) |
| `COORDINATOR_BASE_URL` | Yes | Base URL of the coordinator, used to build callback URLs |

### Expected log output

```
[SFTP] Data feed starting — batch <batchId> with 10 items.
[SFTP] Batch <batchId> — item item-000 queued (John Doe).
...
[SFTP] Batch <batchId> — item item-009 queued (Jane Smith).
[SFTP] Data feed batch <batchId> — all 10 items submitted.
[SFTP] Callback received — batch <batchId>, item item-000, files=2.
...
[SFTP] Batch <batchId> complete — would notify third party.
```

## SFTP Orchestration

### How it works

The orchestration receives an `SftpProcessRequest` containing person data, address data, batch/item IDs, and a callback URL. It:

1. Creates person and address files in parallel
2. Uploads person file to SFTP (with retry policy: 3 attempts, 5s backoff)
3. Uploads address file to SFTP (with retry policy: 3 attempts, 5s backoff)
4. Sends a callback to the coordinator with per-file results

Each upload has its own try/catch — a failure in one file doesn't prevent the other from uploading. Failed files are cleaned up individually.

### Request flow

```
POST /api/sftp/process (SftpProcessRequest)
  └─► 202 Accepted + message on sftp-processing-queue
        │
        ▼
  ProcessSftpQueue (queue trigger)
        │
        ▼
  SftpOrchestration (deterministic ID: sftp-{batchId}-{itemId})
        │
  ┌─────┴─────┐
  ▼           ▼
CreatePersonFile  CreateAddressFile   (parallel)
  └─────┬─────┘
        ▼
  UploadFile (person, with retry + independent try/catch)
        ▼
  UploadFile (address, with retry + independent try/catch)
        ▼
  SendCallback → POST /api/batch/callback (per-file results)
```

### Request schema

**POST /api/sftp/process**:
```json
{
  "batchId": "string",
  "itemId": "string",
  "person": {
    "firstName": "string",
    "lastName": "string",
    "dateOfBirth": "string"
  },
  "address": {
    "street": "string",
    "city": "string",
    "state": "string",
    "zipCode": "string"
  },
  "callbackUrl": "string"
}
```

**Callback payload (POST to callbackUrl)**:
```json
{
  "batchId": "string",
  "itemId": "string",
  "files": [
    { "fileType": "person", "succeeded": true, "errorMessage": null },
    { "fileType": "address", "succeeded": true, "errorMessage": null }
  ]
}
```

### Error handling

- **SFTP upload failure**: Activity retry policy (3 attempts, 5s backoff). Each file is uploaded independently — a person file failure doesn't prevent the address file from uploading.
- **Queue delivery failure**: Azure retries up to 5x, then moves to `sftp-processing-queue-poison`.
- **Callback failure**: `SendCallback` activity has its own retry policy (3 attempts, 5s backoff).
- **Per-file tracking**: Each file has its own status entity. Item status is derived from its files (all files completed → item completed, any failed with none queued → item failed).
- **Partial batch**: Each item is independent. Batch marked `PartialFailure` if any files failed.
- **Idempotent callback**: Updating an already-terminal file status returns 200 without modification.
- **Duplicate orchestration**: Deterministic instance ID (`sftp-{batchId}-{itemId}`) makes duplicate starts a no-op.

### Viewing files on the SFTP server

There are three ways to see what's been uploaded:

**Via the API (easiest):**

```bash
# List all files on the SFTP server
curl http://localhost:7071/api/sftp/files

# Read a specific file's contents
curl http://localhost:7071/api/sftp/files/{fileName}
```

**On the host filesystem (volume mount):**

```bash
ls ./sftp-data/
cat ./sftp-data/person_sftp-abc12345-item-000.txt
```

**Via SSH into the container:**

```bash
ssh -p 2222 testuser@localhost
# password: testpass
ls /config/upload/
```

## Testing

### Manual testing

**Option A: Trigger a batch and check status**

```bash
# 1. Trigger the data feed — returns the batchId
curl -s -X POST http://localhost:7071/api/datafeed/trigger | python3 -m json.tool

# 2. Check batch status (replace BATCH_ID)
curl -s http://localhost:7071/api/batch/BATCH_ID | python3 -m json.tool

# 3. Check uploaded files
curl -s http://localhost:7071/api/sftp/files | python3 -m json.tool

# 4. Read a specific file
curl -s http://localhost:7071/api/sftp/files/person_sftp-BATCH_ID-item-000.txt
```

**Option B: Submit a single item directly**

```bash
# POST a single processing request (bypasses the data feed)
curl -s -X POST http://localhost:7071/api/sftp/process \
  -H "Content-Type: application/json" \
  -d '{
    "batchId": "test01",
    "itemId": "item-000",
    "person": {"firstName":"John","lastName":"Doe","dateOfBirth":"1990-01-15"},
    "address": {"street":"123 Main St","city":"Springfield","state":"IL","zipCode":"62701"},
    "callbackUrl": "http://localhost:7071/api/batch/callback"
  }' | python3 -m json.tool
```

**Cleanup endpoints** (useful between manual test runs):

```bash
curl -s -X DELETE http://localhost:7071/api/batch       # clear batch tracking data
curl -s -X DELETE http://localhost:7071/api/sftp/files   # clear SFTP files
```

### End-to-end test

```bash
# Make sure Docker services and func are running first
docker compose up -d
func start  # in another terminal

# Run the test
./test-sftp-orchestration.sh
```

> **Note:** `test-sftp-retry.sh` is stale — it references endpoints and tools from a previous architecture revision and will not work. Use `test-sftp-orchestration.sh` for E2E testing.

The script will:
1. Clean up previous test data (batch tracking entities + SFTP files)
2. Trigger a batch of 10 items and capture the batchId
3. Poll batch status from Table Storage until all items complete
4. Display item-by-item status table (ItemId, Status, CompletedAt)
5. Verify 20 SFTP files uploaded and read a sample
6. Print pass/fail summary

### Expected test output

```
Step 1: Cleaning up previous test data...
  Batch tracking: cleared 31 entities
  SFTP files: cleared 20 files

Step 2: Triggering data feed...
  Started batch abc12345 with 10 items

Step 3: Waiting for batch to complete...
  Poll 1: 10/10 items done (batch: Completed)
  Item Status:
  item-000     Completed    2026-03-16T19:44:40
  item-001     Completed    2026-03-16T19:44:40
  ...

Step 4: Verifying SFTP files...
  Total files: 20 (person: 10, address: 10)

Summary:
  Batch: Completed | Items: 10 completed, 0 failed | Files: 20 | PASS
```

## SFTP via SSH.NET

SFTP connectivity is provided by [SSH.NET](https://github.com/sshnet/SSH.NET) (`Renci.SshNet`) via `ISftpClientFactory`. The factory reads SFTP config from environment variables once at startup, validates the port, and creates connected `SftpClient` instances. It is used in `SftpOrchestration.cs` for:

- **`UploadFile` activity** — uploads a single file with retry policy
- **`ListFiles` HTTP trigger** — lists all files in the remote upload directory
- **`GetFile` HTTP trigger** — reads a specific file by name

### Configuration

| Variable | Required | Default | Description |
|---|---|---|---|
| `SFTP_HOST` | Yes | — | SFTP server hostname |
| `SFTP_PORT` | No | `22` | SFTP server port |
| `SFTP_USERNAME` | Yes | — | Login username |
| `SFTP_PASSWORD` | Yes | — | Login password |
| `SFTP_REMOTE_PATH` | No | `/upload` | Remote directory for uploads (local dev uses `/config/upload` — see `local.settings.json`) |

## Docker Services

| Service | Image | Ports | Purpose |
|---|---|---|---|
| azurite | `mcr.microsoft.com/azure-storage/azurite` | 10000, 10001, 10002 | Azure Storage emulator (durable functions state, table storage, queues) |
| sftp | `lscr.io/linuxserver/openssh-server` | 2222 | SFTP server for testing |

### SFTP server credentials

| Setting | Value |
|---|---|
| Host | `localhost` |
| Port | `2222` |
| Username | `testuser` |
| Password | `testpass` |
| Upload path | `/config/upload` |

### Data directories (gitignored)

- `.azurite/` — Azurite storage data
- `sftp-data/` — Files uploaded via SFTP
