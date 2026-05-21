using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anjal.Smtp.Tests;

/// <summary>
/// Integration tests that drive a real <see cref="SmtpServer"/> via raw
/// TcpClient sockets to exercise the full AUTH protocol surface.
/// </summary>
public class SmtpAuthTests
{
    [Fact]
    public async System.Threading.Tasks.Task AuthPlain_CorrectCredentials_Succeeds()
    {
        using var fixture = await StartSubmissionServerAsync().ConfigureAwait(false);

        using var conn = await ConnectAsync(fixture.Port).ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false); // 220 banner
        await conn.WriteLineAsync("EHLO test.local").ConfigureAwait(false);
        await DrainEhloAsync(conn).ConfigureAwait(false);

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0alice\0Sekret123"));
        await conn.WriteLineAsync($"AUTH PLAIN {b64}").ConfigureAwait(false);

        string? reply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.NotNull(reply);
        Assert.StartsWith("235", reply!, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task AuthPlain_WrongPassword_Fails535()
    {
        using var fixture = await StartSubmissionServerAsync().ConfigureAwait(false);

        using var conn = await ConnectAsync(fixture.Port).ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);
        await conn.WriteLineAsync("EHLO test.local").ConfigureAwait(false);
        await DrainEhloAsync(conn).ConfigureAwait(false);

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0alice\0WrongPassword"));
        await conn.WriteLineAsync($"AUTH PLAIN {b64}").ConfigureAwait(false);

        string? reply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.NotNull(reply);
        Assert.StartsWith("535", reply!, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task AuthPlain_UnknownUser_Fails535()
    {
        using var fixture = await StartSubmissionServerAsync().ConfigureAwait(false);

        using var conn = await ConnectAsync(fixture.Port).ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);
        await conn.WriteLineAsync("EHLO test.local").ConfigureAwait(false);
        await DrainEhloAsync(conn).ConfigureAwait(false);

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0nobody\0anything"));
        await conn.WriteLineAsync($"AUTH PLAIN {b64}").ConfigureAwait(false);

        string? reply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.StartsWith("535", reply!, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task AuthLogin_Succeeds_WithStepwiseExchange()
    {
        using var fixture = await StartSubmissionServerAsync().ConfigureAwait(false);

        using var conn = await ConnectAsync(fixture.Port).ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);
        await conn.WriteLineAsync("EHLO test.local").ConfigureAwait(false);
        await DrainEhloAsync(conn).ConfigureAwait(false);

        await conn.WriteLineAsync("AUTH LOGIN").ConfigureAwait(false);
        string? prompt1 = await conn.ReadLineAsync().ConfigureAwait(false);
        // Server prompts base64("Username:") = "VXNlcm5hbWU6"
        Assert.StartsWith("334 ", prompt1!, System.StringComparison.Ordinal);
        Assert.Contains("VXNlcm5hbWU6", prompt1!, System.StringComparison.Ordinal);

        string b64User = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice"));
        await conn.WriteLineAsync(b64User).ConfigureAwait(false);

        string? prompt2 = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.StartsWith("334 ", prompt2!, System.StringComparison.Ordinal);
        Assert.Contains("UGFzc3dvcmQ6", prompt2!, System.StringComparison.Ordinal);

        string b64Pass = Convert.ToBase64String(Encoding.UTF8.GetBytes("Sekret123"));
        await conn.WriteLineAsync(b64Pass).ConfigureAwait(false);

        string? reply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.StartsWith("235", reply!, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Auth_Cancellation_RespondsWith501()
    {
        using var fixture = await StartSubmissionServerAsync().ConfigureAwait(false);

        using var conn = await ConnectAsync(fixture.Port).ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);
        await conn.WriteLineAsync("EHLO test.local").ConfigureAwait(false);
        await DrainEhloAsync(conn).ConfigureAwait(false);

        await conn.WriteLineAsync("AUTH LOGIN").ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false); // username prompt
        await conn.WriteLineAsync("*").ConfigureAwait(false); // client cancels

        string? reply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.StartsWith("501", reply!, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task SubmissionPort_MailWithoutAuth_Returns530()
    {
        using var fixture = await StartSubmissionServerAsync().ConfigureAwait(false);

        using var conn = await ConnectAsync(fixture.Port).ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);
        await conn.WriteLineAsync("EHLO test.local").ConfigureAwait(false);
        await DrainEhloAsync(conn).ConfigureAwait(false);

        await conn.WriteLineAsync("MAIL FROM:<a@example.test>").ConfigureAwait(false);
        string? reply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.StartsWith("530", reply!, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task SubmissionPort_MailFromDisallowedDomain_Returns550()
    {
        // Set up user with allowed_from_domains = ["hospital-a.test"]
        using var fixture = await StartSubmissionServerAsync().ConfigureAwait(false);

        using var conn = await ConnectAsync(fixture.Port).ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);
        await conn.WriteLineAsync("EHLO test.local").ConfigureAwait(false);
        await DrainEhloAsync(conn).ConfigureAwait(false);

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0alice\0Sekret123"));
        await conn.WriteLineAsync($"AUTH PLAIN {b64}").ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false); // 235

        await conn.WriteLineAsync("MAIL FROM:<evil@other-domain.test>").ConfigureAwait(false);
        string? reply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.StartsWith("550", reply!, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task SubmissionPort_MailFromAllowedDomain_Accepted()
    {
        using var fixture = await StartSubmissionServerAsync().ConfigureAwait(false);

        using var conn = await ConnectAsync(fixture.Port).ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);
        await conn.WriteLineAsync("EHLO test.local").ConfigureAwait(false);
        await DrainEhloAsync(conn).ConfigureAwait(false);

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0alice\0Sekret123"));
        await conn.WriteLineAsync($"AUTH PLAIN {b64}").ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false); // 235

        await conn.WriteLineAsync("MAIL FROM:<notify@hospital-a.test>").ConfigureAwait(false);
        string? reply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.StartsWith("250", reply!, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task SubmissionPort_AdminUser_AnyFromDomain_Accepted()
    {
        // Admin user has empty allowed_from_domains -> any domain.
        using var fixture = await StartSubmissionServerAsync(
            user: "root", password: "RootPass", allowedDomains: System.Array.Empty<string>())
            .ConfigureAwait(false);

        using var conn = await ConnectAsync(fixture.Port).ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);
        await conn.WriteLineAsync("EHLO test.local").ConfigureAwait(false);
        await DrainEhloAsync(conn).ConfigureAwait(false);

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0root\0RootPass"));
        await conn.WriteLineAsync($"AUTH PLAIN {b64}").ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);

        // Send from any domain - should work because admin has no restrictions.
        await conn.WriteLineAsync("MAIL FROM:<anywhere@anything.test>").ConfigureAwait(false);
        string? reply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.StartsWith("250", reply!, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Auth_OnMtaPort_NotAvailable()
    {
        using var fixture = await StartMtaServerAsync().ConfigureAwait(false);

        using var conn = await ConnectAsync(fixture.Port).ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);
        await conn.WriteLineAsync("EHLO test.local").ConfigureAwait(false);
        await DrainEhloAsync(conn).ConfigureAwait(false);

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0alice\0Sekret123"));
        await conn.WriteLineAsync($"AUTH PLAIN {b64}").ConfigureAwait(false);
        string? reply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.StartsWith("502", reply!, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Auth_AfterAlreadyAuthenticated_Returns503()
    {
        using var fixture = await StartSubmissionServerAsync().ConfigureAwait(false);

        using var conn = await ConnectAsync(fixture.Port).ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);
        await conn.WriteLineAsync("EHLO test.local").ConfigureAwait(false);
        await DrainEhloAsync(conn).ConfigureAwait(false);

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0alice\0Sekret123"));
        await conn.WriteLineAsync($"AUTH PLAIN {b64}").ConfigureAwait(false);
        await conn.ReadLineAsync().ConfigureAwait(false);

        // Try to AUTH again.
        await conn.WriteLineAsync($"AUTH PLAIN {b64}").ConfigureAwait(false);
        string? reply = await conn.ReadLineAsync().ConfigureAwait(false);
        Assert.StartsWith("503", reply!, System.StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static async System.Threading.Tasks.Task<ServerFixture> StartSubmissionServerAsync(
        string user = "alice",
        string password = "Sekret123",
        string[]? allowedDomains = null)
    {
        string hash = Pbkdf2Hasher.Hash(password, iterations: 1000);
        var authenticator = new TestAuthenticator(user, hash, allowedDomains ?? new[] { "hospital-a.test" });
        var sink = new NoopSink();
        var options = new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "test.local",
            Role = SmtpServerRole.Submission,
            AllowPlaintextAuth = true,
        };
        var cts = new System.Threading.CancellationTokenSource();
        var server = new SmtpServer(options, sink,
            authenticator: null, enforceReject: false,
            smtpAuthenticator: authenticator, localDomains: null);
        var task = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(50).ConfigureAwait(false);
        return new ServerFixture(server, task, cts);
    }

    private static async System.Threading.Tasks.Task<ServerFixture> StartMtaServerAsync()
    {
        var sink = new NoopSink();
        var options = new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AdvertisedHostName = "test.local",
            Role = SmtpServerRole.Mta,
        };
        var cts = new System.Threading.CancellationTokenSource();
        var server = new SmtpServer(options, sink);
        var task = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(50).ConfigureAwait(false);
        return new ServerFixture(server, task, cts);
    }

    private static async System.Threading.Tasks.Task<SocketConn> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        return new SocketConn(client);
    }

    private static async System.Threading.Tasks.Task DrainEhloAsync(SocketConn conn)
    {
        // Read multi-line 250-... until a final 250 line.
        string? line;
        do
        {
            line = await conn.ReadLineAsync().ConfigureAwait(false);
        } while (line is not null && line.StartsWith("250-", System.StringComparison.Ordinal));
    }

    private sealed class ServerFixture : System.IDisposable
    {
        private readonly SmtpServer server;
        private readonly System.Threading.Tasks.Task task;
        private readonly System.Threading.CancellationTokenSource cts;

        public int Port { get; }

        public ServerFixture(SmtpServer s, System.Threading.Tasks.Task t, System.Threading.CancellationTokenSource c)
        {
            this.server = s;
            this.task = t;
            this.cts = c;
            this.Port = s.BoundPort;
        }

        public void Dispose()
        {
            this.cts.Cancel();
            try { this.task.Wait(System.TimeSpan.FromSeconds(2)); }
            catch (System.AggregateException) { }
            catch (System.OperationCanceledException) { }
            this.server.Dispose();
            this.cts.Dispose();
        }
    }

    private sealed class SocketConn : System.IDisposable
    {
        private readonly TcpClient client;
        private readonly System.IO.StreamReader reader;
        private readonly System.IO.StreamWriter writer;

        public SocketConn(TcpClient client)
        {
            this.client = client;
            var stream = client.GetStream();
            this.reader = new System.IO.StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            this.writer = new System.IO.StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true,
            };
        }

        public System.Threading.Tasks.Task<string?> ReadLineAsync() => this.reader.ReadLineAsync();
        public System.Threading.Tasks.Task WriteLineAsync(string s) => this.writer.WriteLineAsync(s);

        public void Dispose()
        {
            try { this.writer.Dispose(); } catch (System.IO.IOException) { }
            try { this.reader.Dispose(); } catch (System.IO.IOException) { }
            try { this.client.Dispose(); } catch (System.IO.IOException) { }
        }
    }

    private sealed class TestAuthenticator : ISmtpAuthenticator
    {
        private readonly string user;
        private readonly string hash;
        private readonly string[] domains;

        public TestAuthenticator(string user, string hash, string[] domains)
        {
            this.user = user;
            this.hash = hash;
            this.domains = domains;
        }

        public System.Threading.Tasks.Task<AuthenticatedUser?> AuthenticateAsync(string username, string password, System.Threading.CancellationToken ct = default)
        {
            if (!string.Equals(username, this.user, System.StringComparison.OrdinalIgnoreCase))
            {
                return System.Threading.Tasks.Task.FromResult<AuthenticatedUser?>(null);
            }
            if (!Pbkdf2Hasher.Verify(password, this.hash))
            {
                return System.Threading.Tasks.Task.FromResult<AuthenticatedUser?>(null);
            }
            return System.Threading.Tasks.Task.FromResult<AuthenticatedUser?>(new AuthenticatedUser
            {
                Username = this.user,
                AllowedFromDomains = this.domains,
            });
        }
    }

    private sealed class NoopSink : IMessageSink
    {
        public System.Threading.Tasks.Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, System.Threading.CancellationToken ct = default)
        {
            return System.Threading.Tasks.Task.FromResult(new DeliveryResult
            {
                Outcome = DeliveryOutcome.Accepted,
                ReplyText = "ok",
            });
        }
    }
}
