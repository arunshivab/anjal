using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Anjal.Smtp.Tests;

public class TlsCertificateSourceTests
{
    private sealed class AcceptSink : IMessageSink
    {
        public System.Threading.Tasks.Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Accepted });
    }

    private static X509Certificate2 SelfSigned(string cn)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(cn);
        req.CertificateExtensions.Add(san.Build());
        using X509Certificate2 cert = req.CreateSelfSigned(System.DateTimeOffset.UtcNow.AddMinutes(-5), System.DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>Open a session, EHLO, STARTTLS, and return the certificate the server presented (or null if STARTTLS was not offered).</summary>
    private static async System.Threading.Tasks.Task<string?> HandshakeAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream net = client.GetStream();
        var reader = new StreamReader(net, Encoding.ASCII, false, 1024, leaveOpen: true);
        var writer = new StreamWriter(net, Encoding.ASCII, 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

        await reader.ReadLineAsync();
        await writer.WriteLineAsync("EHLO c");
        bool offered = false;
        string? line;
        do
        {
            line = await reader.ReadLineAsync();
            if (line is not null && line.Contains("STARTTLS", System.StringComparison.Ordinal))
            {
                offered = true;
            }
        }
        while (line is not null && line.Length >= 4 && line[3] == '-');
        if (!offered)
        {
            await writer.WriteLineAsync("QUIT");
            return null;
        }

        await writer.WriteLineAsync("STARTTLS");
        string? reply = await reader.ReadLineAsync();
        Assert.StartsWith("220", reply, System.StringComparison.Ordinal);

        string? seen = null;
#pragma warning disable CA5359 // Test records which self-signed certificate the server presented.
        using var ssl = new SslStream(net, leaveInnerStreamOpen: false, (_, cert, _, _) =>
        {
            seen = cert?.GetCertHashString();
            return true;
        });
#pragma warning restore CA5359
        await ssl.AuthenticateAsClientAsync("test.localhost");
        var w2 = new StreamWriter(ssl, Encoding.ASCII, 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        await w2.WriteLineAsync("QUIT");
        return seen;
    }

    [Fact]
    public async System.Threading.Tasks.Task NewSessions_UseTheCertificateTheSourceReturnsNow()
    {
        using X509Certificate2 a = SelfSigned("test.localhost");
        using X509Certificate2 b = SelfSigned("test.localhost");
        X509Certificate2? current = null;

        var server = new SmtpServer(new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "test.localhost",
            TlsCertificateSource = () => current,
        }, new AcceptSink());
        using var cts = new System.Threading.CancellationTokenSource();
        System.Threading.Tasks.Task run = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(80);
        try
        {
            Assert.Null(await HandshakeAsync(server.BoundPort));   // no cert yet: STARTTLS not advertised

            current = a;
            Assert.Equal(a.GetCertHashString(), await HandshakeAsync(server.BoundPort));

            current = b;                                           // "renewal": no restart
            Assert.Equal(b.GetCertHashString(), await HandshakeAsync(server.BoundPort));
        }
        finally
        {
            cts.Cancel();
            try { await run; }
            catch (System.OperationCanceledException) { }
            server.Dispose();
        }
    }

    [Fact]
    public void CurrentTlsCertificate_PrefersSourceOverStatic()
    {
        using X509Certificate2 s = SelfSigned("static");
        using X509Certificate2 d = SelfSigned("dynamic");
        Assert.Null(new SmtpServerOptions().CurrentTlsCertificate());
        Assert.Same(s, new SmtpServerOptions { TlsCertificate = s }.CurrentTlsCertificate());
        Assert.Same(d, new SmtpServerOptions { TlsCertificate = s, TlsCertificateSource = () => d }.CurrentTlsCertificate());
    }
}
