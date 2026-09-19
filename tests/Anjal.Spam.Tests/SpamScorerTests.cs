using Anjal.Auth;
using Anjal.Mime;
using Anjal.Smtp;

namespace Anjal.Spam.Tests;

public class SpamScorerTests
{
    private sealed class FakeDns : ISpamDnsLookup
    {
        public bool? CanReceive { get; set; } = true;

        public bool? Resolves { get; set; } = true;

        public bool? Reverse { get; set; } = true;

        public System.Threading.Tasks.Task<bool?> CanReceiveMailAsync(string domain, System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.FromResult(this.CanReceive);

        public System.Threading.Tasks.Task<bool?> ResolvesAsync(string hostName, System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.FromResult(this.Resolves);

        public System.Threading.Tasks.Task<bool?> HasReverseDnsAsync(string address, System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.FromResult(this.Reverse);
    }

    private static readonly string[] OneRecipient = new[] { "arun@anjal.co.in" };

    private const string GoodMail =
        "From: Alice <alice@example.com>\r\nTo: arun@anjal.co.in\r\nSubject: Lunch on Friday?\r\nDate: Sat, 19 Sep 2026 10:00:00 +0530\r\n" +
        "Message-ID: <good@example.com>\r\nContent-Type: text/plain\r\n\r\nShall we?\r\n";

    private static DeliveryContext Ctx(string raw, string from = "alice@example.com", string helo = "mail.example.com", string ip = "203.0.113.10", object? auth = null) => new()
    {
        EnvelopeFrom = from,
        EnvelopeTo = OneRecipient,
        RawBytes = System.Text.Encoding.ASCII.GetBytes(raw),
        RemoteAddress = ip,
        ClientHostName = helo,
        AuthResults = auth,
    };

    private static AuthenticationResults Auth(SpfResult spf, DkimResult dkim, DmarcResult dmarc) => new()
    {
        Spf = new SpfDetail { Result = spf, Explanation = "spf" },
        Dkim = new DkimDetail { Result = dkim, Explanation = "dkim" },
        Dmarc = new DmarcDetail { Result = dmarc, Explanation = "dmarc" },
    };

    [Fact]
    public async System.Threading.Tasks.Task CleanMail_ScoresZero()
    {
        var scorer = new SpamScorer(null, new FakeDns());
        DeliveryContext ctx = Ctx(GoodMail, auth: Auth(SpfResult.Pass, DkimResult.Pass, DmarcResult.Pass));
        SpamVerdict v = await scorer.ScoreAsync(ctx, MimeParser.Parse(ctx.RawBytes));
        Assert.Equal(0, v.Score);
        Assert.Empty(v.Reasons);
    }

    [Fact]
    public async System.Threading.Tasks.Task AuthenticationFailures_AddPoints()
    {
        var scorer = new SpamScorer(null, new FakeDns());
        DeliveryContext ctx = Ctx(GoodMail, auth: Auth(SpfResult.Fail, DkimResult.Fail, DmarcResult.Fail));
        SpamVerdict v = await scorer.ScoreAsync(ctx, MimeParser.Parse(ctx.RawBytes));
        Assert.Equal(9, v.Score);
        Assert.Contains(v.Reasons, r => r.Code == "SPF_FAIL");
        Assert.Contains(v.Reasons, r => r.Code == "DKIM_FAIL");
        Assert.Contains(v.Reasons, r => r.Code == "DMARC_FAIL");
        Assert.Equal("SPF_FAIL(3), DKIM_FAIL(3), DMARC_FAIL(3)", v.ReasonsHeaderValue);
    }

    [Fact]
    public async System.Threading.Tasks.Task NoAuthRecords_ScoreLightly()
    {
        var scorer = new SpamScorer(null, new FakeDns());
        DeliveryContext ctx = Ctx(GoodMail, auth: Auth(SpfResult.None, DkimResult.None, DmarcResult.None));
        SpamVerdict v = await scorer.ScoreAsync(ctx, MimeParser.Parse(ctx.RawBytes));
        Assert.Equal(2, v.Score);
    }

    [Fact]
    public async System.Threading.Tasks.Task NoAuthResults_SkipsAuthRules()
    {
        var scorer = new SpamScorer(null, new FakeDns());
        DeliveryContext ctx = Ctx(GoodMail, auth: null);
        SpamVerdict v = await scorer.ScoreAsync(ctx, MimeParser.Parse(ctx.RawBytes));
        Assert.Equal(0, v.Score);
    }

    [Fact]
    public async System.Threading.Tasks.Task DnsSignals_HeloSenderDomainReverse()
    {
        var dns = new FakeDns { CanReceive = false, Resolves = false, Reverse = false };
        var scorer = new SpamScorer(null, dns);
        DeliveryContext ctx = Ctx(GoodMail, helo: "mail.nowhere.invalid");
        SpamVerdict v = await scorer.ScoreAsync(ctx, MimeParser.Parse(ctx.RawBytes));
        Assert.Contains(v.Reasons, r => r.Code == "HELO_UNRESOLVABLE");
        Assert.Contains(v.Reasons, r => r.Code == "SENDER_DOMAIN_UNREACHABLE");
        Assert.Contains(v.Reasons, r => r.Code == "NO_RDNS");
        Assert.Equal(5, v.Score);
    }

    [Fact]
    public async System.Threading.Tasks.Task Helo_BareIpOrNoDot_IsMalformed_EvenWithoutDns()
    {
        var scorer = new SpamScorer(null, null);
        SpamVerdict ip = await scorer.ScoreAsync(Ctx(GoodMail, helo: "[203.0.113.10]"), MimeParser.Parse(System.Text.Encoding.ASCII.GetBytes(GoodMail)));
        SpamVerdict nodot = await scorer.ScoreAsync(Ctx(GoodMail, helo: "localhost"), MimeParser.Parse(System.Text.Encoding.ASCII.GetBytes(GoodMail)));
        Assert.Contains(ip.Reasons, r => r.Code == "HELO_MALFORMED");
        Assert.Contains(nodot.Reasons, r => r.Code == "HELO_MALFORMED");
    }

    [Fact]
    public async System.Threading.Tasks.Task PrivateOrLoopbackClient_SkipsReverseDns()
    {
        var dns = new FakeDns { Reverse = false };
        var scorer = new SpamScorer(null, dns);
        SpamVerdict v = await scorer.ScoreAsync(Ctx(GoodMail, ip: "127.0.0.1"), MimeParser.Parse(System.Text.Encoding.ASCII.GetBytes(GoodMail)));
        Assert.DoesNotContain(v.Reasons, r => r.Code == "NO_RDNS");
    }

    [Fact]
    public async System.Threading.Tasks.Task UnknownDnsAnswer_IsNeutral()
    {
        var dns = new FakeDns { CanReceive = null, Resolves = null, Reverse = null };
        var scorer = new SpamScorer(null, dns);
        SpamVerdict v = await scorer.ScoreAsync(Ctx(GoodMail), MimeParser.Parse(System.Text.Encoding.ASCII.GetBytes(GoodMail)));
        Assert.Equal(0, v.Score);
    }

    [Fact]
    public async System.Threading.Tasks.Task HeaderHygiene_MissingFromMessageIdDate_AndMismatch()
    {
        var scorer = new SpamScorer(null, new FakeDns());
        string bare = "Subject: hi\r\n\r\nbody\r\n";
        SpamVerdict v = await scorer.ScoreAsync(Ctx(bare), MimeParser.Parse(System.Text.Encoding.ASCII.GetBytes(bare)));
        Assert.Contains(v.Reasons, r => r.Code == "MISSING_FROM");
        Assert.Contains(v.Reasons, r => r.Code == "NO_MESSAGE_ID");
        Assert.Contains(v.Reasons, r => r.Code == "NO_DATE");
        Assert.Equal(4, v.Score);

        string mismatch = "From: x@other.test\r\nDate: d\r\nMessage-ID: <m@x>\r\nSubject: hi\r\n\r\nbody\r\n";
        SpamVerdict m = await scorer.ScoreAsync(Ctx(mismatch, from: "bounce@example.com"), MimeParser.Parse(System.Text.Encoding.ASCII.GetBytes(mismatch)));
        Assert.Contains(m.Reasons, r => r.Code == "FROM_MISMATCH");

        string sub = "From: x@mail.example.com\r\nDate: d\r\nMessage-ID: <m@x>\r\nSubject: hi\r\n\r\nbody\r\n";
        SpamVerdict s = await scorer.ScoreAsync(Ctx(sub, from: "bounce@example.com"), MimeParser.Parse(System.Text.Encoding.ASCII.GetBytes(sub)));
        Assert.DoesNotContain(s.Reasons, r => r.Code == "FROM_MISMATCH");
    }

    [Fact]
    public async System.Threading.Tasks.Task Content_CapsAndPhrases_CappedAtMax()
    {
        var scorer = new SpamScorer(null, new FakeDns());
        string spam = "From: a@example.com\r\nDate: d\r\nMessage-ID: <m@x>\r\nSubject: YOU HAVE WON THE LOTTERY\r\n\r\n" +
                      "Act now! Click here for a guaranteed risk-free wire transfer. Dear friend, viagra.\r\n";
        SpamVerdict v = await scorer.ScoreAsync(Ctx(spam), MimeParser.Parse(System.Text.Encoding.ASCII.GetBytes(spam)));
        Assert.Contains(v.Reasons, r => r.Code == "SUBJECT_CAPS");
        SpamReason phrases = Assert.Single(v.Reasons, r => r.Code == "PHRASES");
        Assert.Equal(3, phrases.Points);
        Assert.Equal(4, v.Score);
    }

    [Fact]
    public async System.Threading.Tasks.Task ManyRecipients_AddsPoint()
    {
        var scorer = new SpamScorer(null, new FakeDns());
        var rcpts = new string[25];
        for (int i = 0; i < rcpts.Length; i++)
        {
            rcpts[i] = $"u{i}@anjal.co.in";
        }
        var ctx = new DeliveryContext { EnvelopeFrom = "a@example.com", EnvelopeTo = rcpts, RawBytes = System.Text.Encoding.ASCII.GetBytes(GoodMail), ClientHostName = "mail.example.com", RemoteAddress = "203.0.113.10" };
        SpamVerdict v = await scorer.ScoreAsync(ctx, MimeParser.Parse(ctx.RawBytes));
        Assert.Contains(v.Reasons, r => r.Code == "MANY_RCPT");
    }

    [Fact]
    public void Helpers_DomainOfAndFirstAddress()
    {
        Assert.Equal("example.com", SpamScorer.DomainOf("Bob@Example.COM"));
        Assert.Equal(string.Empty, SpamScorer.DomainOf("nope"));
        Assert.Equal("alice@example.com", SpamScorer.FirstAddress("Alice <Alice@Example.com>, b@c.d"));
        Assert.Equal(string.Empty, SpamScorer.FirstAddress(string.Empty));
    }
}
