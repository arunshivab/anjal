using System.Net;

namespace Anjal.Auth.Tests;

/// <summary>
/// v1.0.0-rc.9.1 (DEF-081): SPF counts DNS lookups by RFC 7208 4.6.4 - one per
/// include, a, mx, ptr, exists or redirect term, and not for fetching the
/// domain's own record. Before the fix every include counted twice, so a
/// 5-include record (the structure Microsoft 365 publishes) reached 11 and was a
/// PermError, though it evaluates to Pass.
/// </summary>
public sealed class Rc91SpfLookupTests
{
    private static async Task<SpfDetail> CheckAsync(Dictionary<string, string> zone, string ip, string domain)
    {
        using var dns = new FakeDnsServer(zone);
        return await new SpfVerifier(new Anjal.Dns.DnsResolver(dns.EndPoint)).CheckAsync(IPAddress.Parse(ip), domain);
    }

    [Fact]
    public async Task MicrosoftsStructure_FiveIncludes_Passes_WithFiveLookups()
    {
        // Mirrors accountprotection.microsoft.com on 29 Sep 2026: five includes, the
        // first ones ending in ~all, the sender listed only in the fifth.
        var zone = new Dictionary<string, string>
        {
            ["accountprotection.ms.test"] = "v=spf1 include:spf-a.int.test include:spf-a.hm.test include:spf-c.hm.test include:spf-d.hm.test include:spf.po.test -all",
            ["spf-a.int.test"] = "v=spf1 ip4:64.4.4.0/24 ip4:65.52.110.207  ip4:157.55.103.193 ~all",
            ["spf-a.hm.test"] = "v=spf1 ip4:157.55.0.192/26 ip4:65.54.190.0/24 ~all",
            ["spf-c.hm.test"] = "v=spf1 ~all",
            ["spf-d.hm.test"] = "v=spf1 ~all",
            ["spf.po.test"] = "v=spf1 ip4:40.92.0.0/15 ip4:40.107.0.0/16 ip4:52.100.0.0/15 -all",
        };
        SpfDetail d = await CheckAsync(zone, "40.107.201.108", "accountprotection.ms.test");
        Assert.Equal(SpfResult.Pass, d.Result);
        Assert.Equal(5, d.LookupCount);
    }

    private static Dictionary<string, string> IncludeChain(int includes)
    {
        var zone = new Dictionary<string, string>();
        var root = new System.Text.StringBuilder("v=spf1");
        for (int i = 1; i <= includes; i++)
        {
            root.Append(" include:i").Append(i).Append(".test");
            zone[$"i{i}.test"] = i == includes ? "v=spf1 ip4:192.0.2.0/24 -all" : "v=spf1 ~all";
        }
        zone["limit.test"] = root.Append(" -all").ToString();
        return zone;
    }

    [Fact]
    public async Task TenLookupTerms_AreWithinTheLimit()
    {
        SpfDetail d = await CheckAsync(IncludeChain(10), "192.0.2.7", "limit.test");
        Assert.Equal(SpfResult.Pass, d.Result);
        Assert.Equal(10, d.LookupCount);
    }

    [Fact]
    public async Task ElevenLookupTerms_AreAPermError_ThatSaysWhy()
    {
        SpfDetail d = await CheckAsync(IncludeChain(11), "192.0.2.7", "limit.test");
        Assert.Equal(SpfResult.PermError, d.Result);
        Assert.Contains("lookup limit", d.Explanation, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Redirect_CountsOnce_AndNestedIncludes_CountOncePerTerm()
    {
        var zone = new Dictionary<string, string>
        {
            ["r.test"] = "v=spf1 redirect=target.test",
            ["target.test"] = "v=spf1 include:outer.test -all",
            ["outer.test"] = "v=spf1 include:inner.test -all",
            ["inner.test"] = "v=spf1 ip4:198.51.100.0/24 -all",
        };
        SpfDetail d = await CheckAsync(zone, "198.51.100.20", "r.test");
        Assert.Equal(SpfResult.Pass, d.Result);
        Assert.Equal(3, d.LookupCount);   // redirect, include:outer, include:inner
    }

    [Fact]
    public void TheHeader_SaysWhyAPermErrorHappened_AndStaysWellFormed()
    {
        var spf = new SpfDetail { Result = SpfResult.PermError, Domain = "limit.test", Explanation = "SPF PermError: DNS lookup limit (10) exceeded." };
        string header = AuthenticationResultsBuilder.Build("mail.anjal.test", spf, new DkimDetail { Result = DkimResult.None }, new DmarcDetail { Result = DmarcResult.None });
        Assert.Contains("spf=permerror (SPF PermError: DNS lookup limit 10 exceeded.) smtp.mailfrom=limit.test", header, System.StringComparison.Ordinal);
        Assert.Equal(header.Count(c => c == '('), header.Count(c => c == ')'));
    }
}
