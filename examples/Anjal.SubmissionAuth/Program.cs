using System.Net;
using System.Net.Sockets;
using System.Text;
using Anjal.Smtp;

namespace Anjal.Examples.SubmissionAuth;

/// <summary>
/// Demonstrates Anjal's SMTP submission authentication. Spawns a single
/// SmtpServer on a random submission port (Role=Submission) with an
/// in-memory authenticator that knows about one user. Then connects via
/// TcpClient and exercises the AUTH PLAIN flow.
///
/// <para>Scenarios:</para>
/// <list type="number">
/// <item>AUTH PLAIN with correct credentials -> 235 success, message accepted.</item>
/// <item>AUTH PLAIN with wrong password -> 535 failure.</item>
/// <item>MAIL FROM without AUTH on submission port -> 530 auth required.</item>
/// <item>MAIL FROM with disallowed sender domain -> 550 not authorized.</item>
/// </list>
/// </summary>
internal static class Program
{
    private static readonly string[] HospitalADomains = new[] { "hospital-a.test" };

    private static async System.Threading.Tasks.Task<int> Main()
    {
        Console.WriteLine("=== Anjal submission AUTH demo ===");
        Console.WriteLine();

        // Hash the password the way the API would.
        string passwordHash = Pbkdf2Hasher.Hash("CorrectPassword123");

        // Inline authenticator: one user "lipi-tenant-a" allowed to send
        // as hospital-a.test.
        ISmtpAuthenticator authenticator = new InlineAuthenticator(
            username: "lipi-tenant-a",
            passwordHash: passwordHash,
            allowedDomains: HospitalADomains);

        var sink = new CollectingSink();

        var options = new SmtpServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0, // auto-assign
            AdvertisedHostName = "anjal.test",
            Role = SmtpServerRole.Submission,
            AllowPlaintextAuth = true, // demo only - real deployments need TLS
        };

        using var server = new SmtpServer(options, sink,
            authenticator: null, enforceReject: false,
            smtpAuthenticator: authenticator, localDomains: null);

        using var cts = new System.Threading.CancellationTokenSource();
        System.Threading.Tasks.Task serverTask = server.StartAsync(cts.Token);
        await System.Threading.Tasks.Task.Delay(100).ConfigureAwait(false);

        int port = server.BoundPort;
        Console.WriteLine($"Submission server listening on 127.0.0.1:{port}");
        Console.WriteLine();

        int failures = 0;

        Console.WriteLine("--- Scenario 1: correct credentials ---");
        if (await RunScenarioAsync(port, "lipi-tenant-a", "CorrectPassword123",
                "patient-notify@hospital-a.test", "patient@gmail.com",
                expectSuccess: true).ConfigureAwait(false))
        {
            Console.WriteLine("  PASS: message accepted by submission server.");
        }
        else
        {
            Console.WriteLine("  FAIL");
            failures++;
        }
        Console.WriteLine();

        Console.WriteLine("--- Scenario 2: wrong password ---");
        if (await RunScenarioAsync(port, "lipi-tenant-a", "WrongPassword",
                "patient-notify@hospital-a.test", "patient@gmail.com",
                expectSuccess: false, expectedFailureCode: "535").ConfigureAwait(false))
        {
            Console.WriteLine("  PASS: server rejected with 535.");
        }
        else
        {
            Console.WriteLine("  FAIL");
            failures++;
        }
        Console.WriteLine();

        Console.WriteLine("--- Scenario 3: MAIL FROM without AUTH ---");
        if (await RunNoAuthScenarioAsync(port, "patient-notify@hospital-a.test",
                expectedFailureCode: "530").ConfigureAwait(false))
        {
            Console.WriteLine("  PASS: server rejected with 530.");
        }
        else
        {
            Console.WriteLine("  FAIL");
            failures++;
        }
        Console.WriteLine();

        Console.WriteLine("--- Scenario 4: from-domain not in allowed list ---");
        if (await RunScenarioAsync(port, "lipi-tenant-a", "CorrectPassword123",
                "evil@hospital-b.test", "patient@gmail.com",
                expectSuccess: false, expectedFailureCode: "550").ConfigureAwait(false))
        {
            Console.WriteLine("  PASS: server rejected with 550.");
        }
        else
        {
            Console.WriteLine("  FAIL");
            failures++;
        }
        Console.WriteLine();

        cts.Cancel();
        try { await serverTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        Console.WriteLine();
        Console.WriteLine($"Messages accepted by sink: {sink.MessagesAccepted}");
        Console.WriteLine();
        if (failures > 0)
        {
            Console.WriteLine($"FAILED: {failures} scenarios did not match expectation.");
            return 1;
        }
        Console.WriteLine("SUCCESS: all 4 scenarios produced the expected verdict.");
        return 0;
    }

    /// <summary>Drive one full session: EHLO, AUTH PLAIN, MAIL FROM, RCPT TO, DATA, QUIT.</summary>
    private static async System.Threading.Tasks.Task<bool> RunScenarioAsync(
        int port, string username, string password, string from, string to,
        bool expectSuccess, string expectedFailureCode = "")
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        using var stream = client.GetStream();
        using var reader = new System.IO.StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        using var writer = new System.IO.StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

        string? greeting = await reader.ReadLineAsync().ConfigureAwait(false);
        Console.WriteLine($"  S: {greeting}");

        await writer.WriteLineAsync("EHLO client.test").ConfigureAwait(false);
        string ehloLine;
        do
        {
            ehloLine = (await reader.ReadLineAsync().ConfigureAwait(false)) ?? string.Empty;
            Console.WriteLine($"  S: {ehloLine}");
        } while (ehloLine.StartsWith("250-", StringComparison.Ordinal));

