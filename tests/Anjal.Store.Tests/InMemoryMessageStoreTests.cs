namespace Anjal.Store.Tests;

public class InMemoryMessageStoreTests
{
    [Fact]
    public async System.Threading.Tasks.Task UpsertRoutingRule_NewLocalPart_AssignsId()
    {
        var store = new InMemoryMessageStore();
        RoutingRule saved = await store.UpsertRoutingRuleAsync(new RoutingRule
        {
            LocalPart = "reports",
            WebhookUrl = "http://example/wh",
            WebhookSecret = "deadbeef",
        });
        Assert.NotEqual(System.Guid.Empty, saved.Id);
        Assert.Equal("reports", saved.LocalPart);
    }

    [Fact]
    public async System.Threading.Tasks.Task UpsertRoutingRule_ExistingLocalPart_KeepsId()
    {
        var store = new InMemoryMessageStore();
        RoutingRule first = await store.UpsertRoutingRuleAsync(new RoutingRule
        {
            LocalPart = "reports",
            WebhookUrl = "http://a",
            WebhookSecret = "00",
        });
        RoutingRule second = await store.UpsertRoutingRuleAsync(new RoutingRule
        {
            LocalPart = "reports",
            WebhookUrl = "http://b",
            WebhookSecret = "11",
        });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("http://b", second.WebhookUrl);
        Assert.Single(store.Rules);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetRoutingRule_CaseInsensitive()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertRoutingRuleAsync(new RoutingRule
        {
            LocalPart = "reports",
            WebhookUrl = "http://example",
            WebhookSecret = "00",
        });

        RoutingRule? lower = await store.GetRoutingRuleAsync("reports");
        RoutingRule? upper = await store.GetRoutingRuleAsync("REPORTS");
        RoutingRule? mixed = await store.GetRoutingRuleAsync("Reports");

        Assert.NotNull(lower);
        Assert.NotNull(upper);
        Assert.NotNull(mixed);
        Assert.Equal(lower.Id, upper.Id);
        Assert.Equal(lower.Id, mixed.Id);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetRoutingRule_Missing_ReturnsNull()
    {
        var store = new InMemoryMessageStore();
        Assert.Null(await store.GetRoutingRuleAsync("nope"));
    }

    [Fact]
    public async System.Threading.Tasks.Task DeleteRoutingRule_RemovesByLocalPart()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertRoutingRuleAsync(new RoutingRule
        {
            LocalPart = "reports",
            WebhookUrl = "http://a",
            WebhookSecret = "00",
        });

        bool removed = await store.DeleteRoutingRuleAsync("reports");
        Assert.True(removed);
        Assert.Empty(store.Rules);

        bool secondRemove = await store.DeleteRoutingRuleAsync("reports");
        Assert.False(secondRemove);
    }

    [Fact]
    public async System.Threading.Tasks.Task TagGrant_GetActive_ReturnsOnlyUnexpired()
    {
        var store = new InMemoryMessageStore();
        System.DateTimeOffset now = System.DateTimeOffset.UtcNow;

        await store.CreateTagGrantAsync(new TagGrant
        {
            LocalPart = "reports",
            Tag = "expired",
            CorrelationKey = "case:1",
            ExpiresAt = now.AddMinutes(-5),
        });
        await store.CreateTagGrantAsync(new TagGrant
        {
            LocalPart = "reports",
            Tag = "active",
            CorrelationKey = "case:2",
            ExpiresAt = now.AddMinutes(60),
        });

        TagGrant? expiredLookup = await store.GetActiveTagGrantAsync("reports", "expired", now);
        TagGrant? activeLookup = await store.GetActiveTagGrantAsync("reports", "active", now);

        Assert.Null(expiredLookup);
        Assert.NotNull(activeLookup);
        Assert.Equal("case:2", activeLookup.CorrelationKey);
    }

    [Fact]
    public async System.Threading.Tasks.Task TagGrant_CaseInsensitiveLookup()
    {
        var store = new InMemoryMessageStore();
        System.DateTimeOffset now = System.DateTimeOffset.UtcNow;
        await store.CreateTagGrantAsync(new TagGrant
        {
            LocalPart = "reports",
            Tag = "CASE-18472",
            ExpiresAt = now.AddDays(1),
        });

        TagGrant? lower = await store.GetActiveTagGrantAsync("REPORTS", "case-18472", now);
        Assert.NotNull(lower);
    }

    [Fact]
    public async System.Threading.Tasks.Task SaveInboundMessage_AssignsIdAndTimestamp()
    {
        var store = new InMemoryMessageStore();
        InboundMessage saved = await store.SaveInboundMessageAsync(new InboundMessage
        {
            EnvelopeFrom = "from@a",
            EnvelopeTo = "to@b",
            LocalPart = "reports",
            Tag = "X",
            Subject = "test",
            RawBytes = new byte[] { 1, 2, 3 },
        });

        Assert.NotEqual(System.Guid.Empty, saved.Id);
        Assert.NotEqual(default, saved.ReceivedAt);
        Assert.Single(store.Messages);
    }

    [Fact]
    public async System.Threading.Tasks.Task SaveWebhookDelivery_AssignsIdAndTimestamp()
    {
        var store = new InMemoryMessageStore();
        InboundMessage msg = await store.SaveInboundMessageAsync(new InboundMessage
        {
            EnvelopeFrom = "a@b",
            EnvelopeTo = "c@d",
            LocalPart = "reports",
            RawBytes = System.Array.Empty<byte>(),
        });

        WebhookDelivery delivery = await store.SaveWebhookDeliveryAsync(new WebhookDelivery
        {
            InboundMessageId = msg.Id,
            Url = "http://hook",
            StatusCode = 200,
        });

        Assert.NotEqual(System.Guid.Empty, delivery.Id);
        Assert.NotEqual(default, delivery.AttemptedAt);
        Assert.Single(store.Deliveries);
    }

    [Fact]
    public async System.Threading.Tasks.Task ListRoutingRules_ReturnsInOrder()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertRoutingRuleAsync(new RoutingRule { LocalPart = "a", WebhookUrl = "http://a", WebhookSecret = "0" });
        await store.UpsertRoutingRuleAsync(new RoutingRule { LocalPart = "b", WebhookUrl = "http://b", WebhookSecret = "0" });
        await store.UpsertRoutingRuleAsync(new RoutingRule { LocalPart = "c", WebhookUrl = "http://c", WebhookSecret = "0" });

        System.Collections.Generic.IReadOnlyList<RoutingRule> all = await store.ListRoutingRulesAsync();
        Assert.Equal(3, all.Count);
        Assert.Equal("a", all[0].LocalPart);
        Assert.Equal("b", all[1].LocalPart);
        Assert.Equal("c", all[2].LocalPart);
    }
}
