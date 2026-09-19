namespace Anjal.Spam;

/// <summary>
/// The DNS questions the scorer asks. Abstracted so tests can answer
/// them without the network and so lookups can be capped or cached.
/// Implementations must never throw; "unknown" is reported as
/// <see langword="null"/> and is scored as neutral.
/// </summary>
public interface ISpamDnsLookup
{
    /// <summary>Does the domain have at least one MX record, or failing that an A/AAAA record?</summary>
    /// <param name="domain">Sender domain.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True/false, or null when the lookup could not be completed.</returns>
    Task<bool?> CanReceiveMailAsync(string domain, CancellationToken ct = default);

    /// <summary>Does the host name resolve to at least one address?</summary>
    /// <param name="hostName">A HELO/EHLO host name.</param>
    /// <param name="ct">Cancellation.</param>
    Task<bool?> ResolvesAsync(string hostName, CancellationToken ct = default);

    /// <summary>Does the IP have a reverse (PTR) record?</summary>
    /// <param name="address">Connecting IP.</param>
    /// <param name="ct">Cancellation.</param>
    Task<bool?> HasReverseDnsAsync(string address, CancellationToken ct = default);
}

/// <summary>
/// Default lookups: MX via <see cref="Anjal.Dns.DnsResolver"/>, A/AAAA
/// and PTR via <see cref="System.Net.Dns"/>. Each question is capped at
/// <see cref="Timeout"/> so a slow resolver cannot stall delivery.
/// </summary>
public sealed class SystemSpamDnsLookup : ISpamDnsLookup
{
    private readonly Anjal.Dns.DnsResolver? mx;

    /// <summary>Construct.</summary>
    /// <param name="mxResolver">Resolver for MX queries; null skips the MX check and relies on A/AAAA.</param>
    public SystemSpamDnsLookup(Anjal.Dns.DnsResolver? mxResolver)
    {
        this.mx = mxResolver;
    }

    /// <summary>Per-question timeout. Default 3 seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <inheritdoc/>
    public async Task<bool?> CanReceiveMailAsync(string domain, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domain);
        if (domain.Length == 0)
        {
            return false;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(this.Timeout);
        if (this.mx is not null)
        {
            try
            {
                IReadOnlyList<Anjal.Dns.MxRecord> records = await this.mx.ResolveMxAsync(domain, cts.Token).ConfigureAwait(false);
                if (records.Count > 0)
                {
                    return true;
                }
            }
            catch (Anjal.Dns.DnsException)
            {
                // No MX (or NXDOMAIN) - fall through to the implicit-MX A/AAAA rule.
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        return await this.ResolvesAsync(domain, cts.Token).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool?> ResolvesAsync(string hostName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hostName);
        if (hostName.Length == 0)
        {
            return false;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(this.Timeout);
        try
        {
            System.Net.IPAddress[] addresses = await System.Net.Dns.GetHostAddressesAsync(hostName, cts.Token).ConfigureAwait(false);
            return addresses.Length > 0;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<bool?> HasReverseDnsAsync(string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!System.Net.IPAddress.TryParse(address, out System.Net.IPAddress? ip))
        {
            return null;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(this.Timeout);
        try
        {
            System.Net.IPHostEntry entry = await System.Net.Dns.GetHostEntryAsync(ip.ToString(), cts.Token).ConfigureAwait(false);
            return !string.IsNullOrEmpty(entry.HostName) && !string.Equals(entry.HostName, address, StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
