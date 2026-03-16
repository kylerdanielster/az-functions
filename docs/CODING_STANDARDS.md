# Coding Standards

## Naming

- **PascalCase** for public types, methods, properties, and constants
- **camelCase** for local variables, parameters, and private fields
- Use `nameof()` for function names in attributes and string references

## Types

- Use `record` types for DTOs and data transfer objects
- Use `static class` for Azure Function containers (no instance state)
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

Access environment variables with `Environment.GetEnvironmentVariable()`. Use `?? throw` for required settings and `?? "default"` for optional settings with defaults.

## External Connections (SFTP, HTTP, etc.)

- Use `using var` for disposable clients — do not call `Disconnect()` explicitly
- Set `OperationTimeout` to prevent indefinite hangs
- Clean up temporary files after use
