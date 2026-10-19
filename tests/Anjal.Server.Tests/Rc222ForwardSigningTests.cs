using System.Security.Cryptography;
using System.Text;
using Anjal.Dkim;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Server.Tests;

/// <summary>
/// rc.22.2 (found while testing MAIL-04, 20 Oct 2026): a copy forwarded by a mailbox rule keeps
/// the outside sender's From header. The outbound worker signs by the From domain and, with
/// DKIM required (production), failed every such copy for good: "no key configured for sender
/// domain 'gmail.com'". A forwarded copy is now signed as the forwarding mailbox's domain - the
/// envelope sender - and the original sender's own signature stays on it.
/// </summary>
public sealed class Rc222ForwardSigningTests
{
    private const string Forwarded =
        "Resent-From: <arun@anjal.co.in>\r\nResent-To: <arun.home@gmail.example>\r\nResent-Date: Mon, 20 Oct 2026 01:00:00 +0000\r\n" +
        "Resent-Message-ID: <r1@anjal.co.in>\r\nX-Anjal-Forwarded: arun@anjal.co.in\r\n" +
        "From: Priya <priya@gmail.com>\r\nTo: <arun@anjal.co.in>\r\nSubject: Results\r\nDate: Mon, 20 Oct 2026 00:50:00 +0000\r\nMessage-ID: <m1@gmail.com>\r\n\r\nHb 12.1\r\n";

    private const string Own = "From: <arun@anjal.co.in>\r\nTo: <doctor@example.org>\r\nSubject: Hello\r\nDate: Mon, 20 Oct 2026 00:50:00 +0000\r\nMessage-ID: <m2@anjal.co.in>\r\n\r\nHi\r\n";

    private readonly InMemoryMessageStore store = new();
    private readonly DateTimeOffset now = new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);

    private sealed class CapturingSender : IMailSender
    {
        public List<byte[]> Transmitted { get; } = new();

        public Task<SendResult> SendAsync(OutboundDelivery delivery, CancellationToken ct = default)
        {
            this.Transmitted.Add(delivery.RawBytes);
            return Task.FromResult(new SendResult { Outcome = SendOutcome.Sent, ReplyCode = 250, Message = "2.0.0 OK", RemoteHost = "mx.example" });
        }
    }

    // Keys only for this server's own domain, as in production.
    private sealed class OwnDomainsOnly : IDkimKeyResolver
    {
        private readonly DkimKey key;

        public OwnDomainsOnly(DkimKey key) => this.key = key;

        public Task<DkimKey?> ResolveAsync(string senderDomain, CancellationToken ct = default) =>
            Task.FromResult(senderDomain == "anjal.co.in" ? this.key : null);
    }

    [Fact]
    public async Task AForwardedCopy_IsSignedAsTheForwardingDomain_NotFailedForTheOutsideSendersDomain()
    {
        using RSA rsa = RSA.Create(2048);
        var key = new DkimKey { Domain = "anjal.co.in", Selector = "default", PrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem() };
        var sender = new CapturingSender();
        var worker = new OutboundWorker(this.store, sender, new OutboundWorkerOptions(), () => this.now, log: null,
            dkimResolver: new OwnDomainsOnly(key), dkimSigner: new DkimSigner(clock: () => this.now), requireDkim: true);
        OutboundMessage copy = await this.store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = "arun@anjal.co.in",
            EnvelopeTo = "arun.home@gmail.example",
            RawBytes = Encoding.ASCII.GetBytes(Forwarded),
            CreatedAt = this.now,
            NextAttemptAt = this.now,
            GiveUpAt = this.now.AddHours(24),
        });
        OutboundMessage own = await this.store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = "arun@anjal.co.in",
            EnvelopeTo = "doctor@example.org",
            RawBytes = Encoding.ASCII.GetBytes(Own),
            CreatedAt = this.now,
            NextAttemptAt = this.now,
            GiveUpAt = this.now.AddHours(24),
        });

        await worker.DrainOnceAsync();

        Assert.Equal(2, sender.Transmitted.Count);
        foreach (byte[] sent in sender.Transmitted)
        {
            string text = Encoding.ASCII.GetString(sent);
            Assert.StartsWith("DKIM-Signature:", text, StringComparison.Ordinal);
            Assert.Contains("d=anjal.co.in;", text, StringComparison.Ordinal);
        }
        string forwarded = Encoding.ASCII.GetString(sender.Transmitted.Single(b => Encoding.ASCII.GetString(b).Contains("X-Anjal-Forwarded", StringComparison.Ordinal)));
        Assert.Contains("From: Priya <priya@gmail.com>", forwarded, StringComparison.Ordinal);   // the original From is untouched
        Assert.Equal(OutboundStatus.Sent, (await this.store.GetOutboundByIdAsync(copy.Id))!.Status);
        Assert.Equal(OutboundStatus.Sent, (await this.store.GetOutboundByIdAsync(own.Id))!.Status);
    }

    [Fact]
    public async Task MailThatIsNotAForward_FromADomainWithoutAKey_StillFailsWhenSigningIsRequired()
    {
        using RSA rsa = RSA.Create(2048);
        var key = new DkimKey { Domain = "anjal.co.in", Selector = "default", PrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem() };
        var sender = new CapturingSender();
        var worker = new OutboundWorker(this.store, sender, new OutboundWorkerOptions(), () => this.now, log: null,
            dkimResolver: new OwnDomainsOnly(key), dkimSigner: new DkimSigner(clock: () => this.now), requireDkim: true);
        OutboundMessage odd = await this.store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = "arun@anjal.co.in",
            EnvelopeTo = "doctor@example.org",
            RawBytes = Encoding.ASCII.GetBytes("From: <someone@other.example>\r\nTo: <doctor@example.org>\r\nSubject: Odd\r\n\r\nx\r\n"),
            CreatedAt = this.now,
            NextAttemptAt = this.now,
            GiveUpAt = this.now.AddHours(24),
        });
        await worker.DrainOnceAsync();
        Assert.Empty(sender.Transmitted);
        Assert.Equal(OutboundStatus.Failed, (await this.store.GetOutboundByIdAsync(odd.Id))!.Status);
    }
}
