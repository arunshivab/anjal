using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Anjal.Acme;

/// <summary>Thrown when the ACME server returns a problem document or an unexpected response.</summary>
public sealed class AcmeException : Exception
{
    /// <summary>Construct.</summary>
    public AcmeException()
    {
    }

    /// <summary>Construct with a message.</summary>
    /// <param name="message">Message.</param>
    public AcmeException(string message) : base(message)
    {
    }

    /// <summary>Construct with a message and inner exception.</summary>
    /// <param name="message">Message.</param>
    /// <param name="inner">Inner exception.</param>
    public AcmeException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>The RFC 7807 problem <c>type</c> (e.g. <c>urn:ietf:params:acme:error:badNonce</c>), if any.</summary>
    public string ProblemType { get; init; } = string.Empty;

    /// <summary>HTTP status, if any.</summary>
    public int StatusCode { get; init; }
}

/// <summary>Well-known ACME directory URLs.</summary>
public static class AcmeDirectories
{
    /// <summary>Let's Encrypt production.</summary>
    public const string LetsEncrypt = "https://acme-v02.api.letsencrypt.org/directory";

    /// <summary>Let's Encrypt staging - untrusted certificates, generous rate limits. Use for rehearsal.</summary>
    public const string LetsEncryptStaging = "https://acme-staging-v02.api.letsencrypt.org/directory";
}

/// <summary>The parsed ACME directory (RFC 8555 §7.1.1).</summary>
public sealed class AcmeDirectory
{
    /// <summary>newNonce URL.</summary>
    public string NewNonce { get; init; } = string.Empty;

    /// <summary>newAccount URL.</summary>
    public string NewAccount { get; init; } = string.Empty;

    /// <summary>newOrder URL.</summary>
    public string NewOrder { get; init; } = string.Empty;

    /// <summary>Terms of service URL from <c>meta</c>, or empty.</summary>
    public string TermsOfService { get; init; } = string.Empty;
}

/// <summary>An order as returned by the server (RFC 8555 §7.1.3).</summary>
public sealed class AcmeOrder
{
    /// <summary>Order URL (from the Location header).</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>pending, ready, processing, valid or invalid.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Authorization URLs, one per identifier.</summary>
    public IReadOnlyList<string> Authorizations { get; init; } = Array.Empty<string>();

    /// <summary>Finalize URL.</summary>
    public string Finalize { get; init; } = string.Empty;

    /// <summary>Certificate URL once valid, else empty.</summary>
    public string Certificate { get; init; } = string.Empty;
}

/// <summary>An HTTP-01 challenge inside an authorization.</summary>
public sealed class AcmeChallenge
{
    /// <summary>Challenge URL.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Challenge type, e.g. <c>http-01</c>.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Token.</summary>
    public string Token { get; init; } = string.Empty;

    /// <summary>pending, processing, valid or invalid.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Error detail if invalid.</summary>
    public string Error { get; init; } = string.Empty;
}

/// <summary>An authorization (RFC 8555 §7.1.4).</summary>
public sealed class AcmeAuthorization
{
    /// <summary>Authorization URL.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>The DNS identifier.</summary>
    public string Identifier { get; init; } = string.Empty;

    /// <summary>pending, valid, invalid, expired, revoked or deactivated.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Challenges offered.</summary>
    public IReadOnlyList<AcmeChallenge> Challenges { get; init; } = Array.Empty<AcmeChallenge>();
}

/// <summary>
/// RFC 8555 client. Every method is one protocol step; the
/// <see cref="AcmeRenewalService"/> sequences them. Nonces are taken from
/// each response's <c>Replay-Nonce</c> header and a <c>badNonce</c>
/// rejection is retried once with a fresh nonce.
/// </summary>
public sealed class AcmeClient : IDisposable
{
    private readonly HttpClient http;
    private readonly AccountKey key;
    private readonly string directoryUrl;
    private readonly Action<string>? log;
    private AcmeDirectory? directory;
    private string? nonce;

    /// <summary>Construct.</summary>
    /// <param name="directoryUrl">ACME directory URL.</param>
    /// <param name="accountKey">Account key.</param>
    /// <param name="log">Optional log sink.</param>
    /// <param name="httpClient">Optional HTTP client (tests inject one); disposed with this object.</param>
    public AcmeClient(string directoryUrl, AccountKey accountKey, Action<string>? log = null, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(directoryUrl);
        ArgumentNullException.ThrowIfNull(accountKey);
        this.directoryUrl = directoryUrl;
        this.key = accountKey;
        this.log = log;
        this.http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        this.http.DefaultRequestHeaders.UserAgent.ParseAdd("Anjal.Acme/" + ModuleInfo.Version);
    }

