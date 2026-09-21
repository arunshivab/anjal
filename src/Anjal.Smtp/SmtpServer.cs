using System.Net.Sockets;

namespace Anjal.Smtp;

/// <summary>
/// SMTP server: listens on a TCP socket and spawns an <see cref="SmtpSession"/>
/// per accepted connection. Sessions run concurrently.
/// </summary>
public sealed class SmtpServer : System.IDisposable
{
    private readonly SmtpServerOptions options;
    private readonly IMessageSink sink;
    private readonly IInboundAuthenticator? authenticator;
    private readonly bool enforceReject;
    private readonly ISmtpAuthenticator? smtpAuthenticator;
    private readonly ILocalDomainResolver? localDomains;
    private readonly TcpListener listener;
    private bool started;
    private bool disposed;

    /// <summary>
    /// Construct a server. <see cref="StartAsync"/> must be called to begin
    /// accepting connections.
    /// </summary>
    /// <param name="options">Server configuration.</param>
    /// <param name="sink">Sink for delivered messages.</param>
    public SmtpServer(SmtpServerOptions options, IMessageSink sink)
        : this(options, sink, authenticator: null, enforceReject: false,
               smtpAuthenticator: null, localDomains: null)
    { }

    /// <summary>
    /// Construct a server with inbound authentication only (PR 8 ctor).
    /// </summary>
    /// <param name="options">Server configuration.</param>
    /// <param name="sink">Sink for delivered messages.</param>
    /// <param name="authenticator">Inbound SPF/DKIM/DMARC authenticator.</param>
    /// <param name="enforceReject">Refuse messages with SMTP 550 when DMARC says reject.</param>
    public SmtpServer(SmtpServerOptions options, IMessageSink sink,
        IInboundAuthenticator? authenticator, bool enforceReject)
        : this(options, sink, authenticator, enforceReject,
               smtpAuthenticator: null, localDomains: null)
    { }

    /// <summary>
    /// Construct a server with full PR 9 feature set: inbound (SPF/DKIM/DMARC)
    /// authentication, optional submission-side (AUTH PLAIN/LOGIN) authentication,
    /// and optional local-domain resolution for the open-relay guard.
    /// </summary>
    /// <param name="options">Server configuration. The role
    /// (<see cref="SmtpServerOptions.Role"/>) determines whether this listener
    /// is an MTA port or a submission port.</param>
    /// <param name="sink">Sink for delivered messages.</param>
    /// <param name="authenticator">Inbound SPF/DKIM/DMARC authenticator (optional).</param>
    /// <param name="enforceReject">Refuse messages with SMTP 550 when DMARC says reject.</param>
    /// <param name="smtpAuthenticator">For submission listeners, validates
    /// AUTH PLAIN/LOGIN credentials. Null disables submission authentication.</param>
    /// <param name="localDomains">For MTA listeners, decides whether a
    /// destination domain is local. Null accepts any RCPT (legacy behavior,
    /// safe only on closed networks).</param>
    public SmtpServer(SmtpServerOptions options, IMessageSink sink,
        IInboundAuthenticator? authenticator, bool enforceReject,
        ISmtpAuthenticator? smtpAuthenticator, ILocalDomainResolver? localDomains)
    {
        System.ArgumentNullException.ThrowIfNull(options);
        System.ArgumentNullException.ThrowIfNull(sink);
        this.options = options;
        this.sink = sink;
        this.authenticator = authenticator;
        this.enforceReject = enforceReject;
        this.smtpAuthenticator = smtpAuthenticator;
        this.localDomains = localDomains;
        this.listener = new TcpListener(options.BindAddress, options.Port);
    }

    /// <summary>
    /// The actual port the server bound to. Useful when configured with port 0
    /// (auto-assign) - read after <see cref="StartAsync"/> returns.
    /// </summary>
    public int BoundPort => ((System.Net.IPEndPoint)this.listener.LocalEndpoint).Port;

    /// <summary>
    /// Start listening. Returns once the socket is bound and ready to accept.
    /// The accept loop runs in the background until <paramref name="ct"/>
    /// signals or <see cref="Dispose"/> is called.
    /// </summary>
    /// <param name="ct">Cancellation token controlling the lifetime of the
    /// accept loop.</param>
    /// <returns>A task that completes when the accept loop has exited.</returns>
    public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken ct = default)
    {
        if (this.started)
        {
            throw new System.InvalidOperationException("Server already started.");
        }
        this.started = true;
        this.listener.Start();
        return this.AcceptLoopAsync(ct);
    }

    private async System.Threading.Tasks.Task AcceptLoopAsync(System.Threading.CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await this.listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (System.OperationCanceledException)
                {
                    return;
                }
                catch (System.Net.Sockets.SocketException)
                {
                    return;
                }

                // Admission control happens before a session exists, so a
                // flood of connections costs a refusal each and nothing more.
                string address = RemoteAddressOf(client);
                if (!this.TryAdmit(address))
                {
                    Counters.Increment("anjal_smtp_connections_refused_total");
                    _ = RefuseAsync(client);
                    continue;
                }

                // Fire and forget - exceptions inside the session are handled there.
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        var session = new SmtpSession(client, this.options, this.sink,
                            this.authenticator, this.enforceReject,
                            this.smtpAuthenticator, this.localDomains);
                        await session.RunAsync(ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        this.Release(address);
                    }
                }, ct);
            }
        }
        finally
        {
            try
            {
                this.listener.Stop();
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Already stopped - swallow.
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }
        this.disposed = true;
        try
        {
            this.listener.Stop();
        }
        catch (System.Net.Sockets.SocketException)
        {
            // Already stopped - swallow.
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> perAddress = new(System.StringComparer.Ordinal);
    private int active;

    /// <summary>Sessions currently being served by this listener.</summary>
    public int ActiveSessions => System.Threading.Volatile.Read(ref this.active);

    /// <summary>
    /// Take a session slot for this address, or refuse. Both the global cap
    /// and the per-address cap must have room.
    /// </summary>
    private bool TryAdmit(string address)
    {
        if (System.Threading.Interlocked.Increment(ref this.active) > this.options.MaxConcurrentSessions)
        {
            System.Threading.Interlocked.Decrement(ref this.active);
            return false;
        }
        int mine = this.perAddress.AddOrUpdate(address, 1, (_, n) => n + 1);
        if (mine > this.options.MaxSessionsPerAddress)
        {
            this.Release(address);
            return false;
        }
        return true;
    }

    private void Release(string address)
    {
        System.Threading.Interlocked.Decrement(ref this.active);
        int left = this.perAddress.AddOrUpdate(address, 0, (_, n) => n - 1);
        if (left <= 0)
        {
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, int>>)this.perAddress)
                .Remove(new System.Collections.Generic.KeyValuePair<string, int>(address, left));
        }
    }

    private static string RemoteAddressOf(TcpClient client)
    {
        try
        {
            return (client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "unknown";
        }
        catch (System.ObjectDisposedException)
        {
            return "unknown";
        }
    }

    /// <summary>Tell a refused client why, briefly, then close.</summary>
    private static async System.Threading.Tasks.Task RefuseAsync(TcpClient client)
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(5));
            byte[] reply = System.Text.Encoding.ASCII.GetBytes("421 4.7.0 Too many connections, try again later\r\n");
            await client.GetStream().WriteAsync(reply, cts.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Refusing anyway; a failed goodbye changes nothing.
        catch (System.Exception)
        {
        }
#pragma warning restore CA1031
        finally
        {
            client.Close();
        }
    }
}
