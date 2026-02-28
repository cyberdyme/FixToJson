# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run Commands

```bash
dotnet build FixToJson.sln          # Build everything
dotnet run --project src/FixToJson.Api  # Run API on http://localhost:5000 (Swagger at /swagger)
dotnet test FixToJson.sln           # Run all tests
dotnet test tests/FixToJson.Api.Tests --filter "FullyQualifiedName~MethodName"  # Run a single test
```

Requires .NET 9 SDK.

## Architecture

This is a .NET 9 Minimal API that converts FIX protocol messages to JSON using QuickFIX/n.

**Single endpoint:** `POST /api/fix/to-json` — accepts `{"fix": "...", "showOnlyTags": false}` and returns JSON with Header/Body/Trailer sections.

### Key Components

- **`Program.cs`** — Minimal API setup, the endpoint handler, and a `SanitiseJsonStringValues` helper that strips raw control characters from request JSON (handles Swagger UI injecting newlines).
- **`Services/FixParsingService.cs`** — Core parsing logic. Three static methods (`NormaliseDelimiters`, `ValidateStructure`, `ParseToJson`). Registered as a singleton.
- **`Models/FixRequest.cs`** — Request DTO with `Fix` (the FIX message string) and `ShowOnlyTags` (bool, controls whether output uses numeric tags or human-readable names).

### FIX Parsing Pipeline

1. **Sanitise** — Strip control chars from raw JSON body
2. **Normalise** — Convert pipe delimiters to SOH (`\x01`). Uses a regex `\|(?=\d+=)` to only replace pipes at tag boundaries, preserving pipes embedded in field values (e.g. `448=Glencore|Glencore`)
3. **Validate structure** — Check for required tags 8, 9, 35, 10
4. **Parse** — QuickFIX/n `Message.FromString()` with `validate: false` (checksum is unreliable after delimiter conversion)
5. **Convert** — `Message.ToJSON()` with an optional `DataDictionary` for human-readable field names and enum descriptions

### Data Dictionaries

FIX data dictionary XML files are embedded resources in `src/FixToJson.Api/DataDictionaries/`. They are loaded once at startup into a static dictionary keyed by BeginString (e.g. `FIX.4.4`). Supports FIX 4.0–5.0SP2.

### Tests

xUnit integration tests (`EndpointTests`) use `WebApplicationFactory<Program>` to test the full HTTP pipeline. Unit tests (`FixParsingServiceTests`) test normalisation, validation, and parsing directly. The `public partial class Program` declaration at the bottom of `Program.cs` exists to make the implicit Program class accessible to the test project.

### SOH Character Note

In C# test code, use `\u0001` (not `\x01`) for the SOH character to avoid greedy hex-escape issues where `\x019` would be parsed as U+0019 instead of SOH + '9'.
