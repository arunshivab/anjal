namespace Anjal.Smtp;

/// <summary>
/// The TLS cipher suites Anjal offers when it connects to another mail
/// server, most preferred first, and helpers to describe a negotiated
/// session and a failed one.
/// </summary>
/// <remarks>
/// <para>
/// .NET's defaults on Linux offer only TLS 1.3 and ECDHE suites. Some
/// receiving servers accept neither - rediffmail.com's MX (27 Sep 2026)
/// accepts only DHE and static-RSA suites - so the handshake failed and the
/// message could not be delivered (DEF-064). This set keeps the modern suites
/// first and adds, in order, DHE with AES-GCM (forward secrecy) and static
/// RSA with AES-GCM (still encrypted, no forward secrecy - what Gmail and
/// Yahoo themselves used to reach Rediff). 3DES, RC4, CBC, NULL, export and
/// anonymous suites are never offered.
/// </para>
/// <para>
/// A cipher-suite policy can only be set on Linux (<see cref="System.Net.Security.CipherSuitesPolicy"/>);
/// elsewhere the platform's defaults apply. Production runs on Linux.
/// </para>
/// </remarks>
public static class TlsCipherSet
{
    private static readonly System.Net.Security.TlsCipherSuite[] Suites =
    {
        // TLS 1.3
        System.Net.Security.TlsCipherSuite.TLS_AES_256_GCM_SHA384,
        System.Net.Security.TlsCipherSuite.TLS_AES_128_GCM_SHA256,
        System.Net.Security.TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,

        // TLS 1.2, ECDHE with AEAD (forward secrecy)
        System.Net.Security.TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        System.Net.Security.TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        System.Net.Security.TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        System.Net.Security.TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        System.Net.Security.TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        System.Net.Security.TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,

        // TLS 1.2, DHE with AES-GCM (forward secrecy; older key exchange)
        System.Net.Security.TlsCipherSuite.TLS_DHE_RSA_WITH_AES_256_GCM_SHA384,
        System.Net.Security.TlsCipherSuite.TLS_DHE_RSA_WITH_AES_128_GCM_SHA256,

        // TLS 1.2, static RSA with AES-GCM (encrypted, no forward secrecy): last resort
        System.Net.Security.TlsCipherSuite.TLS_RSA_WITH_AES_256_GCM_SHA384,
        System.Net.Security.TlsCipherSuite.TLS_RSA_WITH_AES_128_GCM_SHA256,
    };

    /// <summary>The suites offered on outbound connections, most preferred first.</summary>
    public static System.Collections.Generic.IReadOnlyList<System.Net.Security.TlsCipherSuite> OutboundSuites => Suites;

    /// <summary>
    /// The policy to use for an outbound handshake: the suites above on
    /// Linux, and null (platform defaults) elsewhere.
    /// </summary>
    /// <returns>The policy, or null where a policy cannot be set.</returns>
    public static System.Net.Security.CipherSuitesPolicy? OutboundPolicy()
    {
        if (System.OperatingSystem.IsLinux())
        {
            return new System.Net.Security.CipherSuitesPolicy(Suites);
        }
        return null;
    }

    /// <summary>
    /// A short description of a negotiated session, such as
    /// <c>TLSv1.3 TLS_AES_256_GCM_SHA384</c>.
    /// </summary>
    /// <param name="ssl">An authenticated stream.</param>
    /// <returns>The protocol version and cipher suite.</returns>
    public static string Describe(System.Net.Security.SslStream ssl)
    {
        System.ArgumentNullException.ThrowIfNull(ssl);
        string version = ssl.SslProtocol switch
        {
            System.Security.Authentication.SslProtocols.Tls13 => "TLSv1.3",
            System.Security.Authentication.SslProtocols.Tls12 => "TLSv1.2",
            _ => ssl.SslProtocol.ToString(),
        };
        return $"{version} {ssl.NegotiatedCipherSuite}";
    }

    /// <summary>
    /// Every message in an exception chain, outermost first, so a log line
    /// shows the real cause instead of "see inner exception".
    /// </summary>
    /// <param name="ex">The exception.</param>
    /// <returns>The messages joined by " - ".</returns>
    public static string ErrorChain(System.Exception ex)
    {
        System.ArgumentNullException.ThrowIfNull(ex);
        var parts = new System.Collections.Generic.List<string>();
        for (System.Exception? e = ex; e is not null && parts.Count < 6; e = e.InnerException)
        {
            string m = e.Message.Replace(", see inner exception.", string.Empty, System.StringComparison.Ordinal).TrimEnd('.', ' ');
            if (m.Length > 0 && !parts.Contains(m))
            {
                parts.Add(m);
            }
        }
        return string.Join(" - ", parts);
    }
}
