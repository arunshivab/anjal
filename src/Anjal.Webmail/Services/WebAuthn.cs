using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anjal.Webmail.Services;

/// <summary>A passkey kept for a mailbox (rc.13).</summary>
/// <param name="Id">The credential's id, base64url.</param>
/// <param name="PublicKey">Its COSE public key, base64url.</param>
/// <param name="SignCount">The authenticator's counter at last use.</param>
/// <param name="Name">What the person sees, for example "Windows, Chrome".</param>
/// <param name="Added">When it was added.</param>
/// <param name="LastUsed">When it was last used, or null.</param>
public sealed record PasskeyRecord(string Id, string PublicKey, uint SignCount, string Name, DateTimeOffset Added, DateTimeOffset? LastUsed);

/// <summary>
/// Passkeys (WebAuthn level 2) checked on the server (rc.13): a new passkey's
/// public key is read from what the browser returns, and a sign-in is
/// accepted only when its signature verifies with that key, for this
/// server's name and this page's origin, against a challenge used once.
/// Attestation is not asked for: the person adding a passkey is already
/// signed in, so where the key came from adds nothing.
/// </summary>
public static class WebAuthn
{
    /// <summary>COSE algorithm: ECDSA with P-256 and SHA-256.</summary>
    public const int Es256 = -7;

    /// <summary>COSE algorithm: RSASSA-PKCS1-v1_5 with SHA-256.</summary>
    public const int Rs256 = -257;

    /// <summary>
    /// Read a new passkey from the browser's answer to
    /// navigator.credentials.create. Returns the passkey, or the reason it
    /// was refused.
    /// </summary>
    /// <param name="clientDataJson">clientDataJSON, base64url.</param>
    /// <param name="attestationObject">attestationObject, base64url.</param>
    /// <param name="challenge">The challenge that was sent.</param>
    /// <param name="origin">This page's origin, for example https://mail.example.in.</param>
    /// <param name="rpId">This server's name, for example mail.example.in.</param>
    /// <param name="name">The name to give it.</param>
    /// <param name="now">Now.</param>
    /// <returns>The passkey or an error.</returns>
    public static (PasskeyRecord? Passkey, string? Error) Register(string clientDataJson, string attestationObject, byte[] challenge, string origin, string rpId, string name, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(rpId);
        byte[]? client = FromBase64Url(clientDataJson);
        byte[]? attestation = FromBase64Url(attestationObject);
        if (client is null || attestation is null)
        {
            return (null, "The browser's answer could not be read.");
        }
        if (CheckClientData(client, "webauthn.create", challenge, origin) is string bad)
        {
            return (null, bad);
        }
        try
        {
            var reader = new CborReader(attestation);
            if (reader.Read() is not Dictionary<object, object?> map || !map.TryGetValue("authData", out object? ad) || ad is not byte[] authData)
            {
                return (null, "The browser's answer had no authenticator data.");
            }
            if (CheckAuthData(authData, rpId, out byte flags) is string badAuth)
            {
                return (null, badAuth);
            }
            if ((flags & 0x40) == 0 || authData.Length < 55)
            {
                return (null, "The browser did not return a new key.");
            }
            uint count = BinaryPrimitives.ReadUInt32BigEndian(authData.AsSpan(33, 4));
            int idLength = BinaryPrimitives.ReadUInt16BigEndian(authData.AsSpan(53, 2));
            if (55 + idLength > authData.Length)
            {
                return (null, "The key's id was cut short.");
            }
            byte[] credentialId = authData[55..(55 + idLength)];
            var keyReader = new CborReader(authData, 55 + idLength);
            keyReader.Read();
            byte[] cose = authData[(55 + idLength)..keyReader.Position];
            if (KeyAlgorithm(cose) is null)
            {
                return (null, "That kind of passkey is not supported. Use one made by this device, a phone or a security key.");
            }
            return (new PasskeyRecord(ToBase64Url(credentialId), ToBase64Url(cose), count, name, now, null), null);
        }
        catch (FormatException)
        {
            return (null, "The browser's answer could not be read.");
        }
    }

