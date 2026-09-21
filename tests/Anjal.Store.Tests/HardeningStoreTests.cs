namespace Anjal.Store.Tests;

public class HardeningStoreTests
{
    private static readonly byte[] Key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void SecretProtector_RoundTrips_AndSealsWithRandomNonces()
    {
        var p = new SecretProtector(Key);
        string a = p.Seal("-----BEGIN PRIVATE KEY-----\nabc\n", "dkim:x.test:default");
        string b = p.Seal("-----BEGIN PRIVATE KEY-----\nabc\n", "dkim:x.test:default");
        Assert.StartsWith(SecretProtector.Prefix, a, System.StringComparison.Ordinal);
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("BEGIN", a, System.StringComparison.Ordinal);
        Assert.Equal("-----BEGIN PRIVATE KEY-----\nabc\n", p.Open(a, "dkim:x.test:default"));
    }

    [Fact]
    public void SecretProtector_RefusesTamperingAWrongKeyOrAMovedValue()
    {
        var p = new SecretProtector(Key);
        string sealedValue = p.Seal("secret", "dkim:x.test:default");

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => p.Open(sealedValue, "dkim:other.test:default"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            new SecretProtector(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).Open(sealedValue, "dkim:x.test:default"));

        char[] chars = sealedValue.ToCharArray();
        int i = SecretProtector.Prefix.Length + 20;
        chars[i] = chars[i] == 'A' ? 'B' : 'A';
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => p.Open(new string(chars), "dkim:x.test:default"));
    }

    [Fact]
    public void SecretProtector_PassesLegacyPlaintextThrough_AndRequires32ByteKeys()
    {
        var p = new SecretProtector(Key);
        Assert.Equal("-----BEGIN X-----", p.Open("-----BEGIN X-----", "any"));
        Assert.Throws<System.ArgumentException>(() => new SecretProtector(new byte[16]));
    }

    [Theory]
    [InlineData("INBOX", true)]
    [InlineData("Projects 2026", true)]
    [InlineData("", false)]
    [InlineData("../x", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData(".hidden", false)]
    [InlineData(" padded", false)]
    [InlineData("tab\there", false)]
    public void FolderNames_AreValidated(string name, bool valid)
    {
        Assert.Equal(valid, FolderRow.IsValidName(name));
    }

    [Fact]
    public async System.Threading.Tasks.Task FolderNames_LongerThan64_AreRefused_AndTheStoreEnforcesIt()
    {
        Assert.False(FolderRow.IsValidName(new string('a', 65)));
        var store = new InMemoryMessageStore();
        await Assert.ThrowsAsync<System.ArgumentException>(() => store.EnsureFolderAsync(System.Guid.NewGuid(), "../etc"));
    }

    [Fact]
    public async System.Threading.Tasks.Task OutboundLease_ThatLapses_IsTakenAgain()
    {
        var store = new InMemoryMessageStore();
        var now = new System.DateTimeOffset(2026, 9, 21, 10, 0, 0, System.TimeSpan.Zero);
        OutboundMessage m = await store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = "a@b.test",
            EnvelopeTo = "c@d.test",
            RawBytes = new byte[] { 1 },
            NextAttemptAt = now,
            GiveUpAt = now.AddDays(1),
        });

        Assert.Single(await store.LeaseOutboundBatchAsync(10, now));
        // The worker "crashes": nothing is marked. Before the lease lapses the
        // message is not handed out twice...
        Assert.Empty(await store.LeaseOutboundBatchAsync(10, now.AddMinutes(5)));
        // ...and afterwards it is reclaimed instead of stranded in Sending.
        IReadOnlyList<OutboundMessage> again = await store.LeaseOutboundBatchAsync(10, now + OutboundMessage.LeaseDuration + System.TimeSpan.FromSeconds(1));
        Assert.Equal(m.Id, Assert.Single(again).Id);
    }

    [Fact]
    public async System.Threading.Tasks.Task WebhookJobs_LeaseCompleteAndReclaim()
    {
        var store = new InMemoryMessageStore();
        var now = new System.DateTimeOffset(2026, 9, 21, 10, 0, 0, System.TimeSpan.Zero);
        WebhookJob job = await store.EnqueueWebhookJobAsync(new WebhookJob { LocalPart = "lab", NextAttemptAt = now, GiveUpAt = now.AddDays(1) });

        Assert.Single(await store.LeaseWebhookJobsAsync(10, now));
        Assert.Empty(await store.LeaseWebhookJobsAsync(10, now.AddSeconds(1)));
        Assert.Single(await store.LeaseWebhookJobsAsync(10, now + WebhookJob.LeaseDuration + System.TimeSpan.FromSeconds(1)));

        await store.CompleteWebhookJobAsync(job.Id, WebhookJobStatus.Delivered, now, string.Empty);
        Assert.Equal(1, await store.CountWebhookJobsAsync(WebhookJobStatus.Delivered));
        Assert.Empty(await store.LeaseWebhookJobsAsync(10, now.AddDays(2)));
    }

    [Fact]
    public async System.Threading.Tasks.Task Audit_AppendsAndListsNewestFirst()
    {
        var store = new InMemoryMessageStore();
        await store.AppendAuditAsync(new AuditEvent { Actor = "api", Action = "POST /api/mailboxes", Subject = "/api/mailboxes" });
        await store.AppendAuditAsync(new AuditEvent { Actor = "arun@anjal.co.in", Action = "webmail.password.changed" });
        IReadOnlyList<AuditEvent> events = await store.ListAuditAsync(10);
        Assert.Equal(2, events.Count);
        Assert.Equal("webmail.password.changed", events[0].Action);
        Assert.NotEqual(System.Guid.Empty, events[0].Id);
        Assert.Single(await store.ListAuditAsync(1));
    }
}
