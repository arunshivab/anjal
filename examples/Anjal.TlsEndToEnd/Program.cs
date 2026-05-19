using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Anjal.Smtp;
using Anjal.Store;

namespace Anjal.Examples.TlsEndToEnd;

/// <summary>
/// Demonstrates STARTTLS end-to-end on localhost. No external cert or CA
/// needed - we generate a self-signed certificate in-process.
///
///   1. Generate an RSA-2048 self-signed cert for "anjal.test".
///   2. Start an Anjal SMTP server with the cert attached.
///   3. Drive an outbound queue + RelayMailSender at it, using
///      <see cref="TlsMode.Required"/> so the demo asserts TLS was actually used.
///   4. Print the cert details, handshake outcome, and what arrived at the server.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        // 1. Self-signed cert.
        X509Certificate2 cert = GenerateSelfSigned("anjal.test");
        Console.WriteLine("=== Self-signed cert ===");
        Console.WriteLine($"  subject:  {cert.Subject}");
        Console.WriteLine($"  thumbprint: {cert.Thumbprint}");
        Console.WriteLine($"  valid:    {cert.NotBefore:yyyy-MM-dd HH:mm:ss} → {cert.NotAfter:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();

        // 2. Anjal SMTP server with TLS.
        var sink = new RecordingSink();
        using var serverCts = new CancellationTokenSource();
        using var server = new SmtpServer(new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "anjal.test",
            TlsCertificate = cert,
        }, sink);
        Task serverTask = server.StartAsync(serverCts.Token);
        await Task.Delay(150);
        Console.WriteLine($"Anjal SMTP listening with STARTTLS on 127.0.0.1:{server.BoundPort}");
        Console.WriteLine();

        // 3. Outbound store + worker + RelayMailSender with TlsMode.Required.
        var store = new InMemoryMessageStore();
        var sender = new RelayMailSender(new RelayOptions
        {
            Host = "127.0.0.1",
            Port = server.BoundPort,
            ClientHostName = "client.test",
            ConnectTimeout = TimeSpan.FromSeconds(5),
            Tls = new TlsClientOptions
            {
                DefaultMode = TlsMode.Required,    // Fail loudly if TLS isn't used.
                ValidateCertificate = false,        // Self-signed cert in this demo.
            },
        });

        OutboundMessage queued = await store.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = "noreply@anjal.test",
            EnvelopeTo = "patient@gmail.com",
            RawBytes = System.Text.Encoding.UTF8.GetBytes(
                "From: noreply@anjal.test\r\n" +
                "To: patient@gmail.com\r\n" +
                "Subject: Encrypted password reset\r\n" +
                "\r\n" +
                "Your reset code is 482919.\r\n"),
        });
        Console.WriteLine($"Enqueued outbound id={queued.Id}, status={queued.Status}");

        // Drain once.
        IReadOnlyList<OutboundMessage> batch = await store.LeaseOutboundBatchAsync(10, DateTimeOffset.UtcNow);
        Console.WriteLine($"Worker leased {batch.Count} message(s).");
        foreach (OutboundMessage m in batch)
        {
            SendResult res = await sender.SendAsync(new OutboundDelivery
            {
                EnvelopeFrom = m.EnvelopeFrom,
                EnvelopeTo = new[] { m.EnvelopeTo },
                RawBytes = m.RawBytes,
            });
            OutboundStatus newStatus = res.Outcome switch
            {
                SendOutcome.Sent => OutboundStatus.Sent,
                SendOutcome.PermanentFailure => OutboundStatus.Failed,
                _ => OutboundStatus.Pending,
            };
            await store.MarkOutboundResultAsync(m.Id, newStatus, DateTimeOffset.UtcNow, res.Message);
            Console.WriteLine($"  {newStatus} {m.Id} ({res.Message})");
        }

        Console.WriteLine();
        Console.WriteLine("=== Server received ===");
        Console.WriteLine($"  count:        {sink.Received.Count}");
        if (sink.Received.Count > 0)
        {
            DeliveryContext got = sink.Received[0];
            Console.WriteLine($"  envelope_from: {got.EnvelopeFrom}");
            Console.WriteLine($"  envelope_to:   {string.Join(", ", got.EnvelopeTo)}");
            Console.WriteLine($"  bytes:         {got.RawBytes.Length}");
            Console.WriteLine();
            Console.WriteLine("  preview:");
            foreach (string line in System.Text.Encoding.UTF8.GetString(got.RawBytes).Split("\r\n"))
            {
                Console.WriteLine($"    {line}");
            }
        }

        OutboundMessage final = store.Outbound[0];
        Console.WriteLine();
        Console.WriteLine("=== Outbound row after drain ===");
        Console.WriteLine($"  status:   {final.Status}");
        Console.WriteLine($"  attempts: {final.Attempts}");

        serverCts.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }

        Console.WriteLine();
        Console.WriteLine(final.Status == OutboundStatus.Sent
            ? "TLS end-to-end demo: SUCCESS - message delivered over STARTTLS."
            : "TLS end-to-end demo: FAILED - message did not deliver.");
        return final.Status == OutboundStatus.Sent ? 0 : 1;
    }

    /// <summary>
    /// Generate a self-signed RSA-2048 cert suitable for SslStream. The
    /// PKCS#12 round-trip ensures the private key is correctly bound on
    /// all platforms (some Linux builds otherwise lose the key after
    /// CreateSelfSigned).
    /// </summary>
    private static X509Certificate2 GenerateSelfSigned(string commonName)
    {
        using RSA rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={commonName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(commonName);
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },  // serverAuth
            false));

        X509Certificate2 cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(30));

        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }
}

internal sealed class RecordingSink : IMessageSink
{
    public List<DeliveryContext> Received { get; } = new();

    public Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, CancellationToken ct = default)
    {
        this.Received.Add(ctx);
        return Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Accepted, ReplyText = "OK" });
    }
}
