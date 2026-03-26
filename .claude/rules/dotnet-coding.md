---
globs: "*.cs"
description: .NET coding standards and safety guardrails for C# source files
---

# .NET Coding Standards

These rules apply when editing `.cs` files.

## Naming

- **PascalCase** for public types, methods, properties, and constants
- **camelCase** for local variables, parameters, and private fields
- Use `nameof()` for function names in attributes and string references
- Use `const` for string literals referenced in multiple places (e.g., event names)

## Types

- Use `record` types for DTOs and data transfer objects
- Use instance classes with constructor injection for Azure Function containers (isolated worker supports DI)
- Enable nullable reference types (`<Nullable>enable</Nullable>`)

## Null Handling

Use the `?? throw` pattern for required values. Never use the `!` null-forgiving operator to suppress nullable warnings on values that could actually be null.

```csharp
// Correct
string host = Environment.GetEnvironmentVariable("SFTP_HOST")
    ?? throw new InvalidOperationException("SFTP_HOST not configured.");

// Incorrect — silently becomes null at runtime
string host = Environment.GetEnvironmentVariable("SFTP_HOST")!;
```

For HTTP request deserialization, null-check and return 400:

```csharp
var data = await req.ReadFromJsonAsync<MyType>();
if (data is null)
{
    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
    await badRequest.WriteStringAsync("Invalid or missing data.");
    return badRequest;
}
```

## Environment Variables

Access via `Environment.GetEnvironmentVariable()`. Use `?? throw` for required settings and `?? "default"` for optional settings with defaults.

## External Connections (SFTP, HTTP, etc.)

- Use `using var` for disposable clients — do not call `Disconnect()` explicitly
- Set `OperationTimeout` to prevent indefinite hangs
- Clean up temporary files after use
- Validate user-supplied paths and filenames (reject `..`, `/`, `\`)

## Durable Functions

- Use `context.CreateReplaySafeLogger()` in orchestrators — prevents duplicate logs during replay
- Use `executionContext.GetLogger()` in activities and HTTP triggers
- Prefix all log messages with `[Batch]` for filtering (`[SFTP]` for SFTP-specific endpoints in `SftpEndpoints.cs`)
- Use `LogInformation` for milestones, `LogWarning` for non-fatal issues, `LogError` for failures

## Application Insights

Configured in `host.json`:
- Sampling enabled (reduces telemetry volume in production)
- `Request` type excluded from sampling (all requests are captured)
- Live Metrics filters enabled

## Before Committing

1. `dotnet build` — zero warnings, zero errors
2. Run E2E test if changes affect orchestration flow