        // AUTH PLAIN. Format: base64("\0username\0password")
        string credentials = $"\0{username}\0{password}";
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        await writer.WriteLineAsync($"AUTH PLAIN {b64}").ConfigureAwait(false);
        string? authReply = await reader.ReadLineAsync().ConfigureAwait(false);
        Console.WriteLine($"  C: AUTH PLAIN <{credentials.Length} bytes>");
        Console.WriteLine($"  S: {authReply}");

        if (!expectSuccess && authReply is not null && authReply.StartsWith(expectedFailureCode, StringComparison.Ordinal))
        {
            return true;
        }
        if (authReply is null || !authReply.StartsWith("235", StringComparison.Ordinal))
        {
            return !expectSuccess;
        }

        // MAIL FROM
        await writer.WriteLineAsync($"MAIL FROM:<{from}>").ConfigureAwait(false);
        string? mailReply = await reader.ReadLineAsync().ConfigureAwait(false);
        Console.WriteLine($"  C: MAIL FROM:<{from}>");
        Console.WriteLine($"  S: {mailReply}");

        if (!expectSuccess && mailReply is not null && mailReply.StartsWith(expectedFailureCode, StringComparison.Ordinal))
        {
            return true;
        }
        if (mailReply is null || !mailReply.StartsWith("250", StringComparison.Ordinal))
        {
            return !expectSuccess;
        }

        // RCPT TO
        await writer.WriteLineAsync($"RCPT TO:<{to}>").ConfigureAwait(false);
        string? rcptReply = await reader.ReadLineAsync().ConfigureAwait(false);
        Console.WriteLine($"  C: RCPT TO:<{to}>");
        Console.WriteLine($"  S: {rcptReply}");

        // DATA
        await writer.WriteLineAsync("DATA").ConfigureAwait(false);
        string? dataReply = await reader.ReadLineAsync().ConfigureAwait(false);
        Console.WriteLine($"  S: {dataReply}");
        await writer.WriteAsync("From: " + from + "\r\nTo: " + to + "\r\nSubject: Demo\r\n\r\nDemo body.\r\n.\r\n").ConfigureAwait(false);
        string? endReply = await reader.ReadLineAsync().ConfigureAwait(false);
        Console.WriteLine($"  S: {endReply}");

        await writer.WriteLineAsync("QUIT").ConfigureAwait(false);
        return expectSuccess && endReply is not null && endReply.StartsWith("250", StringComparison.Ordinal);
    }

    /// <summary>Drive a session that tries MAIL FROM without AUTH.</summary>
    private static async System.Threading.Tasks.Task<bool> RunNoAuthScenarioAsync(int port, string from, string expectedFailureCode)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        using var stream = client.GetStream();
        using var reader = new System.IO.StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        using var writer = new System.IO.StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

        string? greeting = await reader.ReadLineAsync().ConfigureAwait(false);
        Console.WriteLine($"  S: {greeting}");

        await writer.WriteLineAsync("EHLO client.test").ConfigureAwait(false);
        string ehloLine;
        do
        {
            ehloLine = (await reader.ReadLineAsync().ConfigureAwait(false)) ?? string.Empty;
            Console.WriteLine($"  S: {ehloLine}");
        } while (ehloLine.StartsWith("250-", StringComparison.Ordinal));

        await writer.WriteLineAsync($"MAIL FROM:<{from}>").ConfigureAwait(false);
        string? mailReply = await reader.ReadLineAsync().ConfigureAwait(false);
        Console.WriteLine($"  C: MAIL FROM:<{from}>");
        Console.WriteLine($"  S: {mailReply}");

        await writer.WriteLineAsync("QUIT").ConfigureAwait(false);
        return mailReply is not null && mailReply.StartsWith(expectedFailureCode, StringComparison.Ordinal);
    }

    /// <summary>Authenticator that knows one user in memory.</summary>
    private sealed class InlineAuthenticator : ISmtpAuthenticator
    {
        private readonly string username;
        private readonly string passwordHash;
        private readonly string[] allowedDomains;

        public InlineAuthenticator(string username, string passwordHash, string[] allowedDomains)
        {
            this.username = username;
            this.passwordHash = passwordHash;
            this.allowedDomains = allowedDomains;
        }

        public System.Threading.Tasks.Task<AuthenticatedUser?> AuthenticateAsync(string username, string password, System.Threading.CancellationToken ct = default)
        {
            if (!string.Equals(username, this.username, StringComparison.OrdinalIgnoreCase))
            {
                return System.Threading.Tasks.Task.FromResult<AuthenticatedUser?>(null);
            }
            if (!Pbkdf2Hasher.Verify(password, this.passwordHash))
            {
                return System.Threading.Tasks.Task.FromResult<AuthenticatedUser?>(null);
            }
            return System.Threading.Tasks.Task.FromResult<AuthenticatedUser?>(new AuthenticatedUser
            {
                Username = this.username,
                AllowedFromDomains = this.allowedDomains,
            });
        }
    }

    /// <summary>Sink that counts successful deliveries.</summary>
    private sealed class CollectingSink : IMessageSink
    {
        public int MessagesAccepted { get; private set; }

        public System.Threading.Tasks.Task<DeliveryResult> DeliverAsync(DeliveryContext ctx, System.Threading.CancellationToken ct = default)
        {
            this.MessagesAccepted++;
            return System.Threading.Tasks.Task.FromResult(new DeliveryResult
            {
                Outcome = DeliveryOutcome.Accepted,
                ReplyText = "queued",
            });
        }
    }
}
