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

All Azure Storage services use the single `AzureWebJobsStorage` connection string:

| Service | Purpose |
|---|---|
| **Table Storage** (`BatchTracking`) | Batch metadata and item status tracking |
| **Queue Storage** (`sftp-processing-queue`) | Decouples HTTP acceptance from orchestration processing |
| **Blob/Table** (Durable Functions) | Orchestration state, history, and checkpoints |

Locally, this points to **Azurite** (`UseDevelopmentStorage=true`) on ports 10000 (blob), 10001 (queue), 10002 (table).

In production, this should be a **dedicated storage account** separate from the function app's built-in storage, to isolate application data from infrastructure state.

## Architecture

Two logical apps (Coordinator + SFTP Processor) running in a single function app for the POC. Communication uses HTTP + Storage Queue + callback.

```
App 1: Coordinator                          App 2: SFTP Processor
─────────────────────                       ─────────────────────
Function 1A: DataFeed (Timer)               Function 2A: ReceiveSftpRequest (HTTP)
  1. Generate 10 fake payments (Bogus)        1. Accept POST with payments + callbackUrl
  2. Create batch in Table Storage            2. Drop message onto Storage Queue
  3. POST each item to App 2                  3. Return 202 Accepted immediately
  4. Get 202 back, function exits
                                            Function 2B: ProcessSftpQueue (Queue Trigger)
Function 1B: BatchItemCompleted (HTTP)        1. Start Durable Functions orchestration
  1. Receive callback { batchId, itemId }        (deterministic ID: sftp-{batchId}-{itemId})
  2. Update item status in Table Storage
  3. Query all items — all done?            SftpOrchestration (Orchestrator)
  4. If yes → log "would notify 3rd party"    1. Parallel: CreatePersonFile + CreateAddressFile
     + mark batch complete                    2. Parallel: UploadFile × 2 (with retry)
  5. Return 200 OK                            3. SendCallback → POST to App 1's webhook
```

### Key design decisions

- **Single-request flow**: No external events. Each item is a self-contained `SftpProcessRequest` with person data, address data, and callback URL.
- **Deterministic instance IDs**: `sftp-{batchId}-{itemId}` prevents duplicate orchestrations from at-least-once queue delivery.
- **No counters on batch entity**: Callback handler queries all items by batchId to check completion, avoiding race conditions from concurrent counter updates.
- **Storage Queue**: Decouples HTTP acceptance from orchestration processing. Provides built-in retry with poison queue support.

### Data model (Table Storage)

**Table: BatchTracking**

Batch entity (`PartitionKey: "batch"`, `RowKey: "{batchId}"`):
- Status: `Processing` | `Completed` | `PartialFailure`
- ItemCount, CreatedAt, CompletedAt

Item entity (`PartitionKey: "{batchId}"`, `RowKey: "{itemId}"`):
- Status: `Queued` | `Completed` | `Failed`
- CreatedAt, CompletedAt, ErrorMessage

## Functions

| Function | Trigger | Description |
|---|---|---|
| `RunDataFeed` | Timer (daily) | Generates batch of 10 fake payments, creates batch in Table Storage, POSTs each to processor |
| `TriggerDataFeed` | HTTP POST `/api/datafeed/trigger` | Same as RunDataFeed but returns `{ batchId }` — for testing and manual use |
| `GetBatchStatus` | HTTP GET `/api/batch/{batchId}` | Returns batch + all item statuses from Table Storage |
| `BatchItemCompleted` | HTTP POST `/api/batch/callback` | Receives completion callbacks, updates Table Storage, detects batch completion |
| `ClearBatchData` | HTTP DELETE `/api/batch` | Deletes all entities in BatchTracking table (test cleanup) |
| `ReceiveSftpRequest` | HTTP POST `/api/sftp/process` | Accepts processing request, drops onto Storage Queue, returns 202 |
| `ProcessSftpQueue` | Queue `sftp-processing-queue` | Starts Durable Functions orchestration with deterministic instance ID |
| `SftpOrchestration` | Orchestration | Creates files in parallel, uploads to SFTP with retry, sends callback |
| `SftpOrchestration_ListFiles` | HTTP GET `/api/sftp/files` | Lists files on the SFTP server |
| `SftpOrchestration_GetFile` | HTTP GET `/api/sftp/files/{fileName}` | Returns the contents of a file from the SFTP server |
| `SftpOrchestration_DeleteAllFiles` | HTTP DELETE `/api/sftp/files` | Deletes all files on SFTP server (test cleanup) |

## Data Feed

The `SftpDataFeed` class (`SftpDataFeed.cs`) acts as the coordinator. It generates fake payment data using [Bogus](https://github.com/bchavez/Bogus) and submits batches for SFTP processing.

### What it does

1. Generates 10 random person + address pairs using Bogus
2. Creates a batch entity and 10 item entities in Table Storage
3. POSTs each `SftpProcessRequest` to `POST /api/sftp/process`
4. Each request includes a `callbackUrl` pointing to `/api/batch/callback`
5. As callbacks arrive, updates item status and checks batch completion
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
[SFTP] Callback received — batch <batchId>, item item-000, status=Completed.
...
[SFTP] Batch <batchId> complete — would notify third party.
```

## SFTP Orchestration

### How it works

The orchestration receives an `SftpProcessRequest` containing person data, address data, batch/item IDs, and a callback URL. It:

1. Creates person and address files in parallel
2. Uploads both files to SFTP in parallel (with retry policy: 3 attempts, 5s backoff)
3. Sends a callback to the coordinator with the result

If upload fails after retries, the orchestration cleans up temp files and sends a failure callback.

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
  ┌─────┴─────┐
  ▼           ▼
UploadFile    UploadFile              (parallel, with retry)
  └─────┬─────┘
        ▼
  SendCallback → POST /api/batch/callback
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
  "succeeded": true,
  "errorMessage": null
}
```

### Error handling

- **SFTP upload failure**: Activity retry policy (3 attempts, 5s backoff). If all fail, orchestration sends callback with `succeeded: false`.
- **Queue delivery failure**: Azure retries up to 5x, then moves to `sftp-processing-queue-poison`.
- **Callback failure**: `SendCallback` activity has its own retry policy (3 attempts, 5s backoff).
- **Partial batch**: Each item is independent. Batch marked `PartialFailure` if any items failed.
- **Idempotent callback**: Updating an already-completed item returns 200 without modification.
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
  Batch tracking: cleared 11 entities
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

SFTP connectivity is provided by [SSH.NET](https://github.com/sshnet/SSH.NET) (`Renci.SshNet`). The `SftpClient` is used in three places within `SftpOrchestration.cs`:

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
| `SFTP_REMOTE_PATH` | No | `/upload` | Remote directory for uploads |

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
