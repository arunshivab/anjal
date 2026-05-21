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

                // Fire and forget - exceptions inside the session are handled there.
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    var session = new SmtpSession(client, this.options, this.sink,
                        this.authenticator, this.enforceReject,
                        this.smtpAuthenticator, this.localDomains);
                    await session.RunAsync(ct).ConfigureAwait(false);
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
}
