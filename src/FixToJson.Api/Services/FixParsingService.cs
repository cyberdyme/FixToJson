using System.Reflection;
using System.Text.RegularExpressions;
using QuickFix;
using QuickFix.DataDictionary;

namespace FixToJson.Api.Services;

public sealed partial class FixParsingService
{
    private const char SOH = '\x01';

    private readonly ILogger<FixParsingService> _logger;
    private readonly IMessageFactory _messageFactory = new DefaultMessageFactory();

    /// <summary>
    /// Pre-loaded data dictionaries keyed by BeginString (e.g. "FIX.4.4").
    /// Used to resolve numeric tags to human-readable field names in ToJSON().
    /// </summary>
    private static readonly Dictionary<string, DataDictionary> Dictionaries = LoadAllDictionaries();

    // Matches a pipe that is followed by one or more digits then '=' (i.e. a FIX tag boundary).
    // This distinguishes field-delimiter pipes from pipes embedded inside field values.
    // Examples:
    //   "448=Glencore|Glencore|447=D"
    //        pipe 1 ─┘         └─ pipe 2
    //   pipe 1: followed by "Glencore|" → NOT digits+equals → leave as-is
    //   pipe 2: followed by "447="      → IS  digits+equals → replace with SOH
    [GeneratedRegex(@"\|(?=\d+=)")]
    private static partial Regex PipeDelimiterPattern();

    public FixParsingService(ILogger<FixParsingService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Loads all embedded FIX data dictionary XML files at startup.
    /// </summary>
    private static Dictionary<string, DataDictionary> LoadAllDictionaries()
    {
        var dict = new Dictionary<string, DataDictionary>(StringComparer.OrdinalIgnoreCase);
        var assembly = Assembly.GetExecutingAssembly();

        // Map embedded resource names to their FIX BeginString values
        var mappings = new Dictionary<string, string>
        {
            { "FIX40.xml", "FIX.4.0" },
            { "FIX41.xml", "FIX.4.1" },
            { "FIX42.xml", "FIX.4.2" },
            { "FIX43.xml", "FIX.4.3" },
            { "FIX44.xml", "FIX.4.4" },
            { "FIX50.xml", "FIX.5.0" },
            { "FIX50SP1.xml", "FIXT.1.1" },  // FIX 5.0 SP1 uses FIXT.1.1 transport
            { "FIX50SP2.xml", "FIX.5.0SP2" },
        };

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            // Resource names look like: FixToJson.Api.DataDictionaries.FIX44.xml
            var fileName = resourceName.Split('.').Length >= 2
                ? string.Join(".", resourceName.Split('.')[^2..]) // e.g. "FIX44.xml"
                : resourceName;

            if (!mappings.TryGetValue(fileName, out var beginString))
                continue;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;

            var dd = new DataDictionary(stream);
            dict[beginString] = dd;
        }

        return dict;
    }

    /// <summary>
    /// Normalises the FIX string by replacing pipe delimiters with SOH.
    /// Only replaces pipes that are followed by a FIX tag pattern (digits + '='),
    /// preserving pipes that appear inside field values.
    /// </summary>
    public static string NormaliseDelimiters(string fixMessage)
    {
        // Only normalise if the message contains pipes but no SOH
        if (fixMessage.Contains('|') && !fixMessage.Contains(SOH))
        {
            // Replace pipes that precede a tag (digits followed by '=')
            fixMessage = PipeDelimiterPattern().Replace(fixMessage, SOH.ToString());

            // Handle trailing pipe (after the last field, e.g. "10=215|")
            // — this pipe is NOT followed by digits+= so the regex won't catch it.
            if (fixMessage.Length > 0 && fixMessage[^1] == '|')
            {
                fixMessage = fixMessage[..^1] + SOH;
            }
        }

        // Ensure the message ends with SOH (required by QuickFIX parser)
        if (fixMessage.Length > 0 && fixMessage[^1] != SOH)
        {
            fixMessage += SOH;
        }

        return fixMessage;
    }

    /// <summary>
    /// Validates minimum FIX message structure.
    /// Returns a list of validation errors (empty if valid).
    /// </summary>
    public static List<string> ValidateStructure(string normalisedFix)
    {
        var errors = new List<string>();

        if (!normalisedFix.StartsWith($"8=", StringComparison.Ordinal))
            errors.Add("Missing BeginString (tag 8) — must be the first field.");

        if (!normalisedFix.Contains($"{SOH}9=", StringComparison.Ordinal))
            errors.Add("Missing BodyLength (tag 9).");

        if (!normalisedFix.Contains($"{SOH}35=", StringComparison.Ordinal))
            errors.Add("Missing MsgType (tag 35).");

        if (!normalisedFix.Contains($"{SOH}10=", StringComparison.Ordinal))
            errors.Add("Missing CheckSum (tag 10).");

        return errors;
    }

    /// <summary>
    /// Parses a normalised FIX string and returns the JSON representation
    /// with human-readable field names (e.g. "BeginString" instead of "8").
    /// </summary>
    public string ParseToJson(string normalisedFix, bool showOnlyTags)
    {
        var msg = new Message();

        // Parse the message. Pass validate: false because the checksum
        // may have been computed against the original delimiter form.
        msg.FromString(normalisedFix, validate: false, transportDict: null, appDict: null, msgFactory: _messageFactory);

        var beginString = msg.Header.GetString(QuickFix.Fields.Tags.BeginString);
        var msgType = msg.Header.GetString(QuickFix.Fields.Tags.MsgType);

        _logger.LogDebug("Parsed FIX message: BeginString={BeginString}, MsgType={MsgType}",
            beginString, msgType);

        // Look up the data dictionary for this FIX version to get field names
        DataDictionary? dd = null;
        if (Dictionaries.TryGetValue(beginString, out var found))
        {
            dd = found;
            _logger.LogDebug("Using data dictionary for {BeginString}", beginString);
        }
        else
        {
            _logger.LogWarning(
                "No data dictionary found for BeginString '{BeginString}'. " +
                "JSON output will use numeric tag IDs instead of field names.",
                beginString);
        }

        return showOnlyTags ? msg.ToJSON() :
            // Use QuickFIX/n's built-in ToJSON with the data dictionary
            // to produce human-readable field names and enum descriptions.
            msg.ToJSON(dataDictionary: dd, convertEnumsToDescriptions: dd is not null);
    }
}
