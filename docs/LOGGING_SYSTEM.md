# Logging System

## Log Prefix

All log messages use the `[SFTP]` prefix for easy filtering:

```csharp
logger.LogInformation("[SFTP] Orchestration {id} started.", id);
```

## Logger Creation

- **Orchestrator functions**: Use `context.CreateReplaySafeLogger()` to prevent duplicate log entries during Durable Functions replays
- **Activity functions and HTTP triggers**: Use `executionContext.GetLogger("FunctionName")`

## Application Insights

Configured in `host.json`:

- Sampling enabled (reduces telemetry volume in production)
- `Request` type excluded from sampling (all requests are captured)
- Live Metrics filters enabled

## Log Levels

- `LogInformation` — Normal operation milestones (connected, uploaded, completed)
- `LogWarning` — Non-fatal issues (failed to delete temp file)
- `LogError` — Failures that prevent operation completion
