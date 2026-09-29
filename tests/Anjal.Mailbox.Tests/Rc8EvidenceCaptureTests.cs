using System.Net;
using System.Net.Sockets;
using System.Text;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Mailbox.Tests;

/// <summary>
/// v1.0.0-rc.8, SPEC-08 R-04 and R-08: every accepted incoming message is kept
/// exactly as received - through a real SMTP conversation - and nothing is
/// accepted when its original cannot be kept.
/// </summary>
public sealed class Rc8EvidenceCaptureTests : System.IDisposable
{
    private static readonly string[] OneRecipient = { "arun@anjal.co.in" };

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc8-evidence-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();

    public void Dispose()
    {
        if (System.IO.Directory.Exists(this.root))
        {
            foreach (string f in System.IO.Directory.GetFiles(this.root, "*", System.IO.SearchOption.AllDirectories))
            {
                System.IO.File.SetAttributes(f, System.IO.FileAttributes.Normal);
            }
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    // What the sender puts on the wire, and the message it means (dot-stuffing undone).
    // Line 2 ends with a bare LF, line 3 is a forged Authentication-Results
    // claiming our name, and the body has a dot-stuffed line.
    private const string Wire =
        "From: Doctor <doctor@example.org>\r\n" +
        "To: arun@anjal.co.in\n" +
        "Authentication-Results: test.localhost; spf=pass; dkim=pass\r\n" +
        "Subject: Lab report\r\n" +
        "Message-ID: <ev1@example.org>\r\n" +
        "\r\n" +
        "..starts with a dot\r\n" +
        "Body.\r\n";

    private static readonly byte[] AsReceived = Encoding.ASCII.GetBytes(Wire.Replace("\r\n..starts", "\r\n.starts", System.StringComparison.Ordinal));

    private async Task SeedAsync()
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in" });
    }

    private static async Task<string> SendAsync(int port)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
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
            byte[] b = Encoding.ASCII.GetBytes(cmd + "\r\n");
            await net.WriteAsync(b);
            return await ReplyAsync();
        }
        await ReplyAsync();
        await CmdAsync("EHLO sender.example.org");
        await CmdAsync("MAIL FROM:<doctor@example.org>");
        await CmdAsync("RCPT TO:<arun@anjal.co.in>");
        Assert.StartsWith("354", await CmdAsync("DATA"), System.StringComparison.Ordinal);
        await net.WriteAsync(Encoding.ASCII.GetBytes(Wire + ".\r\n"));
        string final = await ReplyAsync();
        await CmdAsync("QUIT");
        return final;
    }

    private async Task<(string Reply, SmtpServer Server)> RunAsync(string evidenceRoot, IMessageSink sink)
    {
        var recorder = new Anjal.Mailbox.EvidenceRecorder(this.store, new Anjal.Mailbox.EvidenceVault(evidenceRoot));
        var options = new SmtpServerOptions { BindAddress = IPAddress.Loopback, Port = 0, AdvertisedHostName = "test.localhost", Evidence = recorder };
        var server = new SmtpServer(options, sink, authenticator: null, enforceReject: false, smtpAuthenticator: null, localDomains: null);
        _ = server.StartAsync(System.Threading.CancellationToken.None);
        for (int i = 0; i < 50 && server.BoundPort == 0; i++)
        {
            await Task.Delay(20);
        }
        return (await SendAsync(server.BoundPort), server);
    }

    [Fact]
    public async Task IncomingMail_IsKeptExactlyAsReceived_AndTheMailboxCopyPointsToIt()
    {
        await this.SeedAsync();
        var sink = new MailboxSink(this.store, new MaildirStore(System.IO.Path.Combine(this.root, "mail"), "test"), _ => { });
        (string reply, SmtpServer server) = await this.RunAsync(System.IO.Path.Combine(this.root, "evidence"), sink);
        using (server)
        {
            Assert.StartsWith("250", reply, System.StringComparison.Ordinal);
        }

        MessageRow message = Assert.Single(this.store.MailboxMessages);
        Assert.NotNull(message.EvidenceId);
        EvidenceRow evidence = (await this.store.GetEvidenceAsync(message.EvidenceId!.Value))!;
        byte[] kept = await System.IO.File.ReadAllBytesAsync(System.IO.Path.Combine(this.root, "evidence", evidence.Path));

        // Exactly the bytes the sender meant: bare LF kept, forged line kept, dot-stuffing undone.
        Assert.Equal(AsReceived, kept);
        Assert.Equal(Anjal.Mailbox.EvidenceVault.Sha256Hex(AsReceived), evidence.Sha256);
        Assert.Equal("accepted", evidence.Outcome);
        Assert.Equal("doctor@example.org", evidence.EnvelopeFrom);
        Assert.Equal(OneRecipient, evidence.EnvelopeTo);
        Assert.Equal("sender.example.org", evidence.ClientHostName);
        Assert.Equal("127.0.0.1", evidence.RemoteAddress);
        Assert.Equal(1095, evidence.RetentionDays);

        // The working copy still gets its repairs; the evidence does not.
        string working = await System.IO.File.ReadAllTextAsync(System.IO.Directory.GetFiles(System.IO.Path.Combine(this.root, "mail"), "*", System.IO.SearchOption.AllDirectories).Single(f => f.Contains("/new/", System.StringComparison.Ordinal) || f.Contains("\\new\\", System.StringComparison.Ordinal)));
        Assert.DoesNotContain("Authentication-Results: test.localhost; spf=pass", working, System.StringComparison.Ordinal);
        // Final delivery puts Return-Path first since v1.0.0-rc.9 (DEF-069); the evidence above is unchanged.
        Assert.StartsWith("Return-Path: <doctor@example.org>\r\nReceived: from sender.example.org", working, System.StringComparison.Ordinal);
        Assert.DoesNotContain("in.co\n", working.Replace("\r\n", string.Empty, System.StringComparison.Ordinal), System.StringComparison.Ordinal);
        if (!System.OperatingSystem.IsWindows())
        {
            System.IO.UnixFileMode mode = System.IO.File.GetUnixFileMode(System.IO.Path.Combine(this.root, "evidence", evidence.Path));
            Assert.Equal(System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.GroupRead, mode);
        }
    }

    [Fact]
    public async Task WhenTheOriginalCannotBeKept_TheMessageIsDeferred_AndNothingIsDelivered()
    {
        await this.SeedAsync();
        System.IO.Directory.CreateDirectory(this.root);
        string blocker = System.IO.Path.Combine(this.root, "not-a-directory");
        await System.IO.File.WriteAllTextAsync(blocker, "a file where the evidence folder should be");
        var sink = new MailboxSink(this.store, new MaildirStore(System.IO.Path.Combine(this.root, "mail"), "test"), _ => { });
        (string reply, SmtpServer server) = await this.RunAsync(blocker, sink);
        using (server)
        {
            Assert.StartsWith("451 4.3.0", reply, System.StringComparison.Ordinal);
        }
        Assert.Empty(this.store.MailboxMessages);
    }
}
