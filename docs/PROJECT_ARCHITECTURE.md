# Project Architecture

## Runtime

- **.NET 10** with **Azure Functions v4** isolated worker model
- Uses `FunctionsApplication.CreateBuilder()` entry point pattern
- Application Insights for telemetry

## Durable Functions Orchestration

The project uses a single orchestration (`SftpOrchestration`) with the **external events** pattern:

1. **Start** — HTTP trigger creates an orchestration instance, returns an instance ID
2. **Wait** — Orchestrator pauses on two `WaitForExternalEvent` calls (person + address)
3. **Fan-in** — `Task.WhenAll` ensures both events arrive before proceeding
4. **Parallel activities** — Person and address files are created concurrently
5. **Upload** — Both files are uploaded to SFTP after creation completes

### State Storage

Durable task state is stored in Azure Storage (Azurite locally), configured via `AzureWebJobsStorage` in `local.settings.json` and `host.json`.

## SFTP Integration

- **Library**: SSH.NET (`Renci.SshNet`)
- **Pattern**: Create `SftpClient` with `using var`, set `OperationTimeout`, connect, operate, let disposal handle disconnect
- **Local server**: OpenSSH container via Docker Compose (port 2222)

## File Layout

```
Program.cs                    Entry point and DI configuration
SftpOrchestration.cs          All functions: orchestrator, activities, HTTP triggers
host.json                     Azure Functions and durable task configuration
docker-compose.yml            Azurite + SFTP containers for local dev
tools/generate-test-data/     Bogus-based fake data generator
test-sftp-orchestration.sh    E2E test script
```

## Function Responsibilities

| Function | Type | Purpose |
|----------|------|---------|
| `SftpOrchestration` | Orchestrator | Coordinates the workflow |
| `CreatePersonFile` | Activity | Writes person data to a temp file |
| `CreateAddressFile` | Activity | Writes address data to a temp file |
| `UploadFiles` | Activity | Uploads files to SFTP, cleans up temp files |
| `SftpOrchestration_Start` | HTTP trigger | Creates new orchestration instance |
| `SftpOrchestration_Person` | HTTP trigger | Raises person event on an instance |
| `SftpOrchestration_Address` | HTTP trigger | Raises address event on an instance |
| `SftpOrchestration_ListFiles` | HTTP trigger | Lists files on the SFTP server |
| `SftpOrchestration_GetFile` | HTTP trigger | Downloads a file from the SFTP server |
