using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Anjal.Imap;

/// <summary>rc.28 (DES-20 §3): how the IMAP server listens.</summary>
public sealed class ImapServerOptions
{
    /// <summary>The address to listen on.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Any;

    /// <summary>The port (993); 0 picks a free one (tests).</summary>
    public int Port { get; init; } = 993;

    /// <summary>The host name in the greeting.</summary>
    public string HostName { get; init; } = "localhost";

    /// <summary>TLS from the first byte (RFC 8314). Off only in tests.</summary>
    public bool ImplicitTls { get; init; } = true;

    /// <summary>The certificate, read for each connection (it is renewed while the server runs).</summary>
    public Func<X509Certificate2?>? Certificate { get; init; }

    /// <summary>Sessions one person may have at once.</summary>
    public int MaxSessionsPerUser { get; init; } = 20;

    /// <summary>Sessions in all.</summary>
    public int MaxSessions { get; init; } = 2000;

    /// <summary>A session ends after this long without a command (IDLE included).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>The largest message an app may APPEND.</summary>
    public long MaxAppendBytes { get; init; } = 25L * 1024 * 1024;

    /// <summary>How often IDLE looks for new mail.</summary>
    public TimeSpan IdlePoll { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Failed sign-ins per address on the internet (shared with submission).</summary>
    public Anjal.Smtp.AuthFailureLimiter? AuthFailures { get; init; }

    /// <summary>Optional log.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>rc.28 (DES-20): Anjal's IMAP server - IMAP4rev1 on 993, for mail apps signed in with app passwords.</summary>
public sealed class ImapServer : IAsyncDisposable
{
    private readonly ImapServerOptions options;
    private readonly IImapBackend backend;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, int> perUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> perAddress = new(StringComparer.Ordinal);
    private TcpListener? listener;
    private Task? loop;
    private int sessions;

    /// <summary>Construct.</summary>
    /// <param name="options">How to listen.</param>
    /// <param name="backend">Where the mail is.</param>
    public ImapServer(ImapServerOptions options, IImapBackend backend)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        this.options = options;
        this.backend = backend;
    }

    /// <summary>The port listened on (after <see cref="Start"/>).</summary>
    public int LocalPort => (this.listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;

    /// <summary>Start listening.</summary>
    public void Start()
    {
        this.listener = new TcpListener(this.options.BindAddress, this.options.Port);
        this.listener.Start();
        this.loop = Task.Run(() => this.AcceptAsync(this.stop.Token));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await this.stop.CancelAsync().ConfigureAwait(false);
        this.listener?.Stop();
        if (this.loop is not null)
        {
            try
            {
                await this.loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        this.stop.Dispose();
    }

    /// <summary>Connections at once from one internet address (a household or office shares one).</summary>
    internal const int MaxPerAddress = 40;

    /// <summary>Count a person's session; false when they have too many.</summary>
    /// <param name="user">The address.</param>
    /// <returns>True when allowed.</returns>
    internal bool Enter(string user)
    {
        int n = this.perUser.AddOrUpdate(user, 1, (_, v) => v + 1);
        if (n > this.options.MaxSessionsPerUser)
        {
            this.Leave(user);
            return false;
        }
        return true;
    }

    /// <summary>A person's session ended.</summary>
    /// <param name="user">The address.</param>
    internal void Leave(string user) => this.perUser.AddOrUpdate(user, 0, (_, v) => Math.Max(0, v - 1));

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await this.listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => this.ServeAsync(client, ct), ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        string remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        if (Interlocked.Increment(ref this.sessions) > this.options.MaxSessions)
        {
            Interlocked.Decrement(ref this.sessions);
            client.Dispose();
            return;
        }
        // One address cannot take every slot: connections kept open without signing in are counted too.
        if (this.perAddress.AddOrUpdate(remote, 1, (_, v) => v + 1) > MaxPerAddress)
        {
            this.perAddress.AddOrUpdate(remote, 0, (_, v) => Math.Max(0, v - 1));
            Interlocked.Decrement(ref this.sessions);
            client.Dispose();
            return;
        }
        try
        {
            using (client)
            {
                client.NoDelay = true;
                Stream stream = client.GetStream();
                if (this.options.ImplicitTls)
                {
                    if (this.options.Certificate?.Invoke() is not X509Certificate2 cert)
                    {
                        this.options.Log?.Invoke("IMAP: no certificate yet; connection from " + remote + " closed.");
                        return;
                    }
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    handshake.CancelAfter(TimeSpan.FromSeconds(30));
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = cert,
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                    }, handshake.Token).ConfigureAwait(false);
                    stream = ssl;
                }
                await using (stream.ConfigureAwait(false))
                {
                    var session = new ImapSession(this, stream, this.backend, this.options, remote, this.options.ImplicitTls);
                    await session.RunAsync(ct).ConfigureAwait(false);
                }
            }
        }
#pragma warning disable CA1031 // One broken connection never stops the server.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not (IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException))
            {
                this.options.Log?.Invoke($"IMAP: session from {remote} ended: {ex.GetType().Name}: {ex.Message}");
            }
        }
#pragma warning restore CA1031
        finally
        {
            this.perAddress.AddOrUpdate(remote, 0, (_, v) => Math.Max(0, v - 1));
            this.perAddress.TryRemove(new KeyValuePair<string, int>(remote, 0));
            Interlocked.Decrement(ref this.sessions);
        }
    }
}
