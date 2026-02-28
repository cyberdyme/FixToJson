using FixToJson.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FixToJson.Api.Tests;

public class FixParsingServiceTests
{
    // Use \u0001 instead of \x01 to avoid greedy hex-escape issues in C#
    // (\x019 would be parsed as \x019 = U+0019, not SOH + '9')
    private const char SOH = '\u0001';

    private readonly FixParsingService _service = new(NullLogger<FixParsingService>.Instance);

    // ── Delimiter normalisation ──

    [Fact]
    public void NormaliseDelimiters_ReplacesPipeWithSoh()
    {
        const string input = "8=FIX.4.4|9=5|35=0|10=000|";
        var result = FixParsingService.NormaliseDelimiters(input);

        Assert.DoesNotContain("|", result);
        Assert.Contains(SOH, result);
        Assert.StartsWith($"8=FIX.4.4{SOH}", result);
    }

    [Fact]
    public void NormaliseDelimiters_PreservesPipesInsideFieldValues()
    {
        // "448=Glencore|Glencore|447=D" — the first pipe is inside a value,
        // only the second pipe (before "447=") is a field delimiter.
        const string input = "8=FIX.4.4|9=10|35=AE|448=SomeCompany|InternalCompany|447=D|10=000|";
        var result = FixParsingService.NormaliseDelimiters(input);

        // The embedded pipe should survive as a literal pipe
        Assert.Contains("SomeCompany|InternalCompany", result);
        // But real delimiters should be SOH
        Assert.StartsWith($"8=FIX.4.4{SOH}", result);
        Assert.Contains($"{SOH}447=D{SOH}", result);
    }

    [Fact]
    public void NormaliseDelimiters_PreservesSohWhenAlreadyPresent()
    {
        var input = $"8=FIX.4.4{SOH}9=5{SOH}35=0{SOH}10=000{SOH}";
        var result = FixParsingService.NormaliseDelimiters(input);

        Assert.Equal(input, result);
    }

    [Fact]
    public void NormaliseDelimiters_AppendsTrailingSohIfMissing()
    {
        var input = $"8=FIX.4.4{SOH}9=5{SOH}35=0{SOH}10=000";
        var result = FixParsingService.NormaliseDelimiters(input);

        Assert.EndsWith($"{SOH}", result);
    }

    // ── Structural validation ──