    /// <summary>
    /// Check a sign-in with a passkey: the browser's answer to
    /// navigator.credentials.get. Returns the counter to keep, or an error.
    /// </summary>
    /// <param name="passkey">The stored passkey whose id was returned.</param>
    /// <param name="clientDataJson">clientDataJSON, base64url.</param>
    /// <param name="authenticatorData">authenticatorData, base64url.</param>
    /// <param name="signature">signature, base64url.</param>
    /// <param name="challenge">The challenge that was sent.</param>
    /// <param name="origin">This page's origin.</param>
    /// <param name="rpId">This server's name.</param>
    /// <returns>The new counter, or an error.</returns>
    public static (uint SignCount, string? Error) Verify(PasskeyRecord passkey, string clientDataJson, string authenticatorData, string signature, byte[] challenge, string origin, string rpId)
    {
        ArgumentNullException.ThrowIfNull(passkey);
        ArgumentNullException.ThrowIfNull(challenge);
        byte[]? client = FromBase64Url(clientDataJson);
        byte[]? authData = FromBase64Url(authenticatorData);
        byte[]? sig = FromBase64Url(signature);
        byte[]? cose = FromBase64Url(passkey.PublicKey);
        if (client is null || authData is null || sig is null || cose is null)
        {
            return (0, "The browser's answer could not be read.");
        }
        if (CheckClientData(client, "webauthn.get", challenge, origin) is string bad)
        {
            return (0, bad);
        }
        if (CheckAuthData(authData, rpId, out _) is string badAuth)
        {
            return (0, badAuth);
        }
        uint count = BinaryPrimitives.ReadUInt32BigEndian(authData.AsSpan(33, 4));
        if (count != 0 && passkey.SignCount != 0 && count <= passkey.SignCount)
        {
            return (0, "This passkey's counter went backwards, which happens when a key has been copied. It was not accepted.");
        }
        byte[] signed = authData.Concat(SHA256.HashData(client)).ToArray();
        try
        {
            if (!VerifySignature(cose, signed, sig))
            {
                return (0, "The passkey's signature did not match.");
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return (0, "The passkey's signature did not match.");
        }
        return (count, null);
    }

    /// <summary>The COSE algorithm of a stored key, or null when it is not one this server checks.</summary>
    /// <param name="cose">The COSE key.</param>
    /// <returns>-7, -257, or null.</returns>
    public static int? KeyAlgorithm(byte[] cose)
    {
        try
        {
            if (new CborReader(cose).Read() is not Dictionary<object, object?> key)
            {
                return null;
            }
            long kty = key.TryGetValue(1L, out object? k) && k is long kv ? kv : 0;
            long alg = key.TryGetValue(3L, out object? a) && a is long av ? av : 0;
            if (kty == 2 && alg == Es256 && key.TryGetValue(-2L, out object? x) && x is byte[] xb && xb.Length == 32 && key.TryGetValue(-3L, out object? y) && y is byte[] yb && yb.Length == 32)
            {
                return Es256;
            }
            if (kty == 3 && alg == Rs256 && key.TryGetValue(-1L, out object? n) && n is byte[] && key.TryGetValue(-2L, out object? e) && e is byte[])
            {
                return Rs256;
            }
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Encode bytes as base64url, without padding.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The text.</returns>
    public static string ToBase64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decode base64url (with or without padding); null when it is not.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The bytes, or null.</returns>
    public static byte[]? FromBase64Url(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 65536)
        {
            return null;
        }
        string s = text.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? CheckClientData(byte[] client, string type, byte[] challenge, string origin)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(client);
            JsonElement root = doc.RootElement;
            string? t = root.TryGetProperty("type", out JsonElement te) ? te.GetString() : null;
            string? c = root.TryGetProperty("challenge", out JsonElement ce) ? ce.GetString() : null;
            string? o = root.TryGetProperty("origin", out JsonElement oe) ? oe.GetString() : null;
            if (!string.Equals(t, type, StringComparison.Ordinal))
            {
                return "The browser's answer was for something else.";
            }
            byte[]? got = FromBase64Url(c);
            if (got is null || !CryptographicOperations.FixedTimeEquals(got, challenge))
            {
                return "The request had expired. Try again.";
            }
            if (!string.Equals(o, origin, StringComparison.OrdinalIgnoreCase))
            {
                return "The passkey was used from a different web address.";
            }
            return null;
        }
        catch (JsonException)
        {
            return "The browser's answer could not be read.";
        }
    }

    private static string? CheckAuthData(byte[] authData, string rpId, out byte flags)
    {
        flags = 0;
        if (authData.Length < 37)
        {
            return "The authenticator's data was cut short.";
        }
        if (!CryptographicOperations.FixedTimeEquals(authData.AsSpan(0, 32), SHA256.HashData(Encoding.UTF8.GetBytes(rpId))))
        {
            return "The passkey belongs to a different server.";
        }
        flags = authData[32];
        if ((flags & 0x01) == 0)
        {
            return "The authenticator did not confirm someone was present.";
        }
        return null;
    }

    private static bool VerifySignature(byte[] cose, byte[] data, byte[] signature)
    {
        var key = (Dictionary<object, object?>)new CborReader(cose).Read()!;
        int? alg = KeyAlgorithm(cose);
        if (alg == Es256)
        {
            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = (byte[])key[-2L]!, Y = (byte[])key[-3L]! },
            });
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        if (alg == Rs256)
        {
            using var rsa = RSA.Create(new RSAParameters { Modulus = (byte[])key[-1L]!, Exponent = (byte[])key[-2L]! });
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        return false;
    }
}

