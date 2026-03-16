# az-functions

Azure Functions v4 project (.NET 10, isolated worker model) with a Durable Functions orchestration that collects person and address data via HTTP, creates files, and uploads them to an SFTP server.

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
    "ORCHESTRATION_BASE_URL": "http://localhost:7071/api"
  }
}
```

### 3. Start Docker services

The project uses Docker Compose for local dependencies:

- **Azurite** — Azure Storage emulator (required for durable functions)
- **OpenSSH Server** — local SFTP server for testing file uploads

```bash
docker compose up -d
```

### 4. Run the function app

```bash
func start
```

The app runs on port 7071.

## Functions

| Function | Trigger | Description |
|---|---|---|
| `SftpOrchestration_Start` | HTTP POST `/api/sftp/start` | Starts the SFTP orchestration, returns instance ID |
| `SftpOrchestration_Person` | HTTP POST `/api/sftp/person/{instanceId}` | Sends person data to a running orchestration |
| `SftpOrchestration_Address` | HTTP POST `/api/sftp/address/{instanceId}` | Sends address data to a running orchestration |
| `SftpOrchestration_ListFiles` | HTTP GET `/api/sftp/files` | Lists files on the SFTP server |
| `SftpOrchestration_GetFile` | HTTP GET `/api/sftp/files/{fileName}` | Returns the contents of a file from the SFTP server |
| `RunDataFeed` | Timer (runs on app startup) | Generates fake data and triggers the full orchestration pipeline |

## Data Feed

The `SftpDataFeed` function (`SftpDataFeed.cs`) is a timer trigger that fires on app startup (`RunOnStartup = true`). It drives the full orchestration pipeline by generating fake data and sending it via HTTP to the orchestration endpoints.

In production, this function will live in a separate function app and pull data from an external service. For now, it uses [Bogus](https://github.com/bchavez/Bogus) to generate random person and address data as a placeholder. It communicates with the orchestration exclusively over HTTP, so it's already decoupled and ready for separate deployment.

### What it does

1. Generates a random `PersonData` and `AddressData` using Bogus
2. `POST /api/sftp/start` — starts a new orchestration, extracts the instance ID
3. `POST /api/sftp/person/{instanceId}` — sends person data
4. `POST /api/sftp/address/{instanceId}` — sends address data
5. The orchestration completes asynchronously (file creation, SFTP upload)

### Configuration

| Variable | Required | Description |
|---|---|---|
| `ORCHESTRATION_BASE_URL` | Yes | Base URL of the orchestration function app (e.g., `http://localhost:7071/api`) |

### Expected log output on startup

```
[SFTP] Data feed starting — person: <first> <last>, address: <street>, <city>.
[SFTP] Data feed orchestration <id> created.
[SFTP] Data feed orchestration <id> — person data sent.
[SFTP] Data feed orchestration <id> — address data sent, orchestration will complete asynchronously.
```

## SFTP Orchestration

### What is a Durable Function?

Azure Durable Functions extend Azure Functions with stateful workflows. The runtime automatically manages state, checkpoints, and restarts. Unlike regular (stateless) functions that process a single request and terminate, durable functions can pause, wait for external input, and resume — even across process restarts or infrastructure failures.

### Why Durable Functions for this use case?

This project needs to:

- Accept two separate HTTP requests (person data and address data) that arrive independently and in any order
- Guarantee the two pieces of data are paired correctly so concurrent sessions don't mix up person/address records
- Wait until both pieces of data arrive before proceeding
- Only trigger the SFTP upload after both files are created

A stateless function can't do this — it processes a single request and has no way to wait for a second one. Durable Functions solve this with the **external events** pattern, where an orchestration can pause and wait for named events raised by other functions.

### Key patterns used

**External Events** — The orchestration uses `WaitForExternalEvent<T>()` to pause and wait for data from separate HTTP calls. This is how we decouple the two requests while keeping them correlated to the same orchestration instance.

**Fan-out/Fan-in (parallel activities)** — Once both events arrive, person and address file creation runs in parallel via `Task.WhenAll`. The SFTP upload only starts after both activities complete.

