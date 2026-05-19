namespace Anjal.Store.Tests;

public class OutboundTlsPolicyTests
{
    [Fact]
    public async System.Threading.Tasks.Task Upsert_NewPolicy_AssignsIdAndUpdatedAt()
    {
        var store = new InMemoryMessageStore();
        OutboundTlsPolicy saved = await store.UpsertOutboundTlsPolicyAsync(new OutboundTlsPolicy
        {
            Domain = "gmail.com",
            Mode = TlsMode.Required,
        });

        Assert.NotEqual(System.Guid.Empty, saved.Id);
        Assert.Equal("gmail.com", saved.Domain);
        Assert.Equal(TlsMode.Required, saved.Mode);
        Assert.NotEqual(default, saved.UpdatedAt);
    }

    [Fact]
    public async System.Threading.Tasks.Task Upsert_DomainIsLowercased()
    {
        var store = new InMemoryMessageStore();
        OutboundTlsPolicy saved = await store.UpsertOutboundTlsPolicyAsync(new OutboundTlsPolicy
        {
            Domain = "Gmail.COM",
            Mode = TlsMode.Required,
        });

        Assert.Equal("gmail.com", saved.Domain);
    }

    [Fact]
    public async System.Threading.Tasks.Task Upsert_ExistingDomain_UpdatesMode()
    {
        var store = new InMemoryMessageStore();
        OutboundTlsPolicy first = await store.UpsertOutboundTlsPolicyAsync(new OutboundTlsPolicy
        {
            Domain = "gmail.com",
            Mode = TlsMode.Opportunistic,
        });

        OutboundTlsPolicy second = await store.UpsertOutboundTlsPolicyAsync(new OutboundTlsPolicy
        {
            Domain = "gmail.com",
            Mode = TlsMode.Required,
        });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(TlsMode.Required, second.Mode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Get_CaseInsensitiveLookup()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertOutboundTlsPolicyAsync(new OutboundTlsPolicy
        {
            Domain = "gmail.com",
            Mode = TlsMode.Required,
        });

        OutboundTlsPolicy? hit1 = await store.GetOutboundTlsPolicyAsync("gmail.com");
        OutboundTlsPolicy? hit2 = await store.GetOutboundTlsPolicyAsync("GMAIL.COM");
        OutboundTlsPolicy? hit3 = await store.GetOutboundTlsPolicyAsync("Gmail.Com");

        Assert.NotNull(hit1);
        Assert.NotNull(hit2);
        Assert.NotNull(hit3);
        Assert.Equal(hit1!.Id, hit2!.Id);
        Assert.Equal(hit1.Id, hit3!.Id);
    }

    [Fact]
    public async System.Threading.Tasks.Task Get_UnknownDomain_ReturnsNull()
    {
        var store = new InMemoryMessageStore();
        OutboundTlsPolicy? hit = await store.GetOutboundTlsPolicyAsync("nope.example");
        Assert.Null(hit);
    }

    [Fact]
    public async System.Threading.Tasks.Task List_ReturnsAllPolicies()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertOutboundTlsPolicyAsync(new OutboundTlsPolicy { Domain = "a.test", Mode = TlsMode.Opportunistic });
        await store.UpsertOutboundTlsPolicyAsync(new OutboundTlsPolicy { Domain = "b.test", Mode = TlsMode.Required });
        await store.UpsertOutboundTlsPolicyAsync(new OutboundTlsPolicy { Domain = "c.test", Mode = TlsMode.Disabled });

        System.Collections.Generic.IReadOnlyList<OutboundTlsPolicy> all = await store.ListOutboundTlsPoliciesAsync();
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_ExistingPolicy_ReturnsTrue()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertOutboundTlsPolicyAsync(new OutboundTlsPolicy { Domain = "gone.test", Mode = TlsMode.Required });

        bool first = await store.DeleteOutboundTlsPolicyAsync("gone.test");
        bool second = await store.DeleteOutboundTlsPolicyAsync("gone.test");

        Assert.True(first);
        Assert.False(second);

        OutboundTlsPolicy? lookup = await store.GetOutboundTlsPolicyAsync("gone.test");
        Assert.Null(lookup);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delete_CaseInsensitive()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertOutboundTlsPolicyAsync(new OutboundTlsPolicy { Domain = "mixed.test", Mode = TlsMode.Required });
        bool removed = await store.DeleteOutboundTlsPolicyAsync("MIXED.TEST");
        Assert.True(removed);
    }
}
