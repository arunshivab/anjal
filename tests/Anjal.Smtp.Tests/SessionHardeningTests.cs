using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Anjal.Smtp.Tests;

public sealed class SessionHardeningTests
{
    private sealed class RecordingSink : IMessageSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<DeliveryContext> Received { get; } = new();

        public System.Threading.Tasks.Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, System.Threading.CancellationToken ct = default)
        {
            this.Received.Enqueue(ctx);
            return System.Threading.Tasks.Task.FromResult(new DeliveryResult { Outcome = DeliveryOutcome.Accepted, ReplyText = "OK" });
        }
    }

    private sealed class CountingAuthenticator : ISmtpAuthenticator
    {
        public int Calls;

        public bool Throw { get; init; }

        public System.Threading.Tasks.Task<AuthenticatedUser?> AuthenticateAsync(string username, string password, System.Threading.CancellationToken ct = default)
        {
            System.Threading.Interlocked.Increment(ref this.Calls);
            if (this.Throw)
            {
                throw new System.InvalidOperationException("database is down");
            }
            return System.Threading.Tasks.Task.FromResult<AuthenticatedUser?>(null);
        }
    }

    /// <summary>A raw SMTP conversation: bytes out exactly as given, lines in.</summary>
    private sealed class Wire : System.IDisposable
    {
        private readonly TcpClient client = new();
        private Stream stream = Stream.Null;
        private readonly List<byte> pending = new();

        public static async System.Threading.Tasks.Task<Wire> OpenAsync(int port, bool readBanner = true)
        {
            var w = new Wire();
            await w.client.ConnectAsync(IPAddress.Loopback, port);
            w.stream = w.client.GetStream();
            if (readBanner)
            {
                await w.LineAsync();
            }
            return w;
        }

        public Stream Stream => this.stream;

        public void UseStream(Stream s) => this.stream = s;

        public System.Threading.Tasks.Task SendAsync(string text) => this.stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

        /// <summary>Next reply line, or null when the server closed the connection.</summary>
        public async System.Threading.Tasks.Task<string?> LineAsync(int timeoutMs = 10000)
        {
            using var cts = new System.Threading.CancellationTokenSource(timeoutMs);
            byte[] one = new byte[1];
            while (true)
            {
                int n;
                try
                {
                    n = await this.stream.ReadAsync(one, cts.Token);
                }
                catch (System.IO.IOException)
                {
                    n = 0;
                }
                if (n == 0)
                {
                    return this.pending.Count == 0 ? null : Encoding.ASCII.GetString(this.pending.ToArray());
                }
                if (one[0] == '\n')
                {
                    string line = Encoding.ASCII.GetString(this.pending.ToArray()).TrimEnd('\r');
                    this.pending.Clear();
                    return line;
                }
                this.pending.Add(one[0]);
            }
        }

        /// <summary>Read a (possibly multi-line) reply; returns the final line.</summary>
        public async System.Threading.Tasks.Task<string?> ReplyAsync()
        {
            string? line;
            do
            {
                line = await this.LineAsync();
            }
            while (line is not null && line.Length > 3 && line[3] == '-');
            return line;
        }

        public void Dispose()
        {
            this.stream.Dispose();
            this.client.Dispose();
        }
    }

    private static async System.Threading.Tasks.Task<(SmtpServer Server, RecordingSink Sink, System.Threading.CancellationTokenSource Cts)> StartAsync(
        SmtpServerOptions options, ISmtpAuthenticator? auth = null)
    {
        var sink = new RecordingSink();
        var server = new SmtpServer(options, sink, authenticator: null, enforceReject: false, smtpAuthenticator: auth, localDomains: null);
        var cts = new System.Threading.CancellationTokenSource();
        _ = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(50);
        return (server, sink, cts);
    }

    private static SmtpServerOptions Options(System.Action<SmtpServerOptionsBuilder>? tweak = null)
    {
        var b = new SmtpServerOptionsBuilder();
        tweak?.Invoke(b);
        return b.Build();
    }

    /// <summary>Mutable stand-in so each test can set just what it needs.</summary>
    private sealed class SmtpServerOptionsBuilder
    {
        public System.TimeSpan Idle { get; set; } = System.TimeSpan.FromSeconds(30);
        public int PerAddress { get; set; } = 10;
        public int MaxBytes { get; set; } = 1024 * 1024;
        public SmtpServerRole Role { get; set; } = SmtpServerRole.Mta;
        public AuthFailureLimiter? Limiter { get; set; }
        public X509Certificate2? Cert { get; set; }
        public bool Implicit { get; set; }

        public SmtpServerOptions Build() => new()
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "test.localhost",
            CommandTimeout = this.Idle,
            MaxSessionsPerAddress = this.PerAddress,
            MaxMessageBytes = this.MaxBytes,
            Role = this.Role,
            AllowPlaintextAuth = true,
            AuthFailures = this.Limiter,
            TlsCertificate = this.Cert,
            ImplicitTls = this.Implicit,
        };
    }

    /// <summary>
    /// Trusts exactly the test certificate, by thumbprint - the client side
    /// still validates, it just knows the one self-signed key it expects.
    /// </summary>
    private static RemoteCertificateValidationCallback Expect(X509Certificate2 cert) =>
        (_, presented, _, _) => presented is not null &&
            string.Equals(presented.GetCertHashString(), cert.GetCertHashString(), System.StringComparison.Ordinal);

    private static X509Certificate2 SelfSigned()
    {
        using RSA rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=test.localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("test.localhost");
        req.CertificateExtensions.Add(san.Build());
        X509Certificate2 cert = req.CreateSelfSigned(System.DateTimeOffset.UtcNow.AddMinutes(-5), System.DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    private static async System.Threading.Tasks.Task<string?> DeliverAsync(Wire w, string body)
    {
        await w.SendAsync("EHLO client.test\r\n");
        await w.ReplyAsync();
        await w.SendAsync("MAIL FROM:<a@sender.test>\r\n");
        await w.ReplyAsync();
        await w.SendAsync("RCPT TO:<b@dest.test>\r\n");
        await w.ReplyAsync();
        await w.SendAsync("DATA\r\n");
        await w.ReplyAsync();
        await w.SendAsync(body);
        return await w.ReplyAsync();
    }

    [Fact]
    public async System.Threading.Tasks.Task AnIdleClient_IsClosedWith421()
    {
        (SmtpServer server, _, System.Threading.CancellationTokenSource cts) = await StartAsync(Options(b => b.Idle = System.TimeSpan.FromMilliseconds(400)));
        using (server)
        using (cts)
        {
            using Wire w = await Wire.OpenAsync(server.BoundPort);
            string? line = await w.LineAsync(5000);
            Assert.StartsWith("421 4.4.2", line, System.StringComparison.Ordinal);
            Assert.Null(await w.LineAsync(2000));
            cts.Cancel();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task ConnectionsBeyondThePerAddressCap_AreRefused()
    {
        (SmtpServer server, _, System.Threading.CancellationTokenSource cts) = await StartAsync(Options(b => b.PerAddress = 1));
        using (server)
        using (cts)
        {
            using Wire first = await Wire.OpenAsync(server.BoundPort);
            using Wire second = await Wire.OpenAsync(server.BoundPort, readBanner: false);
            Assert.StartsWith("421 4.7.0", await second.LineAsync(), System.StringComparison.Ordinal);

            // The slot comes back when the first session ends.
            await first.SendAsync("QUIT\r\n");
            await first.LineAsync();
            await System.Threading.Tasks.Task.Delay(200);
            using Wire third = await Wire.OpenAsync(server.BoundPort, readBanner: false);
            Assert.StartsWith("220", await third.LineAsync(), System.StringComparison.Ordinal);
            cts.Cancel();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task AnOverlongCommand_Gets500_AndTheSessionStaysInStep()
    {
        (SmtpServer server, _, System.Threading.CancellationTokenSource cts) = await StartAsync(Options());
        using (server)
        using (cts)
        {
            using Wire w = await Wire.OpenAsync(server.BoundPort);
            // The tail of the long line must not be read as a command.
            await w.SendAsync("NOOP " + new string('A', 3000) + "QUIT\r\n");
            Assert.StartsWith("500 5.5.2", await w.LineAsync(), System.StringComparison.Ordinal);
            await w.SendAsync("NOOP\r\n");
            Assert.StartsWith("250", await w.LineAsync(), System.StringComparison.Ordinal);
            cts.Cancel();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task AnOversizedMessage_Gets552_AndItsBodyIsNeverReadAsCommands()
    {
        (SmtpServer server, RecordingSink sink, System.Threading.CancellationTokenSource cts) = await StartAsync(Options(b => b.MaxBytes = 1000));
        using (server)
        using (cts)
        {
            using Wire w = await Wire.OpenAsync(server.BoundPort);
            string body = string.Concat(Enumerable.Repeat("line of filler text\r\n", 200)) + "RSET\r\nQUIT\r\n.\r\n";
            Assert.StartsWith("552", await DeliverAsync(w, body), System.StringComparison.Ordinal);
            await w.SendAsync("NOOP\r\n");
            Assert.StartsWith("250", await w.LineAsync(), System.StringComparison.Ordinal);
            Assert.Empty(sink.Received);
            cts.Cancel();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task ADeclaredSizeOverTheLimit_IsRefusedAtMailFrom()
    {
        (SmtpServer server, _, System.Threading.CancellationTokenSource cts) = await StartAsync(Options(b => b.MaxBytes = 1000));
        using (server)
        using (cts)
        {
            using Wire w = await Wire.OpenAsync(server.BoundPort);
            await w.SendAsync("EHLO c\r\n");
            await w.ReplyAsync();
            await w.SendAsync("MAIL FROM:<a@sender.test> SIZE=5000\r\n");
            Assert.StartsWith("552 5.3.4", await w.LineAsync(), System.StringComparison.Ordinal);
            await w.SendAsync("MAIL FROM:<a@sender.test> SIZE=500\r\n");
            Assert.StartsWith("250", await w.LineAsync(), System.StringComparison.Ordinal);
            cts.Cancel();
        }
    }

    [Theory]
    [InlineData("Hello\n.\nMAIL FROM:<x@y>\r\n.\r\n")]
    [InlineData("Hello\r\n.\nMAIL FROM:<x@y>\r\n.\r\n")]
    [InlineData("Hello\n.\r\nMAIL FROM:<x@y>\r\n.\r\n")]
    [InlineData("Hello\r.\rMAIL FROM:<x@y>\r\n.\r\n")]
    public async System.Threading.Tasks.Task ALoneDotWithBareLineEndings_IsRefusedAndClosed(string body)
    {
        (SmtpServer server, RecordingSink sink, System.Threading.CancellationTokenSource cts) = await StartAsync(Options());
        using (server)
        using (cts)
        {
            using Wire w = await Wire.OpenAsync(server.BoundPort);
            Assert.StartsWith("554 5.5.2", await DeliverAsync(w, body), System.StringComparison.Ordinal);
            Assert.Null(await w.LineAsync(3000));
            Assert.Empty(sink.Received);
            cts.Cancel();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task ADotStuffedLine_IsStillAccepted()
    {
        (SmtpServer server, RecordingSink sink, System.Threading.CancellationTokenSource cts) = await StartAsync(Options());
        using (server)
        using (cts)
        {
            using Wire w = await Wire.OpenAsync(server.BoundPort);
            Assert.StartsWith("250", await DeliverAsync(w, "Subject: s\r\n\r\nbefore\r\n..\r\nafter\r\n.\r\n"), System.StringComparison.Ordinal);
            Assert.True(sink.Received.TryDequeue(out DeliveryContext? got));
            Assert.Contains("before\r\n.\r\nafter", Encoding.ASCII.GetString(got!.RawBytes), System.StringComparison.Ordinal);
            cts.Cancel();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task BareLineEndings_AreStoredAsCrlf_AndCrCrLfEndsTheLine()
    {
        (SmtpServer server, RecordingSink sink, System.Threading.CancellationTokenSource cts) = await StartAsync(Options());
        using (server)
        using (cts)
        {
            using Wire w = await Wire.OpenAsync(server.BoundPort);
            Assert.StartsWith("250", await DeliverAsync(w, "Subject: s\r\n\r\none\ntwo\r\r\nthree\r\n.\r\n"), System.StringComparison.Ordinal);
            Assert.True(sink.Received.TryDequeue(out DeliveryContext? got));
            string stored = Encoding.ASCII.GetString(got!.RawBytes);
            Assert.Contains("one\r\ntwo\r\n\r\nthree\r\n", stored, System.StringComparison.Ordinal);
            Assert.DoesNotContain("\n\n", stored.Replace("\r\n", "\u0001", System.StringComparison.Ordinal), System.StringComparison.Ordinal);
            cts.Cancel();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task EveryInboundMessage_CarriesAReceivedTraceHeader()
    {
        (SmtpServer server, RecordingSink sink, System.Threading.CancellationTokenSource cts) = await StartAsync(Options());
        using (server)
        using (cts)
        {
            using Wire w = await Wire.OpenAsync(server.BoundPort);
            await DeliverAsync(w, "Subject: s\r\n\r\nbody\r\n.\r\n");
            Assert.True(sink.Received.TryDequeue(out DeliveryContext? got));
            string stored = Encoding.ASCII.GetString(got!.RawBytes);
            Assert.StartsWith("Received: from client.test ([127.0.0.1])", stored, System.StringComparison.Ordinal);
            Assert.Contains("by test.localhost with ESMTP id ", stored, System.StringComparison.Ordinal);
            cts.Cancel();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task ThreeFailedLogins_CloseTheSession_AndTheAddressIsThenRefusedBeforeAnyCheck()
    {
        var auth = new CountingAuthenticator();
        var limiter = new AuthFailureLimiter(3, System.TimeSpan.FromMinutes(15));
        (SmtpServer server, _, System.Threading.CancellationTokenSource cts) = await StartAsync(
            Options(b => { b.Role = SmtpServerRole.Submission; b.Limiter = limiter; }), auth);
        using (server)
        using (cts)
        {
            string bad = System.Convert.ToBase64String(Encoding.ASCII.GetBytes("\0u@x.test\0wrong"));
            using (Wire w = await Wire.OpenAsync(server.BoundPort))
            {
                await w.SendAsync("EHLO c\r\n");
                await w.ReplyAsync();
                for (int i = 0; i < 2; i++)
                {
                    await w.SendAsync($"AUTH PLAIN {bad}\r\n");
                    Assert.StartsWith("535", await w.LineAsync(), System.StringComparison.Ordinal);
                }
                await w.SendAsync($"AUTH PLAIN {bad}\r\n");
                Assert.StartsWith("421 4.7.0", await w.LineAsync(), System.StringComparison.Ordinal);
            }
            Assert.Equal(3, auth.Calls);

            using (Wire w = await Wire.OpenAsync(server.BoundPort))
            {
                await w.SendAsync("EHLO c\r\n");
                await w.ReplyAsync();
                await w.SendAsync($"AUTH PLAIN {bad}\r\n");
                Assert.StartsWith("454 4.7.0", await w.LineAsync(), System.StringComparison.Ordinal);
            }
            Assert.Equal(3, auth.Calls); // refused without asking the authenticator
            cts.Cancel();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task AnAuthenticatorFailure_IsATemporaryError_NotAWrongPassword()
    {
        var auth = new CountingAuthenticator { Throw = true };
        (SmtpServer server, _, System.Threading.CancellationTokenSource cts) = await StartAsync(Options(b => b.Role = SmtpServerRole.Submission), auth);
        using (server)
        using (cts)
        {
            using Wire w = await Wire.OpenAsync(server.BoundPort);
            await w.SendAsync("EHLO c\r\n");
            await w.ReplyAsync();
            await w.SendAsync("AUTH PLAIN " + System.Convert.ToBase64String(Encoding.ASCII.GetBytes("\0u@x.test\0pw")) + "\r\n");
            Assert.StartsWith("454 4.7.0", await w.LineAsync(), System.StringComparison.Ordinal);
            cts.Cancel();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task PlaintextPipelinedAfterStartTls_IsDiscarded_NotRunInsideTls()
    {
        using X509Certificate2 cert = SelfSigned();
        (SmtpServer server, _, System.Threading.CancellationTokenSource cts) = await StartAsync(Options(b => b.Cert = cert));
        using (server)
        using (cts)
        {
            using Wire w = await Wire.OpenAsync(server.BoundPort);
            await w.SendAsync("EHLO c\r\n");
            await w.ReplyAsync();

            // A man in the middle appends a command after STARTTLS in the same packet.
            await w.SendAsync("STARTTLS\r\nNOOP\r\n");
            Assert.StartsWith("220", await w.LineAsync(), System.StringComparison.Ordinal);

            var ssl = new SslStream(w.Stream, leaveInnerStreamOpen: true, Expect(cert));
            await ssl.AuthenticateAsClientAsync("test.localhost");
            w.UseStream(ssl);
            await w.SendAsync("EHLO c\r\n");

            // Had the injected NOOP run, its "250 OK" would arrive first.
            string? first = await w.LineAsync();
            Assert.StartsWith("250-test.localhost", first, System.StringComparison.Ordinal);
            cts.Cancel();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task ImplicitTls_HandshakesBeforeTheBanner_AndSaysNothingInPlaintext()
    {
        using X509Certificate2 cert = SelfSigned();
        (SmtpServer server, _, System.Threading.CancellationTokenSource cts) = await StartAsync(
            Options(b => { b.Cert = cert; b.Implicit = true; b.Role = SmtpServerRole.Submission; b.Idle = System.TimeSpan.FromSeconds(2); }));
        using (server)
        using (cts)
        {
            using (Wire tls = await Wire.OpenAsync(server.BoundPort, readBanner: false))
            {
                var ssl = new SslStream(tls.Stream, leaveInnerStreamOpen: true, Expect(cert));
                await ssl.AuthenticateAsClientAsync("test.localhost");
                tls.UseStream(ssl);
                Assert.StartsWith("220", await tls.LineAsync(), System.StringComparison.Ordinal);
                await tls.SendAsync("EHLO c\r\n");
                string? last = await tls.ReplyAsync();
                Assert.StartsWith("250", last, System.StringComparison.Ordinal);
            }

            using (Wire plain = await Wire.OpenAsync(server.BoundPort, readBanner: false))
            {
                await plain.SendAsync("EHLO c\r\n");
                string? reply = await plain.LineAsync(5000);
                Assert.True(reply is null || !reply.StartsWith('2'));
            }
            cts.Cancel();
        }
    }

    [Fact]
    public void NormaliseLineEndings_RewritesBareCrAndLf_AndLeavesCrlfAlone()
    {
        Assert.Equal("a\r\nb\r\nc\r\nd", Encoding.ASCII.GetString(SmtpSession.NormaliseLineEndings(Encoding.ASCII.GetBytes("a\nb\rc\r\nd"))));
        byte[] clean = Encoding.ASCII.GetBytes("x\r\ny\r\n");
        Assert.Same(clean, SmtpSession.NormaliseLineEndings(clean));
    }

    [Fact]
    public void OutboundDotStuffing_SeesDotsBehindBareCr()
    {
        // "<CR>.<CR>" is the smuggling form a lenient receiver might end on;
        // after normalisation it is a real line and gets stuffed.
        string stuffed = Encoding.ASCII.GetString(SmtpClientSession.DotStuff(Encoding.ASCII.GetBytes("a\r.\rb\r\n")));
        Assert.Equal("a\r\n..\r\nb\r\n", stuffed);
    }

    [Fact]
    public void AuthFailureLimiter_CountsWithinTheWindow_AndForgetsAfterIt()
    {
        var now = new System.DateTimeOffset(2026, 9, 21, 10, 0, 0, System.TimeSpan.Zero);
        var limiter = new AuthFailureLimiter(2, System.TimeSpan.FromMinutes(15), () => now);
        Assert.True(limiter.IsAllowed("203.0.113.1"));
        limiter.RecordFailure("203.0.113.1");
        limiter.RecordFailure("203.0.113.1");
        Assert.False(limiter.IsAllowed("203.0.113.1"));
        Assert.True(limiter.IsAllowed("203.0.113.2"));
        now = now.AddMinutes(16);
        Assert.True(limiter.IsAllowed("203.0.113.1"));
    }

    [Fact]
    public void PeerCertificateAcceptance_RefusesNameMismatches()
    {
        Assert.True(SmtpClientSession.IsAcceptable(SslPolicyErrors.None, null));
        Assert.False(SmtpClientSession.IsAcceptable(SslPolicyErrors.RemoteCertificateNameMismatch, null));
        Assert.False(SmtpClientSession.IsAcceptable(SslPolicyErrors.RemoteCertificateNotAvailable, null));
        Assert.False(SmtpClientSession.IsAcceptable(SslPolicyErrors.RemoteCertificateChainErrors, null));
    }
}
