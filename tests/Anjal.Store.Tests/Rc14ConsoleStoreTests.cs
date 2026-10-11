namespace Anjal.Store.Tests;

/// <summary>rc.14: what the consoles read from the store - the audit trail's chain, the queue's oldest message, arrivals by hour.</summary>
public sealed class Rc14ConsoleStoreTests
{
    [Fact]
    public async Task AuditChain_IsIntact_UntilAnEntryIsChanged()
    {
        var store = new InMemoryMessageStore();
        AuditEvent first = await store.AppendAuditAsync(new AuditEvent { Actor = "arun@anjal.co.in", Action = "org.retention", Subject = "imagiqa", RemoteAddress = "10.0.0.1" });
        AuditEvent second = await store.AppendAuditAsync(new AuditEvent { Actor = "arun@anjal.co.in", Action = "org.person.invited", Subject = "rahul@anjal.co.in", RemoteAddress = "10.0.0.1" });
        Assert.Equal(1, first.Seq);
        Assert.Equal(2, second.Seq);
        Assert.Equal(64, second.Chain.Length);
        Assert.Equal(AuditEvent.ComputeChain(first.Chain, second), second.Chain);

        AuditChainCheck intact = await store.VerifyAuditChainAsync();
        Assert.True(intact.Intact);
        Assert.Equal(2, intact.Checked);
        Assert.Null(intact.BrokenAt);

        // Change the first entry's words, as someone with the database might: the chain shows it.
        AuditEvent stored = (await store.ListAuditAsync(10)).Single(e => e.Seq == 1);
        stored.Subject = "someone-else";
        AuditChainCheck broken = await store.VerifyAuditChainAsync();
        Assert.False(broken.Intact);
        Assert.Equal(1, broken.BrokenAt);
    }

    [Fact]
    public async Task EmptyService_HasNothingWaiting_AndNoArrivals()
    {
        var store = new InMemoryMessageStore();
        Assert.Null(await store.OldestPendingOutboundAsync());
        Assert.Empty(await store.CountArrivalsByHourAsync(System.DateTimeOffset.UtcNow.AddDays(-1)));
    }
}
