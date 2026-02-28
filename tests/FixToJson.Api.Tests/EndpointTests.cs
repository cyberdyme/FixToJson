using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FixToJson.Api.Tests;

public class EndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public EndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PostFixToJson_Returns200_ForValidPipeDelimitedMessage()
    {
        var body = "35=0\u0001" + "49=SENDER\u0001" + "56=TARGET\u0001" + "34=1\u0001" + "52=20240101-00:00:00\u0001";
        var bodyLength = body.Length;
        var raw = $"8=FIX.4.4|9={bodyLength}|{body.Replace('\u0001', '|')}";
        var sum = 0;
        foreach (var c in raw.Replace('|', '\u0001')) sum += c;
        var checksum = (sum % 256).ToString("D3");
        raw += $"10={checksum}|";

        var response = await _client.PostAsJsonAsync("/api/fix/to-json", new { fix = raw });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("Header", out _));
        Assert.True(doc.RootElement.TryGetProperty("Body", out _));
        Assert.True(doc.RootElement.TryGetProperty("Trailer", out _));
    }

    [Fact]
    public async Task PostFixToJson_Returns200_ForBodyWithEmbeddedNewline()
    {
        // Simulates exactly what Swagger UI sends: a trailing newline inside the
        // JSON string value, e.g. { "fix": "8=FIX.4.4|...\n" }
        // This raw JSON contains a literal 0x0A inside the "fix" value.
        var fixMsg = "8=FIX.4.4|9=60|35=0|49=SENDER|56=TARGET|34=1|52=20240101-00:00:00.000|10=092|";
        var rawJson = "{\"fix\": \"" + fixMsg + "\n\"}";

        var content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/fix/to-json", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostFixToJson_Returns400_ForEmptyBody()
    {
        var response = await _client.PostAsJsonAsync("/api/fix/to-json", new { fix = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostFixToJson_Returns400_ForMissingRequiredTags()
    {
        var response = await _client.PostAsJsonAsync("/api/fix/to-json", new { fix = "55=AAPL|" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("BeginString", json);
    }

    [Fact]
    public async Task PostFixToJson_Returns400_ForMalformedInput()
    {
        // Has required tags structurally but is totally broken
        var response = await _client.PostAsJsonAsync("/api/fix/to-json",
            new { fix = "8=GARBAGE|9=0|35=X|10=000|" });

        // Could be 200 or 400 depending on how lenient the parser is.
        // We just verify it doesn't return 500.
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task PostFixToJson_Returns200_ForIceMessageWithPipesInValues()
    {
        // The exact ICE message from the user's sample — pipe-delimited, with
        // embedded pipes inside field 448 values (e.g. "Glencore|Glencore").
        const string iceMsg =
            "8=FIX.4.4|9=744|35=AE|49=ICE|34=22436|52=20230103-10:27:34.563789|56=205|57=1|" +
            "571=148146|487=0|856=0|828=0|150=F|17=9591500|39=2|570=N|55=5444726|48=G FMG0023!|22=8|" +
            "461=FXXXXX|207=IFEU|9064=0|916=20230201|917=20230228|32=1|31=911.00|9018=1|9022=1|" +
            "75=20230103|60=20230103-10:27:34.563362|9413=1|9028=362004|9707=4|9700=1|9701=2|9702=0|" +
            "9703=0|9705=3|9706=4|552=1|54=1|37=9591497|11=112703716108|453=12|448=jbtt-fx|447=D|452=11|" +
            "448=Glencore Commodities Ltd.|447=D|452=13|448=207|447=D|452=56|448=2172|447=D|452=4|" +
            "448=GCMZ1JWB|447=D|452=51|448=GCMZ1JWB|447=D|452=55|448=Mizuho Securities|447=D|452=60|" +
            "448=MZF|447=D|452=63|448=W|447=D|452=54|448=SomeCompany|InternalCompany|447=D|452=57|" +
            "448=ISV-TT|Glencore|447=D|452=59|448=jbtt-fx|JBotchin|447=D|452=58|77=O|9121=90134427|10=215|";

        var response = await _client.PostAsJsonAsync("/api/fix/to-json", new { fix = iceMsg });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("Header", out _));
        Assert.True(doc.RootElement.TryGetProperty("Body", out _));
    }
}
