using System.Net;
using System.Net.Sockets;
using System.Text;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Mailbox.Tests;

/// <summary>
/// v1.0.0-rc.9 (DEF-069, D-55; RFC 5321 4.4): final delivery adds a Return-Path
/// with the envelope sender, removing any the sender included - in the working
/// copy only; the evidence stays exactly as received.
/// </summary>
public sealed class Rc9ReturnPathTests : System.IDisposable
{
    private static readonly string[] Arun = { "arun@anjal.co.in" };
    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc9-rp-" + System.Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void ReturnPath_IsFirst_ASendersOwnIsRemovedEvenWhenFolded_AndTheBodyIsUntouched()
    {
        byte[] raw = Encoding.ASCII.GetBytes(
            "Return-Path: <forged@elsewhere.example>\r\n" +
            "From: doctor@example.org\r\n" +
            "return-path:\r\n <another@forged.example>\r\n" +
            "Subject: t\r\n\r\n" +
            "Return-Path: in the body stays\r\n");
        string filed = Encoding.ASCII.GetString(MailboxSink.WithReturnPath(raw, "doctor@example.org"));
        Assert.Equal(
            "Return-Path: <doctor@example.org>\r\n" +
            "From: doctor@example.org\r\n" +
            "Subject: t\r\n\r\n" +
            "Return-Path: in the body stays\r\n", filed);
    }

    [Fact]
    public void ABounce_GetsAnEmptyReturnPath()
    {
        string filed = Encoding.ASCII.GetString(MailboxSink.WithReturnPath(Encoding.ASCII.GetBytes("Subject: x\r\n\r\nb\r\n"), string.Empty));
        Assert.StartsWith("Return-Path: <>\r\n", filed, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThroughSmtp_TheWorkingCopyHasReturnPath_AndTheEvidenceIsExactlyAsReceived()
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in" });
        var sink = new MailboxSink(this.store, new MaildirStore(System.IO.Path.Combine(this.root, "mail"), "test"), _ => { });
        var options = new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "mail.anjal.co.in",
            Evidence = new EvidenceRecorder(this.store, new EvidenceVault(System.IO.Path.Combine(this.root, "evidence"))),
        };
        using var server = new SmtpServer(options, sink);
        _ = server.StartAsync(System.Threading.CancellationToken.None);
        const string Message = "Return-Path: <forged@elsewhere.example>\r\nFrom: doctor@example.org\r\nSubject: t\r\n\r\nbody\r\n";
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, server.BoundPort);
            using NetworkStream net = tcp.GetStream();
            using var r = new System.IO.StreamReader(net, Encoding.ASCII);
            async Task<string> Reply()
            {
                string l;
                do
                {
                    l = (await r.ReadLineAsync())!;
                }
                while (l.Length > 3 && l[3] == '-');
                return l;
            }
            await Reply();
            foreach (string c in new[] { "EHLO mx.example.org", "MAIL FROM:<doctor@example.org>", "RCPT TO:<arun@anjal.co.in>", "DATA" })
            {
                await net.WriteAsync(Encoding.ASCII.GetBytes(c + "\r\n"));
                await Reply();
            }
            await net.WriteAsync(Encoding.ASCII.GetBytes(Message + ".\r\n"));
            Assert.StartsWith("250", await Reply(), System.StringComparison.Ordinal);
        }

        string working = await System.IO.File.ReadAllTextAsync(System.IO.Directory.GetFiles(System.IO.Path.Combine(this.root, "mail"), "*", System.IO.SearchOption.AllDirectories).Single());
        Assert.StartsWith("Return-Path: <doctor@example.org>\r\nReceived: from mx.example.org", working, System.StringComparison.Ordinal);
        Assert.DoesNotContain("forged@elsewhere.example", working, System.StringComparison.Ordinal);

        EvidenceRow evidence = (await this.store.GetEvidenceAsync(this.store.MailboxMessages.Single().EvidenceId!.Value))!;
        byte[] kept = await System.IO.File.ReadAllBytesAsync(System.IO.Path.Combine(this.root, "evidence", evidence.Path));
        Assert.Equal(Encoding.ASCII.GetBytes(Message), kept);
    }

    [Fact]
    public void Reconstruction_RemovesThisServersReturnPath_WithItsOtherLines()
    {
        const string Original = "From: doctor@example.org\r\nSubject: t\r\n\r\nbody\r\n";
        string stored = "Return-Path: <doctor@example.org>\r\nReceived: from mx.example.org ([203.0.113.9])\r\n\tby mail.anjal.co.in with ESMTPS id 0123456789abcdef (TLSv1.3 X);\r\n\tMon, 5 Oct 2026 10:00:00 +0000\r\n" + Original;
        Assert.Equal(Encoding.ASCII.GetBytes(Original), EvidenceMaintenance.RemoveOwnAdditions(Encoding.ASCII.GetBytes(stored), "mail.anjal.co.in"));
    }
}
