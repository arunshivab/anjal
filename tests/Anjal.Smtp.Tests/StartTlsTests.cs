using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Anjal.Store;

namespace Anjal.Smtp.Tests;

public class StartTlsTests
{
    private static readonly string[] SingleRecipient = new[] { "to@test" };

    /// <summary>
    /// Generate a self-signed certificate suitable for SslStream. Re-imports
    /// via PKCS#12 so the private key is bound consistently on Linux.
    /// </summary>
    private static X509Certificate2 GenerateSelfSigned(string cn)
    {
        using RSA rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(cn);
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
            false));
        X509Certificate2 cert = req.CreateSelfSigned(
            System.DateTimeOffset.UtcNow.AddMinutes(-5),
            System.DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    private sealed class RecordingSink : IMessageSink
    {
        public System.Collections.Generic.List<DeliveryContext> Received { get; } = new();

        public System.Threading.Tasks.Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, System.Threading.CancellationToken ct = default)
        {
            this.Received.Add(ctx);
            return System.Threading.Tasks.Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Accepted, ReplyText = "OK" });
        }
    }

    private static async System.Threading.Tasks.Task<(SmtpServer server, RecordingSink sink, System.Threading.CancellationTokenSource cts, System.Threading.Tasks.Task task)> StartServerAsync(
        X509Certificate2? cert,
        bool requireTls = false)
    {
        var sink = new RecordingSink();
        var server = new SmtpServer(new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "test.localhost",
            TlsCertificate = cert,
            RequireTlsForMail = requireTls,
        }, sink);
        var cts = new System.Threading.CancellationTokenSource();
        System.Threading.Tasks.Task t = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(80);
        return (server, sink, cts, t);
    }

    private static async System.Threading.Tasks.Task StopAsync(System.Threading.CancellationTokenSource cts, SmtpServer server, System.Threading.Tasks.Task t)
    {
        cts.Cancel();
        server.Dispose();
        try { await t; } catch (System.OperationCanceledException) { }
        cts.Dispose();
    }

    [Fact]
    public async System.Threading.Tasks.Task EhloWithCert_AdvertisesStartTls()
    {
        using X509Certificate2 cert = GenerateSelfSigned("test.localhost");
        var (server, _, cts, t) = await StartServerAsync(cert);
        try
        {
            using SmtpClientSession session = await SmtpClientSession.ConnectAsync(
                "127.0.0.1", server.BoundPort, System.TimeSpan.FromSeconds(5));
            SmtpReply ehlo = await session.EhloAsync("client.test");

            Assert.Equal(250, ehlo.Code);
            Assert.True(SmtpClientSession.EhloSupportsStartTls(ehlo));
        }
        finally
        {
            await StopAsync(cts, server, t);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task EhloWithoutCert_DoesNotAdvertiseStartTls()
    {
        var (server, _, cts, t) = await StartServerAsync(cert: null);
        try
        {
            using SmtpClientSession session = await SmtpClientSession.ConnectAsync(
                "127.0.0.1", server.BoundPort, System.TimeSpan.FromSeconds(5));
            SmtpReply ehlo = await session.EhloAsync("client.test");

            Assert.Equal(250, ehlo.Code);
            Assert.False(SmtpClientSession.EhloSupportsStartTls(ehlo));
        }
        finally
        {
            await StopAsync(cts, server, t);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task StartTls_UpgradesSession_EhloAfterReturns250()
    {
        using X509Certificate2 cert = GenerateSelfSigned("test.localhost");
        var (server, _, cts, t) = await StartServerAsync(cert);
        try
        {
            using SmtpClientSession session = await SmtpClientSession.ConnectAsync(
                "127.0.0.1", server.BoundPort, System.TimeSpan.FromSeconds(5));
            await session.EhloAsync("client.test");
            SmtpReply starttls = await session.StartTlsAsync("test.localhost", validateCertificate: false);

            Assert.Equal(220, starttls.Code);
            Assert.True(session.IsTls);

            SmtpReply ehloAfter = await session.EhloAsync("client.test");
            Assert.Equal(250, ehloAfter.Code);
        }
        finally
        {
            await StopAsync(cts, server, t);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task FullTransaction_OverTls_DeliversMessage()
    {
        using X509Certificate2 cert = GenerateSelfSigned("test.localhost");
        var (server, sink, cts, t) = await StartServerAsync(cert);
        try
        {
            using SmtpClientSession session = await SmtpClientSession.ConnectAsync(
                "127.0.0.1", server.BoundPort, System.TimeSpan.FromSeconds(5));
            await session.EhloAsync("client.test");
            await session.StartTlsAsync("test.localhost", validateCertificate: false);
            await session.EhloAsync("client.test");

            SmtpReply mail = await session.MailFromAsync("a@b");
            Assert.Equal(250, mail.Code);

            SmtpReply rcpt = await session.RcptToAsync("c@d");
            Assert.True(rcpt.Code == 250 || rcpt.Code == 251);

            byte[] body = System.Text.Encoding.UTF8.GetBytes("Subject: T\r\n\r\nBody.\r\n");
            SmtpReply data = await session.DataAsync(body);
            Assert.Equal(250, data.Code);

            await session.QuitAsync();
            await System.Threading.Tasks.Task.Delay(50);

            Assert.Single(sink.Received);
            Assert.Equal("a@b", sink.Received[0].EnvelopeFrom);
        }
        finally
        {
            await StopAsync(cts, server, t);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task StartTls_WithoutCert_Returns502()
    {
        var (server, _, cts, t) = await StartServerAsync(cert: null);
        try
        {
            using SmtpClientSession session = await SmtpClientSession.ConnectAsync(
                "127.0.0.1", server.BoundPort, System.TimeSpan.FromSeconds(5));
            await session.EhloAsync("client.test");
            SmtpReply starttls = await session.StartTlsAsync("test.localhost", validateCertificate: false);

            Assert.Equal(502, starttls.Code);
            Assert.False(session.IsTls);
        }
        finally
        {
            await StopAsync(cts, server, t);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task RequireTlsForMail_BlocksMailBeforeStartTls()
    {
        using X509Certificate2 cert = GenerateSelfSigned("test.localhost");
        var (server, _, cts, t) = await StartServerAsync(cert, requireTls: true);
        try
        {
            using SmtpClientSession session = await SmtpClientSession.ConnectAsync(
                "127.0.0.1", server.BoundPort, System.TimeSpan.FromSeconds(5));
            await session.EhloAsync("client.test");
            SmtpReply mail = await session.MailFromAsync("a@b");

            Assert.Equal(530, mail.Code);
        }
        finally
        {
            await StopAsync(cts, server, t);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task RequireTlsForMail_PermitsMailAfterStartTls()
    {
        using X509Certificate2 cert = GenerateSelfSigned("test.localhost");
        var (server, _, cts, t) = await StartServerAsync(cert, requireTls: true);
        try
        {
            using SmtpClientSession session = await SmtpClientSession.ConnectAsync(
                "127.0.0.1", server.BoundPort, System.TimeSpan.FromSeconds(5));
            await session.EhloAsync("client.test");
            await session.StartTlsAsync("test.localhost", validateCertificate: false);
            await session.EhloAsync("client.test");

            SmtpReply mail = await session.MailFromAsync("a@b");
            Assert.Equal(250, mail.Code);
        }
        finally
        {
            await StopAsync(cts, server, t);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task RelaySender_OpportunisticTls_UsesTlsWhenOffered()
    {
        using X509Certificate2 cert = GenerateSelfSigned("test.localhost");
        var (server, sink, cts, t) = await StartServerAsync(cert);
        try
        {
            var sender = new RelayMailSender(new RelayOptions
            {
                Host = "127.0.0.1",
                Port = server.BoundPort,
                ClientHostName = "client.test",
                ConnectTimeout = System.TimeSpan.FromSeconds(5),
                Tls = new TlsClientOptions
                {
                    DefaultMode = TlsMode.Opportunistic,
                    ValidateCertificate = false,
                },
            });

            SendResult result = await sender.SendAsync(new OutboundDelivery
            {
                EnvelopeFrom = "from@test",
                EnvelopeTo = SingleRecipient,
                RawBytes = System.Text.Encoding.UTF8.GetBytes("From: from@test\r\nTo: to@test\r\n\r\nBody.\r\n"),
            });

            Assert.Equal(SendOutcome.Sent, result.Outcome);
            Assert.Single(sink.Received);
        }
        finally
        {
            await StopAsync(cts, server, t);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task RelaySender_RequiredTls_FailsWhenServerLacksStartTls()
    {
        var (server, sink, cts, t) = await StartServerAsync(cert: null);
        try
        {
            var sender = new RelayMailSender(new RelayOptions
            {
                Host = "127.0.0.1",
                Port = server.BoundPort,
                ClientHostName = "client.test",
                ConnectTimeout = System.TimeSpan.FromSeconds(5),
                Tls = new TlsClientOptions { DefaultMode = TlsMode.Required },
            });

            SendResult result = await sender.SendAsync(new OutboundDelivery
            {
                EnvelopeFrom = "from@test",
                EnvelopeTo = SingleRecipient,
                RawBytes = System.Text.Encoding.UTF8.GetBytes("From: a\r\n\r\nBody.\r\n"),
            });

            Assert.Equal(SendOutcome.TransientFailure, result.Outcome);
            Assert.Empty(sink.Received);
        }
        finally
        {
            await StopAsync(cts, server, t);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task RelaySender_DisabledTls_SkipsStartTlsEvenWhenOffered()
    {
        using X509Certificate2 cert = GenerateSelfSigned("test.localhost");
        var (server, sink, cts, t) = await StartServerAsync(cert);
        try
        {
            var sender = new RelayMailSender(new RelayOptions
            {
                Host = "127.0.0.1",
                Port = server.BoundPort,
                ClientHostName = "client.test",
                ConnectTimeout = System.TimeSpan.FromSeconds(5),
                Tls = new TlsClientOptions { DefaultMode = TlsMode.Disabled },
            });

            SendResult result = await sender.SendAsync(new OutboundDelivery
            {
                EnvelopeFrom = "from@test",
                EnvelopeTo = SingleRecipient,
                RawBytes = System.Text.Encoding.UTF8.GetBytes("From: a\r\n\r\nBody.\r\n"),
            });

            Assert.Equal(SendOutcome.Sent, result.Outcome);
            Assert.Single(sink.Received);
        }
        finally
        {
            await StopAsync(cts, server, t);
        }
    }

    [Fact]
    public void EhloSupportsStartTls_DetectsCapability()
    {
        var withTls = new SmtpReply
        {
            Code = 250,
            Text = "test.localhost Hello\nSIZE 12345\n8BITMIME\nSTARTTLS\nHELP",
        };
        var withoutTls = new SmtpReply
        {
            Code = 250,
            Text = "test.localhost Hello\nSIZE 12345\n8BITMIME\nHELP",
        };

        Assert.True(SmtpClientSession.EhloSupportsStartTls(withTls));
        Assert.False(SmtpClientSession.EhloSupportsStartTls(withoutTls));
    }
}
