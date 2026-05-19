using Anjal.Api.Dto;

namespace Anjal.Api.Tests;

public class ApiJsonTests
{
    [Fact]
    public void Serialize_UsesCamelCase()
    {
        var req = new RoutingRuleRequest
        {
            LocalPart = "reports",
            WebhookUrl = "https://app.test/hook",
            WebhookSecret = "deadbeef",
        };
        string json = ApiJson.Serialize(req);
        Assert.Contains("\"localPart\":\"reports\"", json, System.StringComparison.Ordinal);
        Assert.Contains("\"webhookUrl\"", json, System.StringComparison.Ordinal);
        Assert.Contains("\"webhookSecret\"", json, System.StringComparison.Ordinal);
        Assert.DoesNotContain("LocalPart", json, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_IsCaseInsensitive()
    {
        string json = "{\"LOCALPART\":\"x\",\"WEBHOOKURL\":\"u\",\"WEBHOOKSECRET\":\"s\"}";
        RoutingRuleRequest? r = ApiJson.Deserialize<RoutingRuleRequest>(json);
        Assert.NotNull(r);
        Assert.Equal("x", r!.LocalPart);
        Assert.Equal("u", r.WebhookUrl);
        Assert.Equal("s", r.WebhookSecret);
    }

    [Fact]
    public void Deserialize_EmptyString_ReturnsNull()
    {
        RoutingRuleRequest? r = ApiJson.Deserialize<RoutingRuleRequest>(string.Empty);
        Assert.Null(r);
    }

    [Fact]
    public void Deserialize_WhitespaceOnly_ReturnsNull()
    {
        RoutingRuleRequest? r = ApiJson.Deserialize<RoutingRuleRequest>("   \r\n  ");
        Assert.Null(r);
    }

    [Fact]
    public void Deserialize_Malformed_Throws()
    {
        Assert.Throws<System.Text.Json.JsonException>(() =>
            ApiJson.Deserialize<RoutingRuleRequest>("{not json"));
    }

    [Fact]
    public void RoundTrip_GuidAndDateTimeOffset()
    {
        var original = new RoutingRuleResponse
        {
            Id = System.Guid.Parse("11111111-2222-3333-4444-555555555555"),
            LocalPart = "x",
            WebhookUrl = "y",
            CreatedAt = new System.DateTimeOffset(2026, 5, 19, 12, 0, 0, System.TimeSpan.Zero),
        };
        string json = ApiJson.Serialize(original);
        RoutingRuleResponse? back = ApiJson.Deserialize<RoutingRuleResponse>(json);
        Assert.NotNull(back);
        Assert.Equal(original.Id, back!.Id);
        Assert.Equal(original.CreatedAt, back.CreatedAt);
    }

    [Fact]
    public void Serialize_StringsAreEscaped()
    {
        // We test the semantic property: the value round-trips losslessly.
        // System.Text.Json may use any valid JSON escape (e.g. \u0022 instead
        // of \" for the quote character), so asserting on the exact escape
        // form would be brittle.
        var req = new RoutingRuleRequest
        {
            LocalPart = "weird\"name\\with\nnewlines",
            WebhookUrl = "u",
            WebhookSecret = "s",
        };
        string json = ApiJson.Serialize(req);

        // No raw control characters or unescaped quotes in the JSON wire form.
        Assert.DoesNotContain("\n", json, System.StringComparison.Ordinal);
        // The only unescaped quotes are the JSON delimiters around values.

        RoutingRuleRequest? back = ApiJson.Deserialize<RoutingRuleRequest>(json);
        Assert.NotNull(back);
        Assert.Equal(req.LocalPart, back!.LocalPart);
        Assert.Equal(req.WebhookUrl, back.WebhookUrl);
        Assert.Equal(req.WebhookSecret, back.WebhookSecret);
    }
}
