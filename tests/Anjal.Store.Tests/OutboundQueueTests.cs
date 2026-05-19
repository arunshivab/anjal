namespace Anjal.Store.Tests;

public class OutboundQueueTests
{
    [Fact]
    public async System.Threading.Tasks.Task Enqueue_AssignsIdAndDefaults()
    {
        var store = new InMemoryMessageStore();
        OutboundMessage saved = await store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = "a@b",
            EnvelopeTo = "c@d",
            RawBytes = new byte[] { 1, 2, 3 },
        });

        Assert.NotEqual(System.Guid.Empty, saved.Id);
        Assert.Equal(OutboundStatus.Pending, saved.Status);
        Assert.Equal(0, saved.Attempts);
        Assert.NotEqual(default, saved.CreatedAt);
        Assert.NotEqual(default, saved.GiveUpAt);
        // Default give-up should be 24 hours after creation.
        Assert.True(saved.GiveUpAt - saved.CreatedAt > System.TimeSpan.FromHours(23));
        Assert.True(saved.GiveUpAt - saved.CreatedAt < System.TimeSpan.FromHours(25));
    }

    [Fact]
    public async System.Threading.Tasks.Task LeaseBatch_OnlyPendingDueMessages()
    {
        var store = new InMemoryMessageStore();
        var now = new System.DateTimeOffset(2026, 5, 19, 12, 0, 0, System.TimeSpan.Zero);

        // Due now.
        await store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = "a@b",
            EnvelopeTo = "due@x",
            RawBytes = System.Array.Empty<byte>(),
            CreatedAt = now,
            NextAttemptAt = now,
            GiveUpAt = now.AddHours(24),
        });

        // Not yet due.
        await store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = "a@b",
            EnvelopeTo = "future@x",
            RawBytes = System.Array.Empty<byte>(),
            CreatedAt = now,
            NextAttemptAt = now.AddMinutes(5),
            GiveUpAt = now.AddHours(24),
        });

        System.Collections.Generic.IReadOnlyList<OutboundMessage> leased =
            await store.LeaseOutboundBatchAsync(10, now);

        Assert.Single(leased);
        Assert.Equal("due@x", leased[0].EnvelopeTo);
        Assert.Equal(OutboundStatus.Sending, leased[0].Status);
    }

    [Fact]
    public async System.Threading.Tasks.Task LeaseBatch_DoesNotReleaseAlreadySending()
    {
        var store = new InMemoryMessageStore();
        var now = System.DateTimeOffset.UtcNow;

        await store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = "a@b",
            EnvelopeTo = "x@y",
            RawBytes = System.Array.Empty<byte>(),
            NextAttemptAt = now,
            GiveUpAt = now.AddHours(24),
        });

        System.Collections.Generic.IReadOnlyList<OutboundMessage> first = await store.LeaseOutboundBatchAsync(10, now);
        System.Collections.Generic.IReadOnlyList<OutboundMessage> second = await store.LeaseOutboundBatchAsync(10, now);

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public async System.Threading.Tasks.Task MarkResult_Sent_TerminalState()
    {
        var store = new InMemoryMessageStore();
        OutboundMessage m = await store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = "a@b",
            EnvelopeTo = "x@y",
            RawBytes = System.Array.Empty<byte>(),
        });

        OutboundMessage? updated = await store.MarkOutboundResultAsync(
            m.Id, OutboundStatus.Sent, System.DateTimeOffset.UtcNow, "250 OK");

        Assert.NotNull(updated);
        Assert.Equal(OutboundStatus.Sent, updated.Status);
        Assert.Equal(1, updated.Attempts);
        Assert.Equal("250 OK", updated.LastError);
    }

    [Fact]
    public async System.Threading.Tasks.Task MarkResult_RetryUpdatesNextAttemptAt()
    {
        var store = new InMemoryMessageStore();
        var now = System.DateTimeOffset.UtcNow;
        OutboundMessage m = await store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = "a@b",
            EnvelopeTo = "x@y",
            RawBytes = System.Array.Empty<byte>(),
            NextAttemptAt = now,
            GiveUpAt = now.AddHours(24),
        });

        System.DateTimeOffset retryAt = now.AddMinutes(5);
        OutboundMessage? updated = await store.MarkOutboundResultAsync(
            m.Id, OutboundStatus.Pending, retryAt, "transient");

        Assert.NotNull(updated);
        Assert.Equal(OutboundStatus.Pending, updated.Status);
        Assert.Equal(retryAt, updated.NextAttemptAt);
        Assert.Equal(1, updated.Attempts);
    }

    [Fact]
    public async System.Threading.Tasks.Task MarkResult_UnknownId_ReturnsNull()
    {
        var store = new InMemoryMessageStore();
        OutboundMessage? updated = await store.MarkOutboundResultAsync(
            System.Guid.NewGuid(), OutboundStatus.Sent, System.DateTimeOffset.UtcNow, "x");
        Assert.Null(updated);
    }

    [Fact]
    public async System.Threading.Tasks.Task LeaseBatch_RespectsBatchSize()
    {
        var store = new InMemoryMessageStore();
        var now = System.DateTimeOffset.UtcNow;
        for (int i = 0; i < 5; i++)
        {
            await store.EnqueueOutboundAsync(new OutboundMessage
            {
                EnvelopeFrom = "a@b",
                EnvelopeTo = $"x{i}@y",
                RawBytes = System.Array.Empty<byte>(),
                NextAttemptAt = now,
                GiveUpAt = now.AddHours(24),
            });
        }

        System.Collections.Generic.IReadOnlyList<OutboundMessage> leased = await store.LeaseOutboundBatchAsync(3, now);
        Assert.Equal(3, leased.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task LeaseBatch_ZeroBatchSize_Throws()
    {
        var store = new InMemoryMessageStore();
        await Assert.ThrowsAsync<System.ArgumentOutOfRangeException>(async () =>
            await store.LeaseOutboundBatchAsync(0, System.DateTimeOffset.UtcNow));
    }
}
