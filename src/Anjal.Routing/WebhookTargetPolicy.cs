using System.Net;
using System.Net.Sockets;

namespace Anjal.Routing;

/// <summary>
/// Where webhooks may be sent. By default only to <c>https</c> URLs on
/// public addresses: a webhook URL is admin-supplied, but a server that
/// will POST mail contents to any address it is told is a tool for reaching
/// internal services (SSRF), so the default is narrow and each widening is
/// an explicit setting.
/// <para>
/// The address check runs at connect time as well as at registration, on
/// the address actually being connected to. Checking only the name when
/// the rule is saved would let DNS be changed afterwards to point at an
/// internal address (DNS rebinding).
/// </para>
/// </summary>
public sealed class WebhookTargetPolicy
{
    /// <summary>Longest webhook URL accepted.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>Allow plain <c>http</c> (for a receiver on the same private network).</summary>
    public bool AllowHttp { get; init; }

    /// <summary>
    /// Allow loopback and private (RFC 1918, RFC 4193, link-local) addresses.
    /// Needed when the receiving application runs on the same host or LAN,
    /// as SIGMA and Lipi deployments may.
    /// </summary>
    public bool AllowPrivateAddresses { get; init; }

    /// <summary>
    /// Read the policy from <c>ANJAL_WEBHOOK_ALLOW_HTTP</c> and
    /// <c>ANJAL_WEBHOOK_ALLOW_PRIVATE</c> (both default false).
    /// </summary>
    public static WebhookTargetPolicy FromEnvironment() => new()
    {
        AllowHttp = IsTrue(System.Environment.GetEnvironmentVariable("ANJAL_WEBHOOK_ALLOW_HTTP")),
        AllowPrivateAddresses = IsTrue(System.Environment.GetEnvironmentVariable("ANJAL_WEBHOOK_ALLOW_PRIVATE")),
    };

    /// <summary>
    /// Why a URL is not acceptable, or null when it is. Checks the shape of
    /// the URL and, when the host is an address literal, the address. Host
    /// names are checked again at connect time.
    /// </summary>
    /// <param name="url">The candidate URL.</param>
    public string? Validate(string url)
    {
        System.ArgumentNullException.ThrowIfNull(url);
        if (url.Length > MaxUrlLength)
        {
            return $"A webhook URL can be at most {MaxUrlLength} characters.";
        }
        if (!System.Uri.TryCreate(url, System.UriKind.Absolute, out System.Uri? uri))
        {
            return "A webhook URL must be an absolute URL.";
        }
        bool https = uri.Scheme == System.Uri.UriSchemeHttps;
        bool http = uri.Scheme == System.Uri.UriSchemeHttp;
        if (!https && !(http && this.AllowHttp))
        {
            return this.AllowHttp ? "A webhook URL must use http or https." : "A webhook URL must use https (set ANJAL_WEBHOOK_ALLOW_HTTP=true for plain http on a private network).";
        }
        if (uri.UserInfo.Length > 0)
        {
            return "A webhook URL must not carry a user name or password; use the webhook secret.";
        }
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? literal) && !this.IsAllowedAddress(literal))
        {
            return "That webhook address is on a private or loopback network (set ANJAL_WEBHOOK_ALLOW_PRIVATE=true if that is intended).";
        }
        return null;
    }

    /// <summary>Whether a connection to this address is allowed.</summary>
    /// <param name="address">The resolved address.</param>
    public bool IsAllowedAddress(IPAddress address)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        return this.AllowPrivateAddresses || IsPublic(address);
    }

    /// <summary>
    /// Whether an address is routable on the public internet: not loopback,
    /// private, link-local, unique-local, carrier-grade NAT, multicast,
    /// unspecified or documentation space.
    /// </summary>
    /// <param name="address">The address.</param>
    public static bool IsPublic(IPAddress address)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None))
        {
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return !(b[0] == 10 ||
                     b[0] == 0 ||
                     (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||   // CGNAT 100.64/10
                     b[0] == 127 ||
                     (b[0] == 169 && b[1] == 254) ||                  // link-local
                     (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                     (b[0] == 192 && b[1] == 168) ||
                     (b[0] == 192 && b[1] == 0 && b[2] == 2) ||       // TEST-NET-1
                     (b[0] == 198 && (b[1] == 18 || b[1] == 19)) ||   // benchmarking
                     (b[0] == 198 && b[1] == 51 && b[2] == 100) ||    // TEST-NET-2
                     (b[0] == 203 && b[1] == 0 && b[2] == 113) ||     // TEST-NET-3
                     b[0] >= 224);                                    // multicast and reserved
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return false;
            }
            byte[] b = address.GetAddressBytes();
            bool uniqueLocal = (b[0] & 0xFE) == 0xFC;                // fc00::/7
            bool documentation = b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8;
            return !uniqueLocal && !documentation;
        }
        return false;
    }

    /// <summary>
    /// An <see cref="HttpClient"/> for delivering webhooks under this policy:
    /// redirects are not followed (a public endpoint could otherwise bounce
    /// the request to an internal one), and every connection is checked
    /// against <see cref="IsAllowedAddress"/> on the address actually dialled.
    /// </summary>
    /// <param name="timeout">Per-request timeout.</param>
    public HttpClient CreateClient(System.TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = System.TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, ct) =>
            {
                IPAddress[] candidates = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
                foreach (IPAddress candidate in candidates)
                {
                    if (!this.IsAllowedAddress(candidate))
                    {
                        continue;
                    }
                    var socket = new Socket(candidate.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(candidate, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException)
                    {
                        socket.Dispose();
                    }
                }
                throw new HttpRequestException($"No permitted address for webhook host {context.DnsEndPoint.Host}.");
            },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase);
}
