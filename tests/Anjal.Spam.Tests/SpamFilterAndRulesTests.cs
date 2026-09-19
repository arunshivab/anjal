using Anjal.Mime;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Spam.Tests;

public class SpamFilterAndRulesTests
{
    private sealed class CaptureSink : IMessageSink
    {
        public DeliveryContext? Last { get; private set; }

        public System.Threading.Tasks.Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, System.Threading.CancellationToken ct = default)
        {
            this.Last = ctx;
            return System.Threading.Tasks.Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Accepted, ReplyText = "ok" });
        }
    }

    private static readonly string[] OneRecipient = new[] { "arun@anjal.co.in" };
    private const string Spammy = "Subject: YOU HAVE WON THE LOTTERY\r\n\r\nClick here, act now, guaranteed.\r\n"; // no From/Date/Message-ID

    private static DeliveryContext Ctx(string? user = null) => new()
    {
        EnvelopeFrom = "a@example.com",
        EnvelopeTo = OneRecipient,
        RawBytes = System.Text.Encoding.ASCII.GetBytes(Spammy),
        RemoteAddress = "203.0.113.10",
        ClientHostName = "mail.example.com",
        AuthenticatedUser = user,
    };

    [Fact]
    public async System.Threading.Tasks.Task JunkMode_AnnotatesAndPassesThrough()
    {
        var inner = new CaptureSink();
        var sink = new SpamFilterSink(new SpamScorer(), inner);
        DeliveryResult r = await sink.DeliverAsync(Ctx());

        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        MimeMessage parsed = MimeParser.Parse(inner.Last!.RawBytes);
        int score = SpamHeaders.ScoreOf(parsed);
        Assert.True(score >= 5, $"score {score}");
        Assert.Contains("PHRASES(3)", parsed.Headers.Get(SpamHeaders.Reasons), System.StringComparison.Ordinal);
        Assert.Equal("YOU HAVE WON THE LOTTERY", parsed.Subject);
    }

    [Fact]
    public async System.Threading.Tasks.Task RejectMode_RefusesAtThreshold_AndPassesBelow()
    {
        var inner = new CaptureSink();
        var reject = new SpamFilterSink(new SpamScorer(), inner) { Action = SpamAction.Reject, RejectThreshold = 5 };
        DeliveryResult r = await reject.DeliverAsync(Ctx());
        Assert.Equal(DeliveryOutcome.PermanentFailure, r.Outcome);
        Assert.Null(inner.Last);

        var lenient = new SpamFilterSink(new SpamScorer(), inner) { Action = SpamAction.Reject, RejectThreshold = 100 };
        DeliveryResult ok = await lenient.DeliverAsync(Ctx());
        Assert.Equal(DeliveryOutcome.Accepted, ok.Outcome);
        Assert.NotNull(inner.Last);
    }

    [Fact]
    public async System.Threading.Tasks.Task AuthenticatedMail_IsNeverScored()
    {
        var inner = new CaptureSink();
        var sink = new SpamFilterSink(new SpamScorer(), inner) { Action = SpamAction.Reject, RejectThreshold = 1 };
        DeliveryResult r = await sink.DeliverAsync(Ctx(user: "arun@anjal.co.in"));
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        Assert.Null(MimeParser.Parse(inner.Last!.RawBytes).Headers.Get(SpamHeaders.Score));
    }

    [Fact]
    public void Headers_PrependAndRead_FirstOccurrenceWins()
    {
        byte[] forged = System.Text.Encoding.ASCII.GetBytes("X-Anjal-Spam-Score: 0\r\nSubject: x\r\n\r\nb\r\n");
        var verdict = new SpamVerdict { Score = 7, Reasons = new[] { new SpamReason { Code = "A", Points = 7 } } };
        byte[] annotated = SpamHeaders.Prepend(forged, verdict);
        Assert.Equal(7, SpamHeaders.ScoreOf(MimeParser.Parse(annotated)));
        Assert.Equal(0, SpamHeaders.ScoreOf(null));
    }

    private static SenderRuleRow Rule(string pattern, SenderRuleAction action) => new() { Pattern = pattern, Action = action };

    [Fact]
    public void SenderRules_ExactBeatsDomain_BlockBeatsAllow()
    {
        var rules = new[]
        {
            Rule("@example.com", SenderRuleAction.Block),
            Rule("alice@example.com", SenderRuleAction.Allow),
        };
        Assert.Equal(SenderRuleAction.Allow, SenderRules.Evaluate(rules, "alice@example.com", string.Empty));
        Assert.Equal(SenderRuleAction.Block, SenderRules.Evaluate(rules, "bob@example.com", string.Empty));
        Assert.Equal(SenderRuleAction.Block, SenderRules.Evaluate(rules, "bob@mail.example.com", string.Empty));
        Assert.Null(SenderRules.Evaluate(rules, "x@other.test", string.Empty));
        Assert.Null(SenderRules.Evaluate(System.Array.Empty<SenderRuleRow>(), "x@other.test", string.Empty));

        var both = new[] { Rule("alice@example.com", SenderRuleAction.Allow), Rule("alice@example.com", SenderRuleAction.Block) };
        Assert.Equal(SenderRuleAction.Block, SenderRules.Evaluate(both, "ALICE@EXAMPLE.COM", string.Empty));
    }

    [Fact]
    public void SenderRules_MatchFromHeaderToo()
    {
        var rules = new[] { Rule("@spammer.test", SenderRuleAction.Block) };
        Assert.Equal(SenderRuleAction.Block, SenderRules.Evaluate(rules, "bounce@legit.test", "news@spammer.test"));
    }
}
