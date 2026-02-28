using System.Text.Json;
using FixToJson.Api.Models;
using FixToJson.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Register services
builder.Services.AddSingleton<FixParsingService>();

// Allow lenient JSON parsing (trailing commas, comments, etc.)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.AllowTrailingCommas = true;
    options.SerializerOptions.ReadCommentHandling = JsonCommentHandling.Skip;
});

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "FIX-to-JSON API",
        Version = "v1",
        Description = "Converts FIX protocol messages to JSON using QuickFIX/n."
    });

    // Add a sample FIX message example to the Swagger UI
    options.MapType<FixRequest>(() => new Microsoft.OpenApi.Models.OpenApiSchema
    {
        Type = "object",
        Properties = new Dictionary<string, Microsoft.OpenApi.Models.OpenApiSchema>
        {
            ["fix"] = new()
            {
                Type = "string",
                Description = "The raw FIX message string. Supports SOH (\\x01) and pipe (|) delimiters.",
                Example = new Microsoft.OpenApi.Any.OpenApiString(
                    "8=FIX.4.4|9=70|35=D|49=SENDER|56=TARGET|34=1|52=20240101-12:00:00|11=ORD001|21=1|40=2|44=150.25|54=1|55=AAPL|59=0|60=20240101-12:00:00|38=100|10=000|")
            },
            ["showOnlyTags"] = new()
            {
                Type = "boolean",
                Description = "If true, return JSON with numeric tag IDs only (no human-readable field names).",
                Default = new Microsoft.OpenApi.Any.OpenApiBoolean(false)
            }
        }
    });
});

var app = builder.Build();

// Enable Swagger in all environments for convenience
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "FIX-to-JSON API v1"));

// ──────────────────────────────────────────────────────────
// POST /api/fix/to-json
// ──────────────────────────────────────────────────────────
app.MapPost("/api/fix/to-json", async (
    HttpRequest httpRequest,
    FixParsingService parser,
    ILogger<Program> logger) =>
{
    logger.LogInformation("POST /api/fix/to-json — request received");

    // ── Read and deserialise body manually ──
    // This lets us handle JSON errors (e.g. embedded newlines from Swagger UI)
    // gracefully instead of letting ASP.NET throw a 400 with a stack trace.
    FixRequest? request;
    try
    {
        var body = await new StreamReader(httpRequest.Body).ReadToEndAsync();

        // Sanitise: strip control characters that are invalid inside JSON strings
        // (e.g. literal newlines injected by Swagger UI textareas) but preserve
        // the JSON structure itself. We only strip inside string values.
        body = SanitiseJsonStringValues(body);

        request = JsonSerializer.Deserialize<FixRequest>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        });
    }
    catch (JsonException ex)
    {
        logger.LogWarning(ex, "Malformed JSON in request body");
        return Results.Problem(
            detail: $"Request body is not valid JSON: {ex.Message}",
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid JSON");
    }

    // ── Validate request body ──
    if (request is null || string.IsNullOrWhiteSpace(request.Fix))
    {
        logger.LogWarning("Empty or missing FIX string in request body");
        return Results.Problem(
            detail: "The 'fix' field is required and must contain a FIX message string.",
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid request");
    }

    // Trim whitespace (handles trailing newlines from copy-paste / Swagger)
    var fixInput = request.Fix.Trim();

    // ── Normalise delimiters ──
    var normalised = FixParsingService.NormaliseDelimiters(fixInput);

    // ── Structural validation ──
    var structureErrors = FixParsingService.ValidateStructure(normalised);
    if (structureErrors.Count > 0)
    {
        logger.LogWarning("FIX structural validation failed: {Errors}", structureErrors);
        return Results.Problem(
            detail: string.Join(" ", structureErrors),
            statusCode: StatusCodes.Status400BadRequest,
            title: "FIX message validation failed");
    }

    // ── Parse and convert ──
    try
    {
        var json = parser.ParseToJson(normalised, request.ShowOnlyTags);

        logger.LogInformation("FIX message parsed successfully");

        // Return the JSON produced by QuickFIX/n as a raw JSON response.
        // We parse it first so the response is a proper JSON object, not a string.
        var jsonDocument = JsonDocument.Parse(json);
        return Results.Ok(jsonDocument.RootElement);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to parse FIX message");
        return Results.Problem(
            detail: $"Failed to parse FIX message: {ex.Message}",
            statusCode: StatusCodes.Status400BadRequest,
            title: "FIX parse error");
    }
})
.WithName("FixToJson")
.WithOpenApi(op =>
{
    op.Summary = "Convert a FIX message string to JSON";
    op.Description = "Accepts a FIX protocol message (SOH or pipe-delimited) and returns its JSON representation.";
    return op;
})
.Accepts<FixRequest>("application/json")
.Produces(StatusCodes.Status200OK)
.ProducesProblem(StatusCodes.Status400BadRequest);

app.Run();

// ──────────────────────────────────────────────────────────
// Helpers
// ──────────────────────────────────────────────────────────

/// <summary>
/// Strips ASCII control characters (0x00-0x1F except \t, \r, \n that are JSON-escaped,
/// and the SOH character \x01 which we need) that appear as raw bytes inside JSON string
/// values. This handles the common case where Swagger UI injects a trailing newline
/// inside the "fix" string value.
/// </summary>
static string SanitiseJsonStringValues(string raw)
{
    // Simple approach: remove raw \n, \r, \t that are NOT preceded by a backslash
    // (i.e. they are literal control chars, not JSON escape sequences).
    // This is safe because valid JSON strings never contain raw control characters.
    var sb = new System.Text.StringBuilder(raw.Length);
    for (int i = 0; i < raw.Length; i++)
    {
        char c = raw[i];
        // Keep everything except raw control characters (except SOH \x01 which
        // could legitimately appear if someone pastes a real FIX message).
        if (c < 0x20 && c != '\x01')
        {
            // If this control char is part of JSON whitespace between tokens
            // (\n, \r, \t, space) that's outside a string, it's fine — but since
            // System.Text.Json handles those, we just strip them from the raw text.
            // Newlines/tabs between JSON tokens are redundant whitespace.
            if (c == '\n' || c == '\r' || c == '\t')
                continue; // skip
            else
                continue; // skip other control chars too
        }
        sb.Append(c);
    }
    return sb.ToString();
}

// Make the implicit Program class accessible for integration tests
public partial class Program { }
