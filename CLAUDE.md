# CLAUDE.md

This file provides core guidance to Claude Code.

## Language & Toolchain

C# on .NET 10. Azure Functions v4 isolated worker model. SSH.NET for SFTP.

## Architecture

Two-app architecture for batch payment processing using HTTP + Storage Queue + callback:

**App 1: Coordinator** (`SftpDataFeed.cs`)
1. `RunDataFeed` (Timer) / `TriggerDataFeed` (HTTP) — generates 10 fake payments via Bogus, creates batch in Table Storage, POSTs each item to App 2
2. `BatchItemCompleted` (HTTP callback) — receives completion callbacks, updates Table Storage, detects batch completion
3. `GetBatchStatus` (HTTP GET) — returns batch + item statuses from Table Storage
4. `ClearBatchData` (HTTP DELETE) — clears BatchTracking table (test cleanup)

**App 2: SFTP Processor** (`SftpProcessor.cs` + `SftpOrchestration.cs`)
1. `ReceiveSftpRequest` (HTTP) — accepts POST with payments + callbackUrl, drops onto Storage Queue, returns 202
2. `ProcessSftpQueue` (Queue Trigger) — starts Durable Functions orchestration with deterministic ID (`sftp-{batchId}-{itemId}`)
3. `SftpOrchestration` (Orchestrator) — creates person + address files in parallel, uploads to SFTP with retry, sends callback

**Batch tracking**: Azure Table Storage (`BatchTracking` table) via `IBatchTracker` / `TableBatchTracker`. Batch entity + item entities per batch.

**Storage**: All services (Table Storage, Queue, Durable Functions state) use the single `AzureWebJobsStorage` connection string. Locally → Azurite. In production → dedicated storage account separate from the function app's built-in storage.

**SFTP**: SSH.NET (`Renci.SshNet`). Local server via OpenSSH Docker container (port 2222).

**File layout**:
```
Program.cs                    Entry point and DI configuration
Models.cs                     Shared records (data, request/response, batch status)
BatchTracking.cs              IBatchTracker interface + TableBatchTracker implementation
SftpProcessor.cs              HTTP receiver + queue trigger (App 2 entry points)
SftpOrchestration.cs          Orchestrator, file creation/upload activities, callback, SFTP endpoints
SftpDataFeed.cs               Timer/HTTP triggers, batch status, callback webhook (App 1)
host.json                     Azure Functions, durable task, and queue config
docker-compose.yml            Azurite + SFTP containers for local dev
test-sftp-orchestration.sh    E2E test script
```

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
dotnet build          # build the project
dotnet restore        # restore NuGet packages
func start            # run locally (requires Docker services)
docker compose up -d  # start Azurite + SFTP containers
```

## Auto-Loaded Rules (`.claude/rules/`)

These load automatically based on context — no action needed:

- `dotnet-coding.md` — loaded when editing `.cs` files. Null safety, naming, Durable Functions patterns, logging.
- `testing.md` — loaded when working with test files. E2E test flow, verification steps.
- `git-workflow.md` — loaded for git operations. Branch naming, commit format, PR guidelines.

## Useful Resources

- TODO: As necessary
