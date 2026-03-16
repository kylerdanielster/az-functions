# CLAUDE.md

This file provides core guidance to Claude Code.

## Language & Toolchain

C# on .NET 10. Azure Functions v4 isolated worker model. SSH.NET for SFTP.

## Architecture

Single Durable Functions orchestration (`SftpOrchestration`) using the **external events** pattern:

1. `POST /api/sftp/start` — creates orchestration, returns instance ID
2. Orchestrator waits on two `WaitForExternalEvent` calls (person + address)
3. `Task.WhenAll` fan-in — both events must arrive before proceeding
4. Person and address files created as parallel activities
5. Both files uploaded to SFTP after creation completes

**State storage**: Azure Storage (Azurite locally) via `AzureWebJobsStorage` + `host.json` durable task config.

**SFTP**: SSH.NET (`Renci.SshNet`). Local server via OpenSSH Docker container (port 2222).

**File layout**:
```
Program.cs                    Entry point and DI configuration
SftpOrchestration.cs          Orchestrator, activities, HTTP triggers
SftpDataFeed.cs               Timer trigger — generates data and starts orchestration on app startup
host.json                     Azure Functions and durable task config
docker-compose.yml            Azurite + SFTP containers for local dev
tools/generate-test-data/     Bogus-based fake data generator
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
