namespace FixToJson.Api.Models;

/// <summary>
/// Request body for the FIX-to-JSON conversion endpoint.
/// </summary>
public sealed class FixRequest
{
    /// <summary>
    /// The raw FIX message string. Supports SOH (\x01) and pipe (|) delimiters.
    /// </summary>
    public string? Fix { get; set; }


    /// <summary>
    /// If true, return JSON with numeric tag IDs only (no human-readable field names).
    /// Defaults to false.
    /// </summary>
    public bool ShowOnlyTags { get; set; } = false;
}
