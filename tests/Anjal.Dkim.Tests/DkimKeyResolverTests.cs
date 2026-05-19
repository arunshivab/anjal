using Anjal.Dkim;

namespace Anjal.Dkim.Tests;

public class DkimKeyResolverTests
{
    private static DkimKey MakeKey(string domain) => new()
    {
        Domain = domain,
        Selector = "default",
        PrivateKeyPem = "fake-pem",
    };

    [Fact]
    public async System.Threading.Tasks.Task SingleKeyResolver_MatchesItsDomain()
    {
        var r = new SingleKeyResolver(MakeKey("a.test"));
        DkimKey? hit = await r.ResolveAsync("a.test");
        Assert.NotNull(hit);
        Assert.Equal("a.test", hit!.Domain);
    }

    [Fact]
    public async System.Threading.Tasks.Task SingleKeyResolver_CaseInsensitive()
    {
        var r = new SingleKeyResolver(MakeKey("a.test"));
        DkimKey? hit = await r.ResolveAsync("A.TEST");
        Assert.NotNull(hit);
    }

    [Fact]
    public async System.Threading.Tasks.Task SingleKeyResolver_ReturnsNullForOther()
    {
        var r = new SingleKeyResolver(MakeKey("a.test"));
        DkimKey? hit = await r.ResolveAsync("b.test");
        Assert.Null(hit);
    }

    [Fact]
    public async System.Threading.Tasks.Task ChainedResolver_ReturnsFirstMatch()
    {
        var first = new SingleKeyResolver(MakeKey("a.test"));
        var second = new SingleKeyResolver(MakeKey("b.test"));
        var chain = new ChainedKeyResolver(first, second);

        Assert.NotNull(await chain.ResolveAsync("a.test"));
        Assert.NotNull(await chain.ResolveAsync("b.test"));
        Assert.Null(await chain.ResolveAsync("c.test"));
    }

    [Fact]
    public async System.Threading.Tasks.Task ChainedResolver_EarlierResolverWins()
    {
        var first = new SingleKeyResolver(new DkimKey { Domain = "a.test", Selector = "first", PrivateKeyPem = "x" });
        var second = new SingleKeyResolver(new DkimKey { Domain = "a.test", Selector = "second", PrivateKeyPem = "y" });
        var chain = new ChainedKeyResolver(first, second);

        DkimKey? hit = await chain.ResolveAsync("a.test");
        Assert.NotNull(hit);
        Assert.Equal("first", hit!.Selector);
    }
}
