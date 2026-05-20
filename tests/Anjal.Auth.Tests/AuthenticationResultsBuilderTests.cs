namespace Anjal.Auth.Tests;

public class AuthenticationResultsBuilderTests
{
    [Fact]
    public void Build_IncludesServingHost()
    {
        string header = AuthenticationResultsBuilder.Build("mx.anjal.test", Spf(SpfResult.Pass), Dkim(DkimResult.Pass), Dmarc(DmarcResult.Pass, DmarcPolicy.Reject));
        Assert.StartsWith("mx.anjal.test;", header, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Build_SpfPass_HasPassToken()
    {
        string header = AuthenticationResultsBuilder.Build("mx", Spf(SpfResult.Pass, domain: "ex.test"), Dkim(DkimResult.None), Dmarc(DmarcResult.None));
        Assert.Contains("spf=pass", header, System.StringComparison.Ordinal);
        Assert.Contains("smtp.mailfrom=ex.test", header, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Build_SpfFail_HasFailToken()
    {
        string header = AuthenticationResultsBuilder.Build("mx", Spf(SpfResult.Fail), Dkim(DkimResult.None), Dmarc(DmarcResult.None));
        Assert.Contains("spf=fail", header, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DkimPass_HasHeaderDomainAndSelector()
    {
        string header = AuthenticationResultsBuilder.Build("mx", Spf(SpfResult.None),
            Dkim(DkimResult.Pass, domain: "ex.test", selector: "s1"),
            Dmarc(DmarcResult.None));
        Assert.Contains("dkim=pass", header, System.StringComparison.Ordinal);
        Assert.Contains("header.d=ex.test", header, System.StringComparison.Ordinal);
        Assert.Contains("header.s=s1", header, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DkimNone_NoDomainOrSelector()
    {
        string header = AuthenticationResultsBuilder.Build("mx", Spf(SpfResult.None), Dkim(DkimResult.None), Dmarc(DmarcResult.None));
        Assert.Contains("dkim=none", header, System.StringComparison.Ordinal);
        Assert.DoesNotContain("header.d=", header, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DmarcPass_HasPolicy()
    {
        string header = AuthenticationResultsBuilder.Build("mx", Spf(SpfResult.Pass), Dkim(DkimResult.Pass),
            Dmarc(DmarcResult.Pass, DmarcPolicy.Reject, fromDomain: "ex.test"));
        Assert.Contains("dmarc=pass", header, System.StringComparison.Ordinal);
        Assert.Contains("p=reject", header, System.StringComparison.Ordinal);
        Assert.Contains("header.from=ex.test", header, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AllErrors_MapToCorrectTokens()
    {
        string header = AuthenticationResultsBuilder.Build("mx",
            Spf(SpfResult.PermError), Dkim(DkimResult.TempError), Dmarc(DmarcResult.TempError));
        Assert.Contains("spf=permerror", header, System.StringComparison.Ordinal);
        Assert.Contains("dkim=temperror", header, System.StringComparison.Ordinal);
        Assert.Contains("dmarc=temperror", header, System.StringComparison.Ordinal);
    }

    private static SpfDetail Spf(SpfResult r, string domain = "") => new()
    {
        Result = r,
        Domain = domain,
        Explanation = $"spf {r}",
    };

    private static DkimDetail Dkim(DkimResult r, string domain = "", string selector = "") => new()
    {
        Result = r,
        Domain = domain,
        Selector = selector,
        Algorithm = "rsa-sha256",
        Explanation = $"dkim {r}",
    };

    private static DmarcDetail Dmarc(DmarcResult r, DmarcPolicy p = DmarcPolicy.None, string fromDomain = "") => new()
    {
        Result = r,
        Policy = p,
        FromDomain = fromDomain,
        Explanation = $"dmarc {r}",
    };
}
