using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Anjal.Acme.Tests;

/// <summary>
/// A small but honest ACME v2 server: directory, nonces (single use),
/// JWS verification (ES256, jwk or kid), account creation, orders with one
/// http-01 authorization per identifier, real challenge validation by
/// fetching the token over HTTP, CSR-based issuance signed by a test root,
/// and PEM chain download. Every deviation from the protocol the client
/// might make becomes a 4xx here, which is what the tests want.
/// </summary>
internal sealed class FakeAcmeServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource cts = new();
    private readonly ConcurrentDictionary<string, byte> nonces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> accounts = new(StringComparer.Ordinal); // kid -> jwk json
    private readonly ConcurrentDictionary<string, Order> orders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Authz> authzs = new(StringComparer.Ordinal);
    private readonly X509Certificate2 root;
    private readonly int challengePort;
    private Task? runTask;
    private int seq;

    public FakeAcmeServer(int challengePort)
    {
        this.challengePort = challengePort;
        int port = FreePort();
        this.BaseUrl = $"http://127.0.0.1:{port}";
        this.listener.Prefixes.Add(this.BaseUrl + "/");
        using ECDsa rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=Anjal Test Root", rootKey, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        this.root = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    public string BaseUrl { get; }

    public string DirectoryUrl => this.BaseUrl + "/directory";

    public X509Certificate2 Root => this.root;

    /// <summary>Set to make challenge validation fail regardless of the token served.</summary>
    public bool FailValidation { get; set; }

    /// <summary>Number of newAccount calls (to check the client re-uses the account).</summary>
    public int NewAccountCalls { get; private set; }

    /// <summary>Number of JWS requests rejected for a bad or reused nonce.</summary>
    public int BadNonceRejections { get; private set; }

    /// <summary>Validity period to issue; default 90 days.</summary>
    public TimeSpan Validity { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Number of certificates issued.</summary>
    public int Issued { get; private set; }

    public void Start()
    {
        this.listener.Start();
        this.runTask = Task.Run(this.LoopAsync);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task LoopAsync()
    {
        while (!this.cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await this.listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await this.HandleAsync(ctx).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    try
                    {
                        Problem(ctx, 500, "urn:ietf:params:acme:error:serverInternal", ex.ToString());
                    }
                    catch (Exception)
                    {
                    }
                }
            });
        }
    }

    private string IssueNonce()
    {
        string n = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        this.nonces[n] = 0;
        return n;
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url!.AbsolutePath;
        ctx.Response.Headers["Replay-Nonce"] = this.IssueNonce();

        if (path == "/directory" && ctx.Request.HttpMethod == "GET")
        {
            Json(ctx, 200, "{\"newNonce\":\"" + this.BaseUrl + "/new-nonce\",\"newAccount\":\"" + this.BaseUrl + "/new-account\",\"newOrder\":\"" + this.BaseUrl + "/new-order\",\"meta\":{\"termsOfService\":\"" + this.BaseUrl + "/tos\"}}");
            return;
        }
        if (path == "/new-nonce")
        {
            ctx.Response.StatusCode = ctx.Request.HttpMethod == "HEAD" ? 200 : 204;
            ctx.Response.Close();
            return;
        }
        if (ctx.Request.HttpMethod != "POST")
        {
            Problem(ctx, 405, "urn:ietf:params:acme:error:malformed", "POST required");
            return;
        }

        string body;
        using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
        {
            body = await sr.ReadToEndAsync().ConfigureAwait(false);
        }
        // DEF-049: Let's Encrypt (Boulder) compares the header exactly - a
        // "; charset=utf-8" parameter is refused with 400 malformed. The fake
        // used to accept any value that merely started with the media type,
        // which is how the client's wrong header passed every test.
        if (!string.Equals(ctx.Request.ContentType, "application/jose+json", StringComparison.Ordinal))
        {
            Problem(ctx, 415, "urn:ietf:params:acme:error:malformed", "Content-Type must be application/jose+json");
            return;
        }

        // ---- JWS verification ----
        using JsonDocument jws = JsonDocument.Parse(body);
        string protectedB64 = jws.RootElement.GetProperty("protected").GetString()!;
        string payloadB64 = jws.RootElement.GetProperty("payload").GetString()!;
        string signatureB64 = jws.RootElement.GetProperty("signature").GetString()!;
        using JsonDocument header = JsonDocument.Parse(Base64Url.Decode(protectedB64));
        JsonElement h = header.RootElement;
        if (h.GetProperty("alg").GetString() != "ES256")
        {
            Problem(ctx, 400, "urn:ietf:params:acme:error:badSignatureAlgorithm", "ES256 only");
            return;
        }
        string nonce = h.GetProperty("nonce").GetString() ?? string.Empty;
        if (!this.nonces.TryRemove(nonce, out _))
        {
            this.BadNonceRejections++;
            Problem(ctx, 400, "urn:ietf:params:acme:error:badNonce", "bad or reused nonce");
            return;
        }
        string url = h.GetProperty("url").GetString() ?? string.Empty;
        if (url != this.BaseUrl + path)
        {
            Problem(ctx, 400, "urn:ietf:params:acme:error:malformed", $"url mismatch: {url} vs {this.BaseUrl + path}");
            return;
        }
        string jwkJson;
        string? kid = null;
        if (h.TryGetProperty("jwk", out JsonElement jwk))
        {
            if (path != "/new-account")
            {
                Problem(ctx, 400, "urn:ietf:params:acme:error:malformed", "jwk only allowed on newAccount");
                return;
            }
            jwkJson = jwk.GetRawText();
        }
        else
        {
            kid = h.GetProperty("kid").GetString();
            if (kid is null || !this.accounts.TryGetValue(kid, out string? stored))
            {
                Problem(ctx, 400, "urn:ietf:params:acme:error:accountDoesNotExist", "unknown kid");
                return;
            }
            jwkJson = stored;
        }
        using (AccountKey verifier = AccountKey.FromJwk(jwkJson))
        {
            byte[] signingInput = Encoding.ASCII.GetBytes(protectedB64 + "." + payloadB64);
            if (!verifier.Verify(signingInput, Base64Url.Decode(signatureB64)))
            {
                Problem(ctx, 400, "urn:ietf:params:acme:error:malformed", "bad signature");
                return;
            }
        }
        string payload = payloadB64.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Base64Url.Decode(payloadB64));

        // ---- Endpoints ----
        if (path == "/new-account")
        {
            this.NewAccountCalls++;
            using AccountKey k = AccountKey.FromJwk(jwkJson);
            string accountUrl = this.BaseUrl + "/acct/" + k.Thumbprint;
            bool existed = this.accounts.ContainsKey(accountUrl);
            this.accounts[accountUrl] = NormalizeJwk(jwkJson);
            ctx.Response.Headers["Location"] = accountUrl;
            Json(ctx, existed ? 200 : 201, "{\"status\":\"valid\"}");
            return;
        }
        if (path == "/new-order")
        {
            using JsonDocument p = JsonDocument.Parse(payload);
            var ids = new List<string>();
            foreach (JsonElement id in p.RootElement.GetProperty("identifiers").EnumerateArray())
            {
                ids.Add(id.GetProperty("value").GetString()!);
            }
            var order = new Order { Id = Interlocked.Increment(ref this.seq).ToString(System.Globalization.CultureInfo.InvariantCulture), Identifiers = ids, Kid = kid! };
            foreach (string id in ids)
            {
                var a = new Authz { Id = Interlocked.Increment(ref this.seq).ToString(System.Globalization.CultureInfo.InvariantCulture), Identifier = id, Token = Base64Url.Encode(RandomNumberGenerator.GetBytes(24)), Kid = kid! };
                this.authzs[a.Id] = a;
                order.AuthzIds.Add(a.Id);
            }
            this.orders[order.Id] = order;
            ctx.Response.Headers["Location"] = this.BaseUrl + "/order/" + order.Id;
            Json(ctx, 201, this.OrderJson(order));
            return;
        }
        if (path.StartsWith("/order/", StringComparison.Ordinal))
        {
            Order o = this.orders[path.Substring("/order/".Length)];
            Json(ctx, 200, this.OrderJson(o));
            return;
        }
        if (path.StartsWith("/authz/", StringComparison.Ordinal))
        {
            Authz a = this.authzs[path.Substring("/authz/".Length)];
            Json(ctx, 200, this.AuthzJson(a));
            return;
        }
        if (path.StartsWith("/chall/", StringComparison.Ordinal))
        {
            Authz a = this.authzs[path.Substring("/chall/".Length)];
            if (payload == "{}" && a.Status == "pending")
            {
                a.Status = "processing";
                _ = Task.Run(() => this.ValidateAsync(a));
            }
            Json(ctx, 200, this.ChallengeJson(a));
            return;
        }
        if (path.StartsWith("/finalize/", StringComparison.Ordinal))
        {
            Order o = this.orders[path.Substring("/finalize/".Length)];
            if (o.AuthzIds.Any(id => this.authzs[id].Status != "valid"))
            {
                Problem(ctx, 403, "urn:ietf:params:acme:error:orderNotReady", "authorizations not valid");
                return;
            }
            using JsonDocument p = JsonDocument.Parse(payload);
            byte[] csr = Base64Url.Decode(p.RootElement.GetProperty("csr").GetString()!);
            CertificateRequest req = CertificateRequest.LoadSigningRequest(csr, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
            // Check SANs match the order.
            var sans = new List<string>();
            foreach (X509Extension ext in req.CertificateExtensions)
            {
                if (ext is X509SubjectAlternativeNameExtension san)
                {
                    sans.AddRange(san.EnumerateDnsNames());
                }
            }
            if (!o.Identifiers.All(i => sans.Contains(i, StringComparer.OrdinalIgnoreCase)))
            {
                Problem(ctx, 400, "urn:ietf:params:acme:error:badCSR", "CSR SANs do not match order");
                return;
            }
            byte[] serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7F;
            using ECDsa rootKey = this.root.GetECDsaPrivateKey()!;
            using X509Certificate2 leaf = req.Create(this.root.SubjectName, X509SignatureGenerator.CreateForECDsa(rootKey), DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow + this.Validity, serial);
            o.ChainPem = leaf.ExportCertificatePem() + "\n" + this.root.ExportCertificatePem() + "\n";
            o.Status = "valid";
            this.Issued++;
            ctx.Response.Headers["Location"] = this.BaseUrl + "/order/" + o.Id;
            Json(ctx, 200, this.OrderJson(o));
            return;
        }
        if (path.StartsWith("/cert/", StringComparison.Ordinal))
        {
            Order o = this.orders[path.Substring("/cert/".Length)];
            byte[] bytes = Encoding.ASCII.GetBytes(o.ChainPem);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/pem-certificate-chain";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
            return;
        }
        Problem(ctx, 404, "urn:ietf:params:acme:error:malformed", "no such resource");
    }

    private async Task ValidateAsync(Authz a)
    {
        try
        {
            await Task.Delay(50).ConfigureAwait(false);
            if (this.FailValidation)
            {
                a.Status = "invalid";
                a.Error = "Validation forced to fail by test";
                return;
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string got = await http.GetStringAsync($"http://127.0.0.1:{this.challengePort}/.well-known/acme-challenge/{a.Token}").ConfigureAwait(false);
            using AccountKey k = AccountKey.FromJwk(this.accounts[a.Kid]);
            string expected = a.Token + "." + k.Thumbprint;
            if (got.Trim() == expected)
            {
                a.Status = "valid";
            }
            else
            {
                a.Status = "invalid";
                a.Error = $"Key authorization mismatch: got '{got}'";
            }
        }
        catch (Exception ex)
        {
            a.Status = "invalid";
            a.Error = "Fetch failed: " + ex.Message;
        }
    }

    private static string NormalizeJwk(string jwkJson)
    {
        using JsonDocument d = JsonDocument.Parse(jwkJson);
        JsonElement r = d.RootElement;
        return "{\"crv\":\"" + r.GetProperty("crv").GetString() + "\",\"kty\":\"" + r.GetProperty("kty").GetString() + "\",\"x\":\"" + r.GetProperty("x").GetString() + "\",\"y\":\"" + r.GetProperty("y").GetString() + "\"}";
    }

    private string OrderJson(Order o)
    {
        string authz = string.Join(",", o.AuthzIds.Select(id => "\"" + this.BaseUrl + "/authz/" + id + "\""));
        string cert = o.Status == "valid" ? ",\"certificate\":\"" + this.BaseUrl + "/cert/" + o.Id + "\"" : string.Empty;
        return "{\"status\":\"" + o.Status + "\",\"authorizations\":[" + authz + "],\"finalize\":\"" + this.BaseUrl + "/finalize/" + o.Id + "\"" + cert + "}";
    }

    private string AuthzJson(Authz a) =>
        "{\"identifier\":{\"type\":\"dns\",\"value\":\"" + a.Identifier + "\"},\"status\":\"" + (a.Status == "valid" ? "valid" : "pending") + "\",\"challenges\":[" + this.ChallengeJson(a) + "]}";

    private string ChallengeJson(Authz a)
    {
        string error = a.Status == "invalid" ? ",\"error\":{\"type\":\"urn:ietf:params:acme:error:unauthorized\",\"detail\":\"" + a.Error.Replace("\"", "'", StringComparison.Ordinal) + "\"}" : string.Empty;
        return "{\"type\":\"http-01\",\"url\":\"" + this.BaseUrl + "/chall/" + a.Id + "\",\"token\":\"" + a.Token + "\",\"status\":\"" + a.Status + "\"" + error + "}";
    }

    private static void Json(HttpListenerContext ctx, int status, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static void Problem(HttpListenerContext ctx, int status, string type, string detail)
    {
        string json = "{\"type\":\"" + type + "\",\"detail\":\"" + detail.Replace("\\", "/", StringComparison.Ordinal).Replace("\"", "'", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal) + "\"}";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        this.cts.Cancel();
        try
        {
            this.listener.Stop();
            this.listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        this.root.Dispose();
        this.cts.Dispose();
    }

    private sealed class Order
    {
        public string Id { get; set; } = string.Empty;

        public string Kid { get; set; } = string.Empty;

        public List<string> Identifiers { get; set; } = new();

        public List<string> AuthzIds { get; } = new();

        public string Status { get; set; } = "pending";

        public string ChainPem { get; set; } = string.Empty;
    }

    private sealed class Authz
    {
        public string Id { get; set; } = string.Empty;

        public string Kid { get; set; } = string.Empty;

        public string Identifier { get; set; } = string.Empty;

        public string Token { get; set; } = string.Empty;

        public string Status { get; set; } = "pending";

        public string Error { get; set; } = string.Empty;
    }
}