**Instance ID as correlation key** — The caller gets a unique instance ID from `POST /api/sftp/start` and includes it in subsequent requests (`/api/sftp/person/{instanceId}` and `/api/sftp/address/{instanceId}`). This ensures person and address data are always paired correctly, even when multiple sessions are running concurrently.

### Architecture decisions

- **Two separate endpoints** (person + address) rather than one combined payload — keeps request contracts explicit and strongly typed. Each endpoint knows exactly what data it expects.
- **Explicit `/start` endpoint** rather than auto-starting on the first data request — provides a clearer orchestration lifecycle with no race conditions between the two data submissions.
- **External events** rather than queue-based fan-in — simpler architecture with no extra infrastructure needed beyond what Durable Functions provides.
- **Parallel file creation** rather than sequential — person and address files have no dependency on each other. Only the SFTP upload depends on both being complete.

### Request flow

```
POST /api/sftp/start
  └─► Creates orchestration, returns instanceId
        │
        ├── POST /api/sftp/person/{instanceId}    (any order)
        │     └─► Raises "PersonReceived" event
        │
        └── POST /api/sftp/address/{instanceId}   (any order)
              └─► Raises "AddressReceived" event
                    │
                    ▼
              Orchestrator resumes (both events received)
                    │
              ┌─────┴─────┐
              ▼           ▼
        CreatePersonFile  CreateAddressFile   (parallel)
              └─────┬─────┘
                    ▼
              UploadFile (per file, with retry)
```

### Example usage

```bash
# 1. Start the orchestration
curl -X POST http://localhost:7071/api/sftp/start
# Returns 202 with instanceId in the response body

# 2. Send person data (use the instanceId from step 1)
curl -X POST http://localhost:7071/api/sftp/person/{instanceId} \
  -H "Content-Type: application/json" \
  -d '{"firstName":"John","lastName":"Doe","dateOfBirth":"1990-01-15"}'

# 3. Send address data (can be sent before or after person data)
curl -X POST http://localhost:7071/api/sftp/address/{instanceId} \
  -H "Content-Type: application/json" \
  -d '{"street":"123 Main St","city":"Springfield","state":"IL","zipCode":"62701"}'
```

### Request schemas

**Person** (`POST /api/sftp/person/{instanceId}`):
```json
{
  "firstName": "string",
  "lastName": "string",
  "dateOfBirth": "string"
}
```

**Address** (`POST /api/sftp/address/{instanceId}`):
```json
{
  "street": "string",
  "city": "string",
  "state": "string",
  "zipCode": "string"
}
```

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
cat ./sftp-data/person_abc123.txt
```

**Via SSH into the container:**

```bash
ssh -p 2222 testuser@localhost
# password: testpass
ls /config/upload/
```

### Testing end-to-end

There's a test script that runs the full flow automatically:

```bash
# Make sure Docker services and func are running first
docker compose up -d
func start  # in another terminal

# Run the test
./test-sftp-orchestration.sh
```

The script will:
1. Start an orchestration and display the instance ID
2. Send randomly generated person data (via Bogus)
3. Send randomly generated address data (via Bogus)
4. Poll until the orchestration completes
5. Show the total file count on the SFTP server
6. Print the contents of the two files uploaded by this run (matched by instance ID)

**Expected console output from `func start`:**

```
[SFTP] Orchestration <id> created.
[SFTP] Orchestration <id> started — waiting for person and address data.
[SFTP] Orchestration <id> — person data received (<first> <last>).
[SFTP] Orchestration <id> — address data received (<street>, <city>).
[SFTP] Orchestration <id> — creating files...
[SFTP] Created person file at /tmp/person_<id>.txt.
[SFTP] Created address file at /tmp/address_<id>.txt.
[SFTP] Orchestration <id> — uploading 2 files to SFTP server...
[SFTP] Connected to SFTP server localhost:2222.
[SFTP] Uploaded person_<id>.txt to /config/upload/person_<id>.txt.
[SFTP] Uploaded address_<id>.txt to /config/upload/address_<id>.txt.
[SFTP] Orchestration <id> — complete!
```

**Expected uploaded file contents:**

`person_<id>.txt`:
```
First Name: <randomly generated>
Last Name: <randomly generated>
Date of Birth: <randomly generated>
```

`address_<id>.txt`:
```
Street: <randomly generated>
City: <randomly generated>
State: <randomly generated>
Zip Code: <randomly generated>
```

### Testing upload retry

There's a separate script that verifies the retry policy works by stopping the SFTP container, triggering the orchestration, then restarting SFTP within the retry window:

```bash
./test-sftp-retry.sh
```

The script will stop the SFTP container, start an orchestration, wait for the first upload attempt to fail, restart SFTP, and verify the orchestration completes via retry.

### Manual step-by-step testing

If you prefer to test manually, run each curl command one at a time:

```bash
# 1. Start the orchestration
curl -s -X POST http://localhost:7071/api/sftp/start | python3 -m json.tool
# Copy the "id" value from the response