    [Fact]
    public void ValidateStructure_ReturnsNoErrors_ForValidMessage()
    {
        var fix = $"8=FIX.4.4{SOH}9=5{SOH}35=0{SOH}10=000{SOH}";
        var errors = FixParsingService.ValidateStructure(fix);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateStructure_ReportsAllMissingTags()
    {
        var fix = $"55=AAPL{SOH}";
        var errors = FixParsingService.ValidateStructure(fix);

        Assert.Equal(4, errors.Count); // 8=, 9=, 35=, 10=
    }

    [Fact]
    public void ValidateStructure_ReportsMissingBeginString_WhenNotFirstField()
    {
        // Tag 8 is present but not first
        var fix = $"9=5{SOH}8=FIX.4.4{SOH}35=0{SOH}10=000{SOH}";
        var errors = FixParsingService.ValidateStructure(fix);

        Assert.Contains(errors, e => e.Contains("BeginString"));
    }

    // ── Full parse to JSON ──

    [Fact]
    public void ParseToJson_ReturnsValidJson_ForHeartbeat()
    {
        // Minimal FIX 4.4 Heartbeat (MsgType 0)
        var body = $"35=0{SOH}49=SENDER{SOH}56=TARGET{SOH}34=1{SOH}52=20240101-00:00:00{SOH}";
        var bodyLength = body.Length;
        var raw = $"8=FIX.4.4{SOH}9={bodyLength}{SOH}{body}";

        // Compute checksum
        var sum = 0;
        foreach (var c in raw) sum += c;
        var checksum = (sum % 256).ToString("D3");
        raw += $"10={checksum}{SOH}";

        var json = _service.ParseToJson(raw, false);

        Assert.Contains("\"Header\"", json);
        Assert.Contains("\"Body\"", json);
        Assert.Contains("\"Trailer\"", json);
        Assert.Contains("FIX.4.4", json);

        // With the data dictionary loaded, tags should resolve to field names
        Assert.Contains("\"BeginString\"", json);
        Assert.Contains("\"BodyLength\"", json);
        Assert.Contains("\"MsgType\"", json);
        Assert.Contains("\"SenderCompID\"", json);
        Assert.Contains("\"TargetCompID\"", json);
        // MsgType "0" should resolve to "HEARTBEAT"
        Assert.Contains("HEARTBEAT", json);
    }

    [Fact]
    public void ParseToJson_HandlesRealWorldIceMessage_WithSohDelimiters()
    {
        // The real-world ICE message contains pipe characters INSIDE field values
        // (e.g. "Glencore|Glencore"), so we test with actual SOH delimiters
        // to avoid the inherent ambiguity of pipe-delimited FIX.
        var msg =
            $"8=FIX.4.4{SOH}9=744{SOH}35=AE{SOH}49=ICE{SOH}34=22436{SOH}" +
            $"52=20230103-10:27:34.563789{SOH}56=205{SOH}57=1{SOH}" +
            $"571=148146{SOH}487=0{SOH}856=0{SOH}828=0{SOH}150=F{SOH}" +
            $"17=9591500{SOH}39=2{SOH}570=N{SOH}55=5444726{SOH}" +
            $"48=G FMG0023!{SOH}22=8{SOH}461=FXXXXX{SOH}207=IFEU{SOH}" +
            $"9064=0{SOH}916=20230201{SOH}917=20230228{SOH}32=1{SOH}" +
            $"31=911.00{SOH}9018=1{SOH}9022=1{SOH}75=20230103{SOH}" +
            $"60=20230103-10:27:34.563362{SOH}9413=1{SOH}9028=362004{SOH}" +
            $"9707=4{SOH}9700=1{SOH}9701=2{SOH}9702=0{SOH}9703=0{SOH}" +
            $"9705=3{SOH}9706=4{SOH}552=1{SOH}54=1{SOH}37=9591497{SOH}" +
            $"11=112703716108{SOH}453=12{SOH}" +
            $"448=jbtt-fx{SOH}447=D{SOH}452=11{SOH}" +
            $"448=Glencore Commodities Ltd.{SOH}447=D{SOH}452=13{SOH}" +
            $"448=207{SOH}447=D{SOH}452=56{SOH}" +
            $"448=2172{SOH}447=D{SOH}452=4{SOH}" +
            $"448=GCMZ1JWB{SOH}447=D{SOH}452=51{SOH}" +
            $"448=GCMZ1JWB{SOH}447=D{SOH}452=55{SOH}" +
            $"448=Mizuho Securities{SOH}447=D{SOH}452=60{SOH}" +
            $"448=MZF{SOH}447=D{SOH}452=63{SOH}" +
            $"448=W{SOH}447=D{SOH}452=54{SOH}" +
            $"448=Glencore|Glencore{SOH}447=D{SOH}452=57{SOH}" +
            $"448=ISV-TT|Glencore{SOH}447=D{SOH}452=59{SOH}" +
            $"448=jbtt-fx|JBotchin{SOH}447=D{SOH}452=58{SOH}" +
            $"77=O{SOH}9121=90134427{SOH}10=215{SOH}";

        var json = _service.ParseToJson(msg, false);

        Assert.Contains("\"Header\"", json);
        Assert.Contains("\"Body\"", json);
        Assert.Contains("ICE", json);
        // With data dictionary, MsgType "AE" should resolve to "TRADE_CAPTURE_REPORT"
        Assert.Contains("TRADE_CAPTURE_REPORT", json);
        // Known FIX 4.4 field names should appear
        Assert.Contains("\"SenderCompID\"", json);
        Assert.Contains("\"Symbol\"", json);
        Assert.Contains("\"SecurityExchange\"", json);
    }

    [Fact]
    public void ParseToJson_ThrowsOnGarbage()
    {
        Assert.ThrowsAny<Exception>(() => _service.ParseToJson("this is not a fix message", true));
    }
}
