using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anjal.Acme;

/// <summary>
/// Base64url encoding as used throughout ACME (RFC 4648 §5, no padding).
/// </summary>
public static class Base64Url
{
    /// <summary>Encode bytes.</summary>
    /// <param name="data">Bytes to encode.</param>
    public static string Encode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Encode a UTF-8 string.</summary>
    /// <param name="text">Text to encode.</param>
    public static string Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Encode(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Decode to bytes.</summary>
    /// <param name="text">Base64url text.</param>
    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string s = text.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}

/// <summary>
/// The ACME account key: ECDSA P-256, used to sign every request as a
/// JWS with the ES256 algorithm. Exposed as a JWK for account creation
/// and as a thumbprint for HTTP-01 key authorizations (RFC 7638).
/// </summary>
public sealed class AccountKey : IDisposable
{
    private readonly ECDsa key;

    private AccountKey(ECDsa key)
    {
        this.key = key;
    }

    /// <summary>Generate a fresh P-256 key.</summary>
    public static AccountKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Load from a PKCS#8 or EC PEM.</summary>
    /// <param name="pem">PEM text.</param>
    public static AccountKey FromPem(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        var ec = ECDsa.Create();
        ec.ImportFromPem(pem);
        return new AccountKey(ec);
    }

    /// <summary>Export as PKCS#8 PEM.</summary>
    public string ToPem() => this.key.ExportPkcs8PrivateKeyPem();

    /// <summary>
    /// The public key as a JWK object with members in the lexicographic
    /// order RFC 7638 requires for thumbprinting: crv, kty, x, y.
    /// </summary>
    public string JwkJson
    {
        get
        {
            ECParameters p = this.key.ExportParameters(false);
            string x = Base64Url.Encode(p.Q.X!);
            string y = Base64Url.Encode(p.Q.Y!);
            return "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + x + "\",\"y\":\"" + y + "\"}";
        }
    }

    /// <summary>RFC 7638 thumbprint: base64url(SHA-256(canonical JWK)).</summary>
    public string Thumbprint => Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(this.JwkJson)));

    /// <summary>ES256 signature (raw R||S, 64 bytes) over the signing input.</summary>
    /// <param name="signingInput">UTF-8 bytes of <c>protected.payload</c>.</param>
    public byte[] Sign(byte[] signingInput)
    {
        ArgumentNullException.ThrowIfNull(signingInput);
        return this.key.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Verify an ES256 signature produced by <see cref="Sign"/>. Used by tests and the fake CA.</summary>
    /// <param name="signingInput">Signing input.</param>
    /// <param name="signature">Raw R||S signature.</param>
    public bool Verify(byte[] signingInput, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(signingInput);
        ArgumentNullException.ThrowIfNull(signature);
        return this.key.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Build a verifier from a JWK JSON object (crv/kty/x/y).</summary>
    /// <param name="jwkJson">JWK text.</param>
    public static AccountKey FromJwk(string jwkJson)
    {
        ArgumentNullException.ThrowIfNull(jwkJson);
        using JsonDocument doc = JsonDocument.Parse(jwkJson);
        JsonElement root = doc.RootElement;
        var p = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = Base64Url.Decode(root.GetProperty("x").GetString() ?? string.Empty),
                Y = Base64Url.Decode(root.GetProperty("y").GetString() ?? string.Empty),
            },
        };
        var ec = ECDsa.Create(p);
        return new AccountKey(ec);
    }

    /// <inheritdoc/>
    public void Dispose() => this.key.Dispose();
}

/// <summary>
/// Builds ACME request bodies: RFC 7515 JWS in flattened JSON
/// serialization with the <c>alg</c>, <c>nonce</c>, <c>url</c> and
/// either <c>jwk</c> (new account) or <c>kid</c> (everything else)
/// protected-header members that RFC 8555 §6.2 requires.
/// </summary>
public static class Jws
{
    /// <summary>
    /// Sign a request.
    /// </summary>
    /// <param name="key">Account key.</param>
    /// <param name="url">Request URL (must match the POST target).</param>
    /// <param name="nonce">Fresh nonce from the server.</param>
    /// <param name="payloadJson">Body JSON, empty string for POST-as-GET, or null for an empty payload.</param>
    /// <param name="kid">Account URL, or null to embed the JWK instead.</param>
    public static string Sign(AccountKey key, string url, string nonce, string? payloadJson, string? kid)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(nonce);

        string header = kid is null
            ? "{\"alg\":\"ES256\",\"jwk\":" + key.JwkJson + ",\"nonce\":\"" + nonce + "\",\"url\":\"" + Escape(url) + "\"}"
            : "{\"alg\":\"ES256\",\"kid\":\"" + Escape(kid) + "\",\"nonce\":\"" + nonce + "\",\"url\":\"" + Escape(url) + "\"}";
        string protectedB64 = Base64Url.Encode(header);
        string payloadB64 = payloadJson is null ? string.Empty : Base64Url.Encode(payloadJson);
        byte[] signingInput = Encoding.ASCII.GetBytes(protectedB64 + "." + payloadB64);
        string signature = Base64Url.Encode(key.Sign(signingInput));
        return "{\"protected\":\"" + protectedB64 + "\",\"payload\":\"" + payloadB64 + "\",\"signature\":\"" + signature + "\"}";
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