# 2. Send person data (replace INSTANCE_ID)
curl -s -X POST http://localhost:7071/api/sftp/person/INSTANCE_ID \
  -H "Content-Type: application/json" \
  -d '{"firstName":"John","lastName":"Doe","dateOfBirth":"1990-01-15"}' | python3 -m json.tool

# 3. Send address data (replace INSTANCE_ID)
curl -s -X POST http://localhost:7071/api/sftp/address/INSTANCE_ID \
  -H "Content-Type: application/json" \
  -d '{"street":"123 Main St","city":"Springfield","state":"IL","zipCode":"62701"}' | python3 -m json.tool

# 4. Check orchestration status (use statusQueryGetUri from step 1 response)
curl -s "STATUS_QUERY_URL" | python3 -m json.tool

# 5. List files on the SFTP server
curl -s http://localhost:7071/api/sftp/files | python3 -m json.tool

# 6. Read a specific file
curl -s http://localhost:7071/api/sftp/files/person_xxx.txt
```

## SFTP via SSH.NET

SFTP connectivity is provided by [SSH.NET](https://github.com/sshnet/SSH.NET) (`Renci.SshNet`), a .NET library for SSH and SFTP operations. The project uses its `SftpClient` class to connect, upload, list, and read files on a remote SFTP server.

### How it's used

The `SftpClient` is created in three places, all within `SftpOrchestration.cs`:

- **`UploadFile` activity** — connects to the SFTP server, uploads a single file to the configured remote path, then deletes the local temp file. Called once per file with a retry policy (3 attempts, 5s backoff).
- **`ListFiles` HTTP trigger** — connects and lists all files in the remote upload directory.
- **`GetFile` HTTP trigger** — connects and reads the contents of a specific file by name.

Each usage follows the same pattern:

```csharp
using var client = new SftpClient(host, port, username, password);
client.OperationTimeout = TimeSpan.FromSeconds(30);
client.Connect();
// ... upload, list, or read files
// client is disposed (and disconnected) by the using statement
```

### Configuration

Connection details are read from environment variables (set in `local.settings.json` for local dev):

| Variable | Required | Default | Description |
|---|---|---|---|
| `SFTP_HOST` | Yes | — | SFTP server hostname |
| `SFTP_PORT` | No | `22` | SFTP server port |
| `SFTP_USERNAME` | Yes | — | Login username |
| `SFTP_PASSWORD` | Yes | — | Login password |
| `SFTP_REMOTE_PATH` | No | `/upload` | Remote directory for uploads |

### Local development

For local testing, the Docker Compose setup includes an OpenSSH server container that acts as the SFTP server (see Docker Services below). The `local.settings.json` values point to this container (`localhost:2222`, user `testuser`, password `testpass`).

## Docker Services

| Service | Image | Ports | Purpose |
|---|---|---|---|
| azurite | `mcr.microsoft.com/azure-storage/azurite` | 10000, 10001, 10002 | Azure Storage emulator (durable functions state) |
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