    /// <summary>The account URL (<c>kid</c>) once the account exists.</summary>
    public string? AccountUrl { get; private set; }

    /// <summary>Fetch and cache the directory.</summary>
    /// <param name="ct">Cancellation.</param>
    public async Task<AcmeDirectory> GetDirectoryAsync(CancellationToken ct = default)
    {
        if (this.directory is not null)
        {
            return this.directory;
        }
        using HttpResponseMessage res = await this.http.GetAsync(this.directoryUrl, ct).ConfigureAwait(false);
        string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            throw new AcmeException($"Directory fetch failed: {(int)res.StatusCode} {body}") { StatusCode = (int)res.StatusCode };
        }
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        string tos = string.Empty;
        if (root.TryGetProperty("meta", out JsonElement meta) && meta.TryGetProperty("termsOfService", out JsonElement t))
        {
            tos = t.GetString() ?? string.Empty;
        }
        this.directory = new AcmeDirectory
        {
            NewNonce = root.GetProperty("newNonce").GetString() ?? string.Empty,
            NewAccount = root.GetProperty("newAccount").GetString() ?? string.Empty,
            NewOrder = root.GetProperty("newOrder").GetString() ?? string.Empty,
            TermsOfService = tos,
        };
        return this.directory;
    }

    /// <summary>
    /// Create the account for the account key, or find the existing one
    /// (the server returns the same account URL for a known key). Agrees
    /// to the terms of service.
    /// </summary>
    /// <param name="contactEmail">Contact address for expiry notices, or empty.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The account URL.</returns>
    public async Task<string> EnsureAccountAsync(string contactEmail, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contactEmail);
        if (this.AccountUrl is not null)
        {
            return this.AccountUrl;
        }
        AcmeDirectory dir = await this.GetDirectoryAsync(ct).ConfigureAwait(false);
        string payload = contactEmail.Length == 0
            ? "{\"termsOfServiceAgreed\":true}"
            : "{\"termsOfServiceAgreed\":true,\"contact\":[\"mailto:" + contactEmail + "\"]}";
        (HttpStatusCode status, string body, HttpResponseHeaders headers) = await this.PostAsync(dir.NewAccount, payload, useJwk: true, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.Created && status != HttpStatusCode.OK)
        {
            throw Problem("newAccount", status, body);
        }
        this.AccountUrl = headers.Location ?? throw new AcmeException("newAccount response had no Location header.");
        this.log?.Invoke($"ACME account: {this.AccountUrl}");
        return this.AccountUrl;
    }

    /// <summary>Create an order for the given DNS names.</summary>
    /// <param name="domains">DNS identifiers.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<AcmeOrder> NewOrderAsync(IReadOnlyList<string> domains, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domains);
        if (domains.Count == 0)
        {
            throw new ArgumentException("At least one domain is required.", nameof(domains));
        }
        AcmeDirectory dir = await this.GetDirectoryAsync(ct).ConfigureAwait(false);
        var sb = new StringBuilder("{\"identifiers\":[");
        for (int i = 0; i < domains.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append("{\"type\":\"dns\",\"value\":\"").Append(domains[i].ToLowerInvariant()).Append("\"}");
        }
        sb.Append("]}");
        (HttpStatusCode status, string body, HttpResponseHeaders headers) = await this.PostAsync(dir.NewOrder, sb.ToString(), useJwk: false, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.Created && status != HttpStatusCode.OK)
        {
            throw Problem("newOrder", status, body);
        }
        return ParseOrder(headers.Location ?? string.Empty, body);
    }

    /// <summary>Re-fetch an order (POST-as-GET).</summary>
    /// <param name="orderUrl">Order URL.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<AcmeOrder> GetOrderAsync(string orderUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderUrl);
        (HttpStatusCode status, string body, _) = await this.PostAsync(orderUrl, string.Empty, useJwk: false, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
        {
            throw Problem("order", status, body);
        }
        return ParseOrder(orderUrl, body);
    }

    /// <summary>Fetch an authorization (POST-as-GET).</summary>
    /// <param name="authzUrl">Authorization URL.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<AcmeAuthorization> GetAuthorizationAsync(string authzUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authzUrl);
        (HttpStatusCode status, string body, _) = await this.PostAsync(authzUrl, string.Empty, useJwk: false, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
        {
            throw Problem("authorization", status, body);
        }
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        var challenges = new List<AcmeChallenge>();
        if (root.TryGetProperty("challenges", out JsonElement arr))
        {
            foreach (JsonElement c in arr.EnumerateArray())
            {
                challenges.Add(ParseChallenge(c));
            }
        }
        return new AcmeAuthorization
        {
            Url = authzUrl,
            Identifier = root.TryGetProperty("identifier", out JsonElement id) && id.TryGetProperty("value", out JsonElement v) ? v.GetString() ?? string.Empty : string.Empty,
            Status = root.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? string.Empty : string.Empty,
            Challenges = challenges,
        };
    }

    /// <summary>The key authorization for an HTTP-01 token: <c>token.thumbprint</c>.</summary>
    /// <param name="token">Challenge token.</param>
    public string KeyAuthorization(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token + "." + this.key.Thumbprint;
    }

    /// <summary>Tell the server the challenge is ready to be validated.</summary>
    /// <param name="challengeUrl">Challenge URL.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<AcmeChallenge> RespondToChallengeAsync(string challengeUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challengeUrl);
        (HttpStatusCode status, string body, _) = await this.PostAsync(challengeUrl, "{}", useJwk: false, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
        {
            throw Problem("challenge", status, body);
        }
        using JsonDocument doc = JsonDocument.Parse(body);
        return ParseChallenge(doc.RootElement);
    }

    /// <summary>Re-fetch a challenge to see whether validation completed.</summary>
    /// <param name="challengeUrl">Challenge URL.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<AcmeChallenge> GetChallengeAsync(string challengeUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challengeUrl);
        (HttpStatusCode status, string body, _) = await this.PostAsync(challengeUrl, string.Empty, useJwk: false, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
        {
            throw Problem("challenge", status, body);
        }
        using JsonDocument doc = JsonDocument.Parse(body);
        return ParseChallenge(doc.RootElement);
    }

    /// <summary>Submit the CSR.</summary>
    /// <param name="finalizeUrl">Order finalize URL.</param>
    /// <param name="csrDer">DER-encoded PKCS#10 request.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<AcmeOrder> FinalizeAsync(string finalizeUrl, byte[] csrDer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(finalizeUrl);
        ArgumentNullException.ThrowIfNull(csrDer);
        string payload = "{\"csr\":\"" + Base64Url.Encode(csrDer) + "\"}";
        (HttpStatusCode status, string body, HttpResponseHeaders headers) = await this.PostAsync(finalizeUrl, payload, useJwk: false, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
        {
            throw Problem("finalize", status, body);
        }
        return ParseOrder(headers.Location ?? string.Empty, body);
    }

    /// <summary>Download the issued certificate chain as PEM.</summary>
    /// <param name="certificateUrl">Certificate URL from the valid order.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string> DownloadCertificateAsync(string certificateUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(certificateUrl);
        (HttpStatusCode status, string body, _) = await this.PostAsync(certificateUrl, string.Empty, useJwk: false, ct, accept: "application/pem-certificate-chain").ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
        {
            throw Problem("certificate", status, body);
        }
        if (!body.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
        {
            throw new AcmeException("Certificate download did not return PEM.");
        }
        return body;
    }

    /// <summary>
    /// Build a PKCS#10 CSR for the domains with the given key. The first
    /// domain is the subject CN; all domains go in the SAN extension.
    /// </summary>
    /// <param name="domains">DNS names.</param>
    /// <param name="certificateKey">The certificate's private key (ECDSA or RSA).</param>
    public static byte[] BuildCsr(IReadOnlyList<string> domains, AsymmetricAlgorithm certificateKey)
    {
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(certificateKey);
        if (domains.Count == 0)
        {
            throw new ArgumentException("At least one domain is required.", nameof(domains));
        }
        var subject = new X500DistinguishedName("CN=" + domains[0]);
        CertificateRequest req = certificateKey switch
        {
            ECDsa ec => new CertificateRequest(subject, ec, HashAlgorithmName.SHA256),
            RSA rsa => new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            _ => throw new ArgumentException("Key must be ECDsa or RSA.", nameof(certificateKey)),
        };
        var san = new SubjectAlternativeNameBuilder();
        foreach (string d in domains)
        {
            san.AddDnsName(d);
        }
        req.CertificateExtensions.Add(san.Build());
        return req.CreateSigningRequest();
    }

    private async Task<(HttpStatusCode Status, string Body, HttpResponseHeaders Headers)> PostAsync(
        string url, string payload, bool useJwk, CancellationToken ct, string accept = "application/json")
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string n = await this.TakeNonceAsync(ct).ConfigureAwait(false);
            string jws = Jws.Sign(this.key, url, n, payload, useJwk ? null : this.AccountUrl);
            using var content = new StringContent(jws, Encoding.UTF8, "application/jose+json");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            req.Headers.Accept.ParseAdd(accept);
            using HttpResponseMessage res = await this.http.SendAsync(req, ct).ConfigureAwait(false);
            if (res.Headers.TryGetValues("Replay-Nonce", out IEnumerable<string>? nonces))
            {
                this.nonce = nonces.FirstOrDefault();
            }
            string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (res.StatusCode == HttpStatusCode.BadRequest && body.Contains("urn:ietf:params:acme:error:badNonce", StringComparison.Ordinal) && attempt == 0)
            {
                this.log?.Invoke("ACME: bad nonce, retrying with a fresh one.");
                continue;
            }
            return (res.StatusCode, body, new HttpResponseHeaders(res.Headers.Location?.ToString(), res.Headers.RetryAfter?.Delta));
        }
        throw new AcmeException("ACME request failed after nonce retry.");
    }

    private async Task<string> TakeNonceAsync(CancellationToken ct)
    {
        string? n = this.nonce;
        this.nonce = null;
        if (n is not null)
        {
            return n;
        }
        AcmeDirectory dir = await this.GetDirectoryAsync(ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Head, dir.NewNonce);
        using HttpResponseMessage res = await this.http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.Headers.TryGetValues("Replay-Nonce", out IEnumerable<string>? nonces))
        {
            string? fresh = nonces.FirstOrDefault();
            if (!string.IsNullOrEmpty(fresh))
            {
                return fresh;
            }
        }
        throw new AcmeException("newNonce did not return a Replay-Nonce header.");
    }

    private static AcmeOrder ParseOrder(string url, string body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        var authz = new List<string>();
        if (root.TryGetProperty("authorizations", out JsonElement arr))
        {
            foreach (JsonElement a in arr.EnumerateArray())
            {
                authz.Add(a.GetString() ?? string.Empty);
            }
        }
        return new AcmeOrder
        {
            Url = url,
            Status = root.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? string.Empty : string.Empty,
            Authorizations = authz,
            Finalize = root.TryGetProperty("finalize", out JsonElement f) ? f.GetString() ?? string.Empty : string.Empty,
            Certificate = root.TryGetProperty("certificate", out JsonElement c) ? c.GetString() ?? string.Empty : string.Empty,
        };
    }

    private static AcmeChallenge ParseChallenge(JsonElement c)
    {
        string error = string.Empty;
        if (c.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("detail", out JsonElement d))
        {
            error = d.GetString() ?? string.Empty;
        }
        return new AcmeChallenge
        {
            Url = c.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? string.Empty : string.Empty,
            Type = c.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty,
            Token = c.TryGetProperty("token", out JsonElement tk) ? tk.GetString() ?? string.Empty : string.Empty,
            Status = c.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? string.Empty : string.Empty,
            Error = error,
        };
    }

    private static AcmeException Problem(string step, HttpStatusCode status, string body)
    {
        string type = string.Empty;
        string detail = body;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("type", out JsonElement t))
            {
                type = t.GetString() ?? string.Empty;
            }
            if (doc.RootElement.TryGetProperty("detail", out JsonElement d))
            {
                detail = d.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body: report as-is.
        }
        return new AcmeException($"ACME {step} failed: {(int)status} {type} {detail}".Trim())
        {
            ProblemType = type,
            StatusCode = (int)status,
        };
    }

    /// <inheritdoc/>
    public void Dispose() => this.http.Dispose();

    /// <summary>The response headers the client cares about.</summary>
    private sealed record HttpResponseHeaders(string? Location, TimeSpan? RetryAfter);
}
