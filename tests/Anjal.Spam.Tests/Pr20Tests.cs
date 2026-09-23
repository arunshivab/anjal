using System.Text;

namespace Anjal.Spam.Tests;

public class Pr20Tests
{
    [Theory]
    [InlineData("@.", false)]
    [InlineData("@.com", false)]
    [InlineData("@example.", false)]
    [InlineData("@a..b", false)]
    [InlineData("@-hospital.example", false)]
    [InlineData("@hospital-.example", false)]
    [InlineData("@localhost", false)]
    [InlineData("a@b_c.example", false)]
    [InlineData("@hospital.example", true)]
    [InlineData("accounts@vendor.example", true)]
    [InlineData("@sub.apulki.co.in", true)]
    [InlineData("@x-ray.example", true)]
    public void DEF035_TheDomainMustBeRealShaped(string pattern, bool valid)
    {
        Assert.Equal(valid, SenderRules.IsValidPattern(pattern));
    }

    [Fact]
    public void READ10_OurReceivedLineStaysFirst_TheSpamHeadersGoBeneathIt()
    {
        byte[] raw = Encoding.ASCII.GetBytes("Received: from a.test ([192.0.2.1])\r\n\tby mx.test with ESMTP id 1;\r\n\tMon, 21 Sep 2026 10:00:00 +0000\r\nSubject: x\r\n\r\nbody\r\n");
        string result = Encoding.ASCII.GetString(SpamHeaders.Prepend(raw, new SpamVerdict { Score = 2 }));
        Assert.StartsWith("Received: from a.test ([192.0.2.1])\r\n\tby mx.test with ESMTP id 1;\r\n\tMon, 21 Sep 2026 10:00:00 +0000\r\nX-Anjal-Spam-Score: 2\r\n", result, StringComparison.Ordinal);
        Assert.EndsWith("Subject: x\r\n\r\nbody\r\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void READ10_WithoutALeadingReceived_TheHeadersStillGoFirst()
    {
        string result = Encoding.ASCII.GetString(SpamHeaders.Prepend(Encoding.ASCII.GetBytes("Subject: x\r\n\r\nb\r\n"), new SpamVerdict { Score = 0 }));
        Assert.StartsWith("X-Anjal-Spam-Score: 0\r\n", result, StringComparison.Ordinal);
    }
}

/// <summary>
/// DEF-039: scripts without capitals must not be scored as shouting. These
/// are the languages Anjal is built for.
/// </summary>
public class CaselessScriptTests
{
    private sealed class NoDns : ISpamDnsLookup
    {
        public Task<bool?> CanReceiveMailAsync(string domain, CancellationToken ct = default) => Task.FromResult<bool?>(true);

        public Task<bool?> ResolvesAsync(string host, CancellationToken ct = default) => Task.FromResult<bool?>(true);

        public Task<bool?> HasReverseDnsAsync(string ip, CancellationToken ct = default) => Task.FromResult<bool?>(true);
    }

    private static readonly string[] OneRecipient = { "arun@qa.test" };

    private static bool Shouts(string subject)
    {
        byte[] raw = System.Text.Encoding.UTF8.GetBytes(
            $"From: someone@hospital.example\r\nTo: arun@qa.test\r\nSubject: {subject}\r\nDate: Mon, 21 Sep 2026 10:00:00 +0000\r\n" +
            "Message-ID: <x@hospital.example>\r\nContent-Type: text/plain\r\n\r\nbody text here\r\n");
        var ctx = new Anjal.Smtp.DeliveryContext
        {
            EnvelopeFrom = "someone@hospital.example",
            EnvelopeTo = OneRecipient,
            RawBytes = raw,
            RemoteAddress = "203.0.113.10",
            ClientHostName = "mail.hospital.example",
        };
        SpamVerdict v = new SpamScorer(null, new NoDns()).ScoreAsync(ctx, Anjal.Mime.MimeParser.Parse(raw)).GetAwaiter().GetResult();
        return v.Reasons.Any(r => r.Code == "SUBJECT_CAPS");
    }

    [Theory]
    [InlineData("காலை வணக்கம் மருத்துவமனை")]              // Tamil
    [InlineData("नमस्ते अस्पताल रिपोर्ट")]                      // Hindi
    [InlineData("നമസ്കാരം ആശുപത്രി റിപ്പോർട്ട്")]                 // Malayalam
    [InlineData("患者の報告書を送ります")]                        // Japanese
    [InlineData("مرحبا تقرير المستشفى")]                      // Arabic
    [InlineData("lowercase english subject line")]
    public void ScriptsWithoutCapitals_AreNotShouting(string subject)
    {
        Assert.False(Shouts(subject));
    }

    [Theory]
    [InlineData("URGENT CLAIM YOUR PRIZE NOW")]
    [InlineData("FINAL NOTICE REGARDING YOUR ACCOUNT")]
    public void ShoutingInALatinScript_StillCounts(string subject)
    {
        Assert.True(Shouts(subject));
    }

    [Fact]
    public void TamilWithAShoutingEnglishWord_IsStillJudgedOnTheCasedLetters()
    {
        Assert.False(Shouts("மருத்துவமனை ok"));           // a lowercase cased letter: not shouting
    }
}
