namespace Anjal.Routing.Tests;

public class StoreBackedRoutingTableTests
{
    private static Anjal.Store.InMemoryMessageStore NewStoreWithRule(string localPart)
    {
        var store = new Anjal.Store.InMemoryMessageStore();
        _ = store.UpsertRoutingRuleAsync(new Anjal.Store.RoutingRule
        {
            LocalPart = localPart,
            WebhookUrl = "http://example/wh",
            WebhookSecret = "0011",
        }).Result;
        return store;
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_NoMatchingRule_ReturnsNoSuchMailbox()
    {
        var store = new Anjal.Store.InMemoryMessageStore();
        var table = new StoreBackedRoutingTable(store);

        RoutingDecision decision = await table.ResolveAsync("unknown@host");
        Assert.Equal(RoutingOutcome.NoSuchMailbox, decision.Outcome);
        Assert.Null(decision.Rule);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_BareAddressWithRule_Accepted()
    {
        var store = NewStoreWithRule("reports");
        var table = new StoreBackedRoutingTable(store);

        RoutingDecision decision = await table.ResolveAsync("reports@host");
        Assert.Equal(RoutingOutcome.Accepted, decision.Outcome);
        Assert.NotNull(decision.Rule);
        Assert.Null(decision.Grant);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_TaggedAddressWithoutGrant_TagNotAuthorised()
    {
        var store = NewStoreWithRule("reports");
        var table = new StoreBackedRoutingTable(store);

        RoutingDecision decision = await table.ResolveAsync("reports+CASE-1@host");
        Assert.Equal(RoutingOutcome.TagNotAuthorised, decision.Outcome);
        Assert.NotNull(decision.Rule);
        Assert.Null(decision.Grant);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_TaggedAddressWithActiveGrant_Accepted()
    {
        var store = NewStoreWithRule("reports");
        await store.CreateTagGrantAsync(new Anjal.Store.TagGrant
        {
            LocalPart = "reports",
            Tag = "CASE-1",
            CorrelationKey = "case:1",
            ExpiresAt = System.DateTimeOffset.UtcNow.AddDays(1),
        });
        var table = new StoreBackedRoutingTable(store);

        RoutingDecision decision = await table.ResolveAsync("reports+CASE-1@host");
        Assert.Equal(RoutingOutcome.Accepted, decision.Outcome);
        Assert.NotNull(decision.Grant);
        Assert.Equal("case:1", decision.Grant.CorrelationKey);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_TaggedAddressWithExpiredGrant_TagNotAuthorised()
    {
        var store = NewStoreWithRule("reports");
        var fixedNow = new System.DateTimeOffset(2026, 6, 1, 0, 0, 0, System.TimeSpan.Zero);
        await store.CreateTagGrantAsync(new Anjal.Store.TagGrant
        {
            LocalPart = "reports",
            Tag = "CASE-1",
            ExpiresAt = fixedNow.AddHours(-1),
        });

        var table = new StoreBackedRoutingTable(store, () => fixedNow);
        RoutingDecision decision = await table.ResolveAsync("reports+CASE-1@host");

        Assert.Equal(RoutingOutcome.TagNotAuthorised, decision.Outcome);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_MalformedRecipient_ReturnsNoSuchMailbox()
    {
        var store = NewStoreWithRule("reports");
        var table = new StoreBackedRoutingTable(store);

        RoutingDecision decision = await table.ResolveAsync("not-an-address");
        Assert.Equal(RoutingOutcome.NoSuchMailbox, decision.Outcome);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_CaseInsensitiveLocalPart()
    {
        var store = NewStoreWithRule("Reports");
        var table = new StoreBackedRoutingTable(store);

        RoutingDecision decision = await table.ResolveAsync("REPORTS@host");
        Assert.Equal(RoutingOutcome.Accepted, decision.Outcome);
    }
}
