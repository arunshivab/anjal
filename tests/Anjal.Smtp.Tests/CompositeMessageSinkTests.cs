namespace Anjal.Smtp.Tests;

public class CompositeMessageSinkTests
{
    private sealed class FixedSink : IMessageSink
    {
        private readonly DeliveryOutcome outcome;
        private readonly string text;

        public FixedSink(DeliveryOutcome outcome, string text)
        {
            this.outcome = outcome;
            this.text = text;
        }

        public int Calls { get; private set; }

        public System.Threading.Tasks.Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, System.Threading.CancellationToken ct = default)
        {
            this.Calls++;
            return System.Threading.Tasks.Task.FromResult(new DeliveryResult { Outcome = this.outcome, ReplyText = this.text });
        }
    }

    private static readonly DeliveryContext Ctx = new()
    {
        EnvelopeFrom = "a@b",
        EnvelopeTo = new[] { "c@d" },
        RawBytes = new byte[] { 0x41 },
    };

    [Fact]
    public void Ctor_RequiresAtLeastOneSink()
    {
        Assert.Throws<System.ArgumentException>(() => new CompositeMessageSink());
    }

    [Fact]
    public async System.Threading.Tasks.Task AllSinksAreCalled_EvenAfterAccept()
    {
        var a = new FixedSink(DeliveryOutcome.Accepted, "a ok");
        var b = new FixedSink(DeliveryOutcome.PermanentFailure, "b no");
        var c = new FixedSink(DeliveryOutcome.Accepted, "c ok");
        var composite = new CompositeMessageSink(a, b, c);

        DeliveryResult r = await composite.DeliverAsync(Ctx);

        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        Assert.Equal("a ok; c ok", r.ReplyText);
        Assert.Equal(1, a.Calls);
        Assert.Equal(1, b.Calls);
        Assert.Equal(1, c.Calls);
        Assert.Equal(3, composite.Sinks.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task TransientWinsOverPermanent_WhenNothingAccepted()
    {
        var composite = new CompositeMessageSink(
            new FixedSink(DeliveryOutcome.PermanentFailure, "no"),
            new FixedSink(DeliveryOutcome.TransientFailure, "later"));

        DeliveryResult r = await composite.DeliverAsync(Ctx);
        Assert.Equal(DeliveryOutcome.TransientFailure, r.Outcome);
        Assert.Equal("later", r.ReplyText);
    }

    [Fact]
    public async System.Threading.Tasks.Task AllPermanent_MergesText()
    {
        var composite = new CompositeMessageSink(
            new FixedSink(DeliveryOutcome.PermanentFailure, "No such mailbox"),
            new FixedSink(DeliveryOutcome.PermanentFailure, "No accepted recipients"));

        DeliveryResult r = await composite.DeliverAsync(Ctx);
        Assert.Equal(DeliveryOutcome.PermanentFailure, r.Outcome);
        Assert.Equal("No such mailbox; No accepted recipients", r.ReplyText);
    }
}
