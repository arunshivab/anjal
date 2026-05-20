namespace Anjal.Auth.Tests;

public class AuthResultsJsonTests
{
    [Fact]
    public void Serialize_AllPass_ProducesValidJson()
    {
        var results = new AuthenticationResults
        {
            ServingHost = "mx.anjal.test",
            Spf = new SpfDetail
            {
                Result = SpfResult.Pass,
                Domain = "ex.test",
                PeerAddress = "192.0.2.5",
                MatchedMechanism = "ip4:192.0.2.0/24",
                LookupCount = 2,
                Explanation = "matched",
            },
            Dkim = new DkimDetail
            {
                Result = DkimResult.Pass,
                Domain = "ex.test",
                Selector = "default",
                Algorithm = "rsa-sha256",
                Explanation = "verified",
            },
            Dmarc = new DmarcDetail
            {
                Result = DmarcResult.Pass,
                FromDomain = "ex.test",
                Policy = DmarcPolicy.Reject,
                SpfAligned = true,
                DkimAligned = true,
                AlignedDomain = "ex.test",
                Explanation = "aligned",
            },
            HeaderValue = "mx.anjal.test; spf=pass; dkim=pass; dmarc=pass",
        };

        string json = AuthResultsJson.Serialize(results);

        Assert.StartsWith("{", json, System.StringComparison.Ordinal);
        Assert.EndsWith("}", json, System.StringComparison.Ordinal);
        Assert.Contains("\"spf\":{", json, System.StringComparison.Ordinal);
        Assert.Contains("\"dkim\":{", json, System.StringComparison.Ordinal);
        Assert.Contains("\"dmarc\":{", json, System.StringComparison.Ordinal);
        Assert.Contains("\"result\":\"pass\"", json, System.StringComparison.Ordinal);
        Assert.Contains("\"domain\":\"ex.test\"", json, System.StringComparison.Ordinal);
        Assert.Contains("\"selector\":\"default\"", json, System.StringComparison.Ordinal);
        Assert.Contains("\"lookupCount\":2", json, System.StringComparison.Ordinal);
        Assert.Contains("\"spfAligned\":true", json, System.StringComparison.Ordinal);
        Assert.Contains("\"policy\":\"reject\"", json, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_EscapesQuotesInExplanation()
    {
        var results = new AuthenticationResults
        {
            Spf = new SpfDetail
            {
                Result = SpfResult.Pass,
                Explanation = "explanation with \"quotes\"",
            },
            Dkim = new DkimDetail { Result = DkimResult.None },
            Dmarc = new DmarcDetail { Result = DmarcResult.None },
            HeaderValue = string.Empty,
        };

        string json = AuthResultsJson.Serialize(results);
        Assert.Contains(@"\""quotes\""", json, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_EmptyDefaults_ProducesEmptyStrings()
    {
        var results = new AuthenticationResults();
        string json = AuthResultsJson.Serialize(results);

        Assert.Contains("\"result\":\"none\"", json, System.StringComparison.Ordinal);
        Assert.Contains("\"spfAligned\":false", json, System.StringComparison.Ordinal);
    }
}