/// <summary>
/// Just enough CBOR (RFC 8949) to read what browsers send for passkeys:
/// integers, byte and text strings, arrays, maps, tags, and simple values.
/// Integers become <see cref="long"/>; maps become dictionaries.
/// </summary>
public sealed class CborReader
{
    private const int MaxDepth = 16;

    private readonly byte[] data;

    /// <summary>Construct.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="start">Where to begin.</param>
    public CborReader(byte[] data, int start = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        this.data = data;
        this.Position = start;
    }

    /// <summary>Where the next item begins.</summary>
    public int Position { get; private set; }

    /// <summary>Read one item.</summary>
    /// <returns>long, byte[], string, List, Dictionary, bool or null.</returns>
    public object? Read() => this.Read(0);

    private object? Read(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException("CBOR nested too deeply.");
        }
        byte initial = this.Next();
        int major = initial >> 5;
        int info = initial & 0x1F;
        if (major == 7)
        {
            return info switch
            {
                20 => false,
                21 => true,
                22 or 23 => null,
                25 => this.Skip(2),
                26 => this.Skip(4),
                27 => this.Skip(8),
                _ => throw new FormatException("Unsupported CBOR simple value."),
            };
        }
        ulong arg = this.Argument(info);
        switch (major)
        {
            case 0:
                return arg > long.MaxValue ? throw new FormatException("CBOR integer too large.") : (long)arg;
            case 1:
                return arg > long.MaxValue ? throw new FormatException("CBOR integer too large.") : -1 - (long)arg;
            case 2:
                return this.Take(arg);
            case 3:
                return Encoding.UTF8.GetString(this.Take(arg));
            case 4:
                {
                    var list = new List<object?>();
                    for (ulong i = 0; i < arg; i++)
                    {
                        list.Add(this.Read(depth + 1));
                    }
                    return list;
                }
            case 5:
                {
                    var map = new Dictionary<object, object?>();
                    for (ulong i = 0; i < arg; i++)
                    {
                        object key = this.Read(depth + 1) ?? throw new FormatException("CBOR map key is null.");
                        map[key] = this.Read(depth + 1);
                    }
                    return map;
                }
            default:
                return this.Read(depth + 1);
        }
    }

    private ulong Argument(int info)
    {
        if (info < 24)
        {
            return (ulong)info;
        }
        int bytes = info switch { 24 => 1, 25 => 2, 26 => 4, 27 => 8, _ => throw new FormatException("Indefinite CBOR lengths are not supported.") };
        ulong value = 0;
        for (int i = 0; i < bytes; i++)
        {
            value = (value << 8) | this.Next();
        }
        return value;
    }

    private byte Next() => this.Position < this.data.Length ? this.data[this.Position++] : throw new FormatException("CBOR ends early.");

    private byte[] Take(ulong count)
    {
        if (count > (ulong)(this.data.Length - this.Position))
        {
            throw new FormatException("CBOR ends early.");
        }
        byte[] result = this.data[this.Position..(this.Position + (int)count)];
        this.Position += (int)count;
        return result;
    }

    private object? Skip(int count)
    {
        this.Take((ulong)count);
        return null;
    }
}
