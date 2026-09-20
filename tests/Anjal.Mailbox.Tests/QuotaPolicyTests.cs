using System.Net;
using System.Net.Sockets;
using System.Text;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Mailbox.Tests;

public class QuotaPolicyTests
{
    private static async System.Threading.Tasks.Task<(InMemoryMessageStore Store, MailboxRow Mailbox)> SeedAsync(long quota, long used)
    {
        var store = new InMemoryMessageStore();
        TenantRow tenant = await store.UpsertTenantAsync(new TenantRow { Slug = "t" }).ConfigureAwait(false);
        await store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "q.test" }).ConfigureAwait(false);
        MailboxRow mb = await store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "u", Domain = "q.test", QuotaBytes = quota }).ConfigureAwait(false);
        if (used > 0)
        {
            await store.AddMailboxUsageAsync(mb.Id, used).ConfigureAwait(false);
        }
        return (store, (await store.GetMailboxByIdAsync(mb.Id).ConfigureAwait(false))!);
    }

    [Fact]
    public void IsFull_Rules()
    {
        Assert.False(QuotaPolicy.IsFull(new MailboxRow { QuotaBytes = 100, UsedBytes = 99 }));
        Assert.True(QuotaPolicy.IsFull(new MailboxRow { QuotaBytes = 100, UsedBytes = 100 }));
        Assert.True(QuotaPolicy.IsFull(new MailboxRow { QuotaBytes = 100, UsedBytes = 500 }));
        Assert.False(QuotaPolicy.IsFull(new MailboxRow { QuotaBytes = 0, UsedBytes = 500 }), "0 means unlimited");
    }

    [Fact]
    public async System.Threading.Tasks.Task Rcpt_FullMailbox_Deferred452_OthersAllowed()
    {
        (InMemoryMessageStore store, _) = await SeedAsync(quota: 1000, used: 1000);
        var policy = new QuotaPolicy(store);

        PolicyDecision full = await policy.OnRcptToAsync("203.0.113.1", null, "a@b", "u@q.test");
        Assert.False(full.Allowed);
        Assert.Equal(452, full.ReplyCode);
        Assert.Contains("4.2.2", full.ReplyText, System.StringComparison.Ordinal);

        Assert.True((await policy.OnRcptToAsync("203.0.113.1", null, "a@b", "U+tag@Q.TEST")).Allowed == false, "tags and case resolve to the same mailbox");
        Assert.True((await policy.OnRcptToAsync("203.0.113.1", null, "a@b", "nobody@q.test")).Allowed);
        Assert.True((await policy.OnRcptToAsync("203.0.113.1", null, "a@b", "not-an-address")).Allowed);
        Assert.True((await policy.OnConnectAsync("203.0.113.1")).Allowed);
        Assert.True((await policy.OnMailFromAsync("203.0.113.1", null, "a@b")).Allowed);
    }

    [Fact]
    public async System.Threading.Tasks.Task Rcpt_UnderQuota_Allowed()
    {
        (InMemoryMessageStore store, _) = await SeedAsync(quota: 1000, used: 999);
        Assert.True((await new QuotaPolicy(store).OnRcptToAsync("203.0.113.1", null, "a@b", "u@q.test")).Allowed);
    }

    [Fact]
    public async System.Threading.Tasks.Task OverSmtp_FullMailbox_Gets452_ThenAcceptsAfterSpaceFreed()
    {
        (InMemoryMessageStore store, MailboxRow mb) = await SeedAsync(quota: 1000, used: 1000);
        var options = new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            Policy = new QuotaPolicy(store),
        };
        using var cts = new System.Threading.CancellationTokenSource();
        using var server = new SmtpServer(options, new MailboxSink(store, new MaildirStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-quota-" + System.Guid.NewGuid().ToString("N")), "t")));
        System.Threading.Tasks.Task run = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(80);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.BoundPort);
            NetworkStream net = client.GetStream();
            var reader = new StreamReader(net, Encoding.ASCII);
            var writer = new StreamWriter(net, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await reader.ReadLineAsync();
            await writer.WriteLineAsync("EHLO c");
            string? line;
            do { line = await reader.ReadLineAsync(); } while (line is not null && line.Length >= 4 && line[3] == '-');
            await writer.WriteLineAsync("MAIL FROM:<a@example.com>");
            Assert.StartsWith("250", await reader.ReadLineAsync(), System.StringComparison.Ordinal);
            await writer.WriteLineAsync("RCPT TO:<u@q.test>");
            Assert.StartsWith("452 4.2.2 Mailbox full", await reader.ReadLineAsync(), System.StringComparison.Ordinal);
            Assert.True(Counters.Get("anjal_quota_refusals_total") >= 1);

            await store.AddMailboxUsageAsync(mb.Id, -500);
            await writer.WriteLineAsync("RCPT TO:<u@q.test>");
            Assert.StartsWith("250", await reader.ReadLineAsync(), System.StringComparison.Ordinal);
            await writer.WriteLineAsync("QUIT");
        }
        finally
        {
            cts.Cancel();
            try { await run; } catch (System.OperationCanceledException) { }
        }
    }
}
