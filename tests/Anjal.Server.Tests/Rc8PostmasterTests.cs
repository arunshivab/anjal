using System.Net;
using System.Net.Sockets;
using System.Text;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Server.Tests;

/// <summary>
/// v1.0.0-rc.8, SPEC-08 R-14 (RFC 5321 4.5.1, RFC 2142): postmaster and abuse
/// are always accepted - through a real SMTP conversation with the server's own
/// recipient checks - and nothing else is opened.
/// </summary>
public sealed class Rc8PostmasterTests : System.IDisposable
{
    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc8-pm-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<string> log = new();

    public void Dispose()
    {
        if (System.IO.Directory.Exists(this.root))
        {
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    private async Task<TenantRow> SeedAsync(string? designated, params string[] localParts)
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", PostmasterMailbox = designated });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        foreach (string lp in localParts)
        {
            await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = lp, Domain = "anjal.co.in" });
        }
        return tenant;
    }

    /// <summary>Send one message to <paramref name="rcpt"/>; returns the RCPT reply and, if accepted, the final reply.</summary>
    private async Task<(string Rcpt, string? Final)> SendAsync(string rcpt, string? operatorPostmaster)
    {
        var sink = new MailboxSink(this.store, new MaildirStore(this.root, "test"), this.log.Add)
        {
            ServerHostName = "mail.anjal.co.in",
            OperatorPostmaster = operatorPostmaster,
        };
        var options = new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "mail.anjal.co.in",
            Recipients = new ServerRecipientResolver(sink, routing: null),
        };
        using var server = new SmtpServer(options, sink, authenticator: null, enforceReject: false, smtpAuthenticator: null,
            localDomains: new ServerLocalDomainResolver(System.Array.Empty<string>(), this.store, this.store));
        _ = server.StartAsync(System.Threading.CancellationToken.None);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        using NetworkStream net = tcp.GetStream();
        using var reader = new System.IO.StreamReader(net, Encoding.ASCII);
        async Task<string> ReplyAsync()
        {
            string line;
            do
            {
                line = (await reader.ReadLineAsync())!;
            }
            while (line.Length > 3 && line[3] == '-');
            return line;
        }
        async Task<string> CmdAsync(string cmd)
        {
            await net.WriteAsync(Encoding.ASCII.GetBytes(cmd + "\r\n"));
            return await ReplyAsync();
        }
        await ReplyAsync();
        await CmdAsync("EHLO reporter.example.net");
        await CmdAsync("MAIL FROM:<reports@example.net>");
        string rcptReply = await CmdAsync($"RCPT TO:<{rcpt}>");
        string? final = null;
        if (rcptReply.StartsWith("250", System.StringComparison.Ordinal))
        {
            await CmdAsync("DATA");
            await net.WriteAsync(Encoding.ASCII.GetBytes("From: reports@example.net\r\nSubject: Report\r\n\r\nA report.\r\n.\r\n"));
            final = await ReplyAsync();
        }
        await CmdAsync("QUIT");
        return (rcptReply, final);
    }

    private async Task<string> DeliveredToAsync()
    {
        MessageRow m = this.store.MailboxMessages[^1];
        MailboxRow? mb = (await this.store.ListMailboxesAsync()).Single(x => x.Id == m.MailboxId);
        return mb.Address;
    }

    [Theory]
    [InlineData("Postmaster")]
    [InlineData("postmaster@mail.anjal.co.in")]
    [InlineData("ABUSE@mail.anjal.co.in")]
    public async Task ThisServersPostmaster_WithOrWithoutADomain_ReachesTheOperator(string rcpt)
    {
        await this.SeedAsync(null, "arun", "desk");
        (string r, string? final) = await this.SendAsync(rcpt, operatorPostmaster: "arun@anjal.co.in");
        Assert.StartsWith("250", r, System.StringComparison.Ordinal);
        Assert.StartsWith("250", final, System.StringComparison.Ordinal);
        Assert.Equal("arun@anjal.co.in", await this.DeliveredToAsync());
    }

    [Theory]
    [InlineData("postmaster@anjal.co.in")]
    [InlineData("abuse@anjal.co.in")]
    public async Task ADomainsPostmasterAndAbuse_ReachTheDesignatedMailbox(string rcpt)
    {
        await this.SeedAsync("desk@anjal.co.in", "arun", "desk");
        (string r, _) = await this.SendAsync(rcpt, operatorPostmaster: null);
        Assert.StartsWith("250", r, System.StringComparison.Ordinal);
        Assert.Equal("desk@anjal.co.in", await this.DeliveredToAsync());
    }

    [Fact]
    public async Task WithNoDesignation_TheFirstMailboxReceivesIt_AndTheLogSaysSo()
    {
        await this.SeedAsync(null, "desk", "arun");
        (string r, _) = await this.SendAsync("abuse@anjal.co.in", operatorPostmaster: null);
        Assert.StartsWith("250", r, System.StringComparison.Ordinal);
        Assert.Equal("arun@anjal.co.in", await this.DeliveredToAsync());
        Assert.Contains(this.log, l => l.Contains("abuse@anjal.co.in delivered to arun@anjal.co.in, the tenant's first mailbox", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARealMailboxCalledPostmaster_StillWins()
    {
        await this.SeedAsync("arun@anjal.co.in", "arun", "postmaster");
        await this.SendAsync("postmaster@anjal.co.in", operatorPostmaster: null);
        Assert.Equal("postmaster@anjal.co.in", await this.DeliveredToAsync());
    }

    [Theory]
    [InlineData("nobody@anjal.co.in", "550 5.1.1")]
    [InlineData("abuse@elsewhere.example", "550 5.7.1")]
    [InlineData("root@mail.anjal.co.in", "550 5.7.1")]
    public async Task NothingElseIsOpened(string rcpt, string expected)
    {
        await this.SeedAsync("arun@anjal.co.in", "arun");
        (string r, _) = await this.SendAsync(rcpt, operatorPostmaster: "arun@anjal.co.in");
        Assert.StartsWith(expected, r, System.StringComparison.Ordinal);
    }
}
