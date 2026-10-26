using System.Security.Cryptography;
using System.Text;

namespace Anjal.Store;

/// <summary>
/// rc.25 (DES-15, organisation-held encryption, D-232): the one envelope format for everything
/// Anjal stores encrypted - a mail file, a subject, a body text, a document, a wrapped key.
/// A fresh 256-bit content key encrypts the bytes with AES-256-GCM; the content key itself is
/// wrapped either to an RSA public key (OAEP with SHA-256; kind 1 - mail arriving, the sender's
/// copy, anything written without the reader's key) or with a 256-bit symmetric key (AES-GCM;
/// kind 2 - re-keyed mail and what is written in a session). The header names the key by an
/// 8-byte id so that the right key is found and keys can be rotated.
/// <code>
/// "ANJE" | version 1 | kind | key id (8) | wrapped length (u16, big-endian) | wrapped content key | nonce (12) | tag (16) | ciphertext
/// </code>
/// Only .NET's own System.Security.Cryptography is used.
/// </summary>
public static class MailEnvelope
{
    /// <summary>Kind 1: the content key wrapped to an RSA public key.</summary>
    public const byte KindPublicKey = 1;

    /// <summary>Kind 2: the content key wrapped with a 256-bit symmetric key.</summary>
    public const byte KindSymmetric = 2;

    /// <summary>Kind 3: the content key wrapped to several RSA public keys (evidence kept for more than one organisation).</summary>
    public const byte KindManyKeys = 3;

    /// <summary>The prefix of an encrypted text field ("e1." then base64url of an envelope).</summary>
    public const string FieldPrefix = "e1.";

    /// <summary>
    /// How every encrypted text field begins: the prefix and the base64url of the envelope's magic and
    /// version ("ANJE", 1). A plain text that merely starts with "e1." is not taken for one.
    /// </summary>
    public const string FieldMarker = "e1.QU5KRQE";

    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyIdBytes = 8;
    private const int FixedHeader = 4 + 1 + 1 + KeyIdBytes + 2;
    private static readonly byte[] Magic = { 0x41, 0x4E, 0x4A, 0x45 };
    private static readonly byte[] BodyAad = Encoding.ASCII.GetBytes("ANJE1");

    /// <summary>True when the bytes are an envelope (the magic and the version).</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>Whether it is an envelope.</returns>
    public static bool IsEnvelope(ReadOnlySpan<byte> data) =>
        data.Length >= FixedHeader + NonceBytes + TagBytes && data[..4].SequenceEqual(Magic) && data[4] == 1;

    /// <summary>True when a stored text is an encrypted field.</summary>
    /// <param name="value">The stored text.</param>
    /// <returns>Whether it is encrypted.</returns>
    public static bool IsField(string? value) => value is not null && value.StartsWith(FieldMarker, StringComparison.Ordinal);

    /// <summary>The kind and key id of an envelope, or null when it is not one.</summary>
    /// <param name="data">The envelope.</param>
    /// <returns>The kind and the key id.</returns>
    public static (byte Kind, string KeyId)? Describe(ReadOnlySpan<byte> data) =>
        IsEnvelope(data) ? (data[5], Convert.ToHexString(data.Slice(6, KeyIdBytes)).ToLowerInvariant()) : null;

    /// <summary>Encrypt bytes to an RSA public key (kind 1).</summary>
    /// <param name="plain">The bytes.</param>
    /// <param name="publicKey">The RSA public key (SubjectPublicKeyInfo DER).</param>
    /// <returns>The envelope.</returns>
    public static byte[] SealToPublicKey(ReadOnlySpan<byte> plain, byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        byte[] contentKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            byte[] wrapped;
            using (var rsa = RSA.Create())
            {
                rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
                wrapped = rsa.Encrypt(contentKey, RSAEncryptionPadding.OaepSHA256);
            }
            return Build(KindPublicKey, MailKeys.PublicKeyId(publicKey), wrapped, contentKey, plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
        }
    }

    /// <summary>Encrypt bytes to several RSA public keys at once (kind 3; one key gives kind 1).</summary>
    /// <param name="plain">The bytes.</param>
    /// <param name="publicKeys">The public keys (at least one, at most 32).</param>
    /// <returns>The envelope.</returns>
    public static byte[] SealToPublicKeys(ReadOnlySpan<byte> plain, IReadOnlyList<byte[]> publicKeys)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        List<byte[]> distinct = publicKeys.DistinctBy(MailKeys.PublicKeyIdText).ToList();
        if (distinct.Count == 0 || distinct.Count > 32)
        {
            throw new ArgumentException("One to 32 public keys are needed.", nameof(publicKeys));
        }
        if (distinct.Count == 1)
        {
            return SealToPublicKey(plain, distinct[0]);
        }
        byte[] contentKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var list = new MemoryStream();
            list.WriteByte((byte)distinct.Count);
            foreach (byte[] key in distinct)
            {
                byte[] wrapped;
                using (var rsa = RSA.Create())
                {
                    rsa.ImportSubjectPublicKeyInfo(key, out _);
                    wrapped = rsa.Encrypt(contentKey, RSAEncryptionPadding.OaepSHA256);
                }
                list.Write(MailKeys.PublicKeyId(key));
                list.WriteByte((byte)(wrapped.Length >> 8));
                list.WriteByte((byte)wrapped.Length);
                list.Write(wrapped);
            }
            return Build(KindManyKeys, new byte[KeyIdBytes], list.ToArray(), contentKey, plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
        }
    }

    /// <summary>Encrypt bytes with a 256-bit symmetric key (kind 2).</summary>
    /// <param name="plain">The bytes.</param>
    /// <param name="key">The 32-byte key.</param>
    /// <returns>The envelope.</returns>
    public static byte[] SealWithKey(ReadOnlySpan<byte> plain, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        byte[] contentKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            return Build(KindSymmetric, MailKeys.SymmetricKeyId(key), WrapSymmetric(contentKey, key), contentKey, plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
        }
    }

    /// <summary>Open an envelope with the keys the resolver can give.</summary>
    /// <param name="envelope">The envelope.</param>
    /// <param name="keys">Where the private and symmetric keys come from.</param>
    /// <returns>The bytes, or null when no key for it is unlocked here.</returns>
    /// <exception cref="CryptographicException">The envelope is damaged or was changed.</exception>
    public static byte[]? Open(ReadOnlySpan<byte> envelope, IMailKeySource keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!TryParse(envelope, out byte kind, out string keyId, out int wrappedAt, out int wrappedLength, out int bodyAt))
        {
            throw new CryptographicException("Not an Anjal envelope.");
        }
        byte[]? contentKey = UnwrapContentKey(kind, keyId, envelope.Slice(wrappedAt, wrappedLength), keys);
        if (contentKey is null)
        {
            return null;
        }
        try
        {
            return DecryptBody(envelope[bodyAt..], contentKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
        }
    }

    /// <summary>
    /// Re-wrap an envelope's content key with a symmetric key, keeping the ciphertext as it is
    /// (DES-15 §5.3: mail that arrived for the RSA key is re-keyed to the mailbox data key at the
    /// next sign-in, so lists and search cost AES, not RSA). Null when the envelope cannot be opened here.
    /// </summary>
    /// <param name="envelope">The envelope.</param>
    /// <param name="keys">The keys unlocked here.</param>
    /// <param name="key">The symmetric key to wrap with.</param>
    /// <returns>The re-keyed envelope.</returns>
    public static byte[]? Rekey(ReadOnlySpan<byte> envelope, IMailKeySource keys, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(key);
        if (!TryParse(envelope, out byte kind, out string keyId, out int wrappedAt, out int wrappedLength, out int bodyAt))
        {
            throw new CryptographicException("Not an Anjal envelope.");
        }
        byte[]? contentKey = UnwrapContentKey(kind, keyId, envelope.Slice(wrappedAt, wrappedLength), keys);
        if (contentKey is null)
        {
            return null;
        }
        try
        {
            byte[] wrapped = WrapSymmetric(contentKey, key);
            ReadOnlySpan<byte> body = envelope[bodyAt..];
            byte[] result = new byte[FixedHeader + wrapped.Length + body.Length];
            WriteHeader(result, KindSymmetric, MailKeys.SymmetricKeyId(key), wrapped);
            body.CopyTo(result.AsSpan(FixedHeader + wrapped.Length));
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
        }
    }

    /// <summary>Encrypt a text field to an RSA public key ("e1." + base64url).</summary>
    /// <param name="text">The text.</param>
    /// <param name="publicKey">The RSA public key.</param>
    /// <returns>The stored form.</returns>
    public static string FieldToPublicKey(string text, byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FieldPrefix + Base64Url(SealToPublicKey(Encoding.UTF8.GetBytes(text), publicKey));
    }

    /// <summary>Encrypt a text field with a symmetric key.</summary>
    /// <param name="text">The text.</param>
    /// <param name="key">The 32-byte key.</param>
    /// <returns>The stored form.</returns>
    public static string FieldWithKey(string text, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FieldPrefix + Base64Url(SealWithKey(Encoding.UTF8.GetBytes(text), key));
    }

    /// <summary>The envelope inside a stored field, or null when the text is not encrypted.</summary>
    /// <param name="value">The stored text.</param>
    /// <returns>The envelope bytes.</returns>
    public static byte[]? FieldEnvelope(string? value)
    {
        if (!IsField(value))
        {
            return null;
        }
        try
        {
            byte[] envelope = FromBase64Url(value![FieldPrefix.Length..]);
            return IsEnvelope(envelope) ? envelope : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Open a stored field: the text, the text itself when it was never encrypted, or null when locked.</summary>
    /// <param name="value">The stored text.</param>
    /// <param name="keys">The keys unlocked here.</param>
    /// <returns>The text, or null.</returns>
    public static string? OpenField(string? value, IMailKeySource keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (value is null)
        {
            return null;
        }
        byte[]? envelope = FieldEnvelope(value);
        if (envelope is null)
        {
            return value;
        }
        try
        {
            byte[]? plain = Open(envelope, keys);
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Re-key a stored field (§5.3); the field unchanged when it cannot be opened or is already re-keyed.</summary>
    /// <param name="value">The stored text.</param>
    /// <param name="keys">The keys unlocked here.</param>
    /// <param name="key">The symmetric key.</param>
    /// <returns>The stored form.</returns>
    public static string RekeyField(string value, IMailKeySource keys, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[]? envelope = FieldEnvelope(value);
        if (envelope is null || envelope[5] != KindPublicKey)
        {
            return value;
        }
        try
        {
            byte[]? rekeyed = Rekey(envelope, keys, key);
            return rekeyed is null ? value : FieldPrefix + Base64Url(rekeyed);
        }
        catch (CryptographicException)
        {
            // Damaged, or not Anjal's: left as it is.
            return value;
        }
    }

    /// <summary>base64url without padding.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The text.</returns>
    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decode base64url without padding.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The bytes.</returns>
    public static byte[] FromBase64Url(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }

    private static byte[] Build(byte kind, byte[] keyId, byte[] wrapped, byte[] contentKey, ReadOnlySpan<byte> plain)
    {
        byte[] result = new byte[FixedHeader + wrapped.Length + NonceBytes + TagBytes + plain.Length];
        WriteHeader(result, kind, keyId, wrapped);
        int at = FixedHeader + wrapped.Length;
        Span<byte> nonce = result.AsSpan(at, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(contentKey, TagBytes);
        aes.Encrypt(nonce, plain, result.AsSpan(at + NonceBytes + TagBytes, plain.Length), result.AsSpan(at + NonceBytes, TagBytes), BodyAad);
        return result;
    }

    private static void WriteHeader(byte[] target, byte kind, byte[] keyId, byte[] wrapped)
    {
        Magic.CopyTo(target, 0);
        target[4] = 1;
        target[5] = kind;
        keyId.AsSpan(0, KeyIdBytes).CopyTo(target.AsSpan(6));
        target[14] = (byte)(wrapped.Length >> 8);
        target[15] = (byte)wrapped.Length;
        wrapped.CopyTo(target, FixedHeader);
    }

    private static bool TryParse(ReadOnlySpan<byte> data, out byte kind, out string keyId, out int wrappedAt, out int wrappedLength, out int bodyAt)
    {
        kind = 0;
        keyId = string.Empty;
        wrappedAt = FixedHeader;
        wrappedLength = 0;
        bodyAt = 0;
        if (!IsEnvelope(data))
        {
            return false;
        }
        kind = data[5];
        keyId = Convert.ToHexString(data.Slice(6, KeyIdBytes)).ToLowerInvariant();
        wrappedLength = (data[14] << 8) | data[15];
        bodyAt = FixedHeader + wrappedLength;
        return (kind is KindPublicKey or KindSymmetric or KindManyKeys) && data.Length >= bodyAt + NonceBytes + TagBytes;
    }

    private static byte[]? UnwrapContentKey(byte kind, string keyId, ReadOnlySpan<byte> wrapped, IMailKeySource keys)
    {
        if (kind == KindPublicKey)
        {
            RSA? rsa = keys.PrivateKey(keyId);
            return rsa?.Decrypt(wrapped.ToArray(), RSAEncryptionPadding.OaepSHA256);
        }
        if (kind == KindManyKeys)
        {
            int count = wrapped.Length > 0 ? wrapped[0] : 0;
            int at = 1;
            for (int i = 0; i < count && at + KeyIdBytes + 2 <= wrapped.Length; i++)
            {
                string id = Convert.ToHexString(wrapped.Slice(at, KeyIdBytes)).ToLowerInvariant();
                int length = (wrapped[at + KeyIdBytes] << 8) | wrapped[at + KeyIdBytes + 1];
                int from = at + KeyIdBytes + 2;
                if (from + length > wrapped.Length)
                {
                    break;
                }
                if (keys.PrivateKey(id) is RSA rsa)
                {
                    return rsa.Decrypt(wrapped.Slice(from, length).ToArray(), RSAEncryptionPadding.OaepSHA256);
                }
                at = from + length;
            }
            return null;
        }
        byte[]? key = keys.SymmetricKey(keyId);
        if (key is null)
        {
            return null;
        }
        if (wrapped.Length != NonceBytes + 32 + TagBytes)
        {
            throw new CryptographicException("A wrapped key has the wrong length.");
        }
        byte[] contentKey = new byte[32];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(wrapped[..NonceBytes], wrapped.Slice(NonceBytes, 32), wrapped.Slice(NonceBytes + 32, TagBytes), contentKey, BodyAad);
        return contentKey;
    }

    private static byte[] WrapSymmetric(byte[] contentKey, byte[] key)
    {
        byte[] wrapped = new byte[NonceBytes + 32 + TagBytes];
        RandomNumberGenerator.Fill(wrapped.AsSpan(0, NonceBytes));
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(wrapped.AsSpan(0, NonceBytes), contentKey, wrapped.AsSpan(NonceBytes, 32), wrapped.AsSpan(NonceBytes + 32, TagBytes), BodyAad);
        return wrapped;
    }

    private static byte[] DecryptBody(ReadOnlySpan<byte> body, byte[] contentKey)
    {
        int length = body.Length - NonceBytes - TagBytes;
        byte[] plain = new byte[length];
        using var aes = new AesGcm(contentKey, TagBytes);
        aes.Decrypt(body[..NonceBytes], body.Slice(NonceBytes + TagBytes, length), body.Slice(NonceBytes, TagBytes), plain, BodyAad);
        return plain;
    }
}

/// <summary>Where the keys that open envelopes come from (a person's session, the server, a test).</summary>
public interface IMailKeySource
{
    /// <summary>The unlocked RSA private key with this id, or null.</summary>
    /// <param name="keyId">The key id (16 hex characters).</param>
    /// <returns>The key.</returns>
    RSA? PrivateKey(string keyId);

    /// <summary>The unlocked symmetric key with this id, or null.</summary>
    /// <param name="keyId">The key id.</param>
    /// <returns>The key.</returns>
    byte[]? SymmetricKey(string keyId);
}

/// <summary>Keys: making them, naming them, wrapping them, and deriving the keys that unlock them.</summary>
public static class MailKeys
{
    /// <summary>RSA key size for mailbox, organisation and break-glass keys (DES-15 §2).</summary>
    public const int RsaBits = 3072;

    /// <summary>PBKDF2 rounds for the unlock key (the same as the password hash).</summary>
    public const int UnlockRounds = 600_000;

    /// <summary>The setting holding the pepper (32 random bytes, base64), kept out of the database.</summary>
    public const string PepperSetting = "ANJAL_KEY_PEPPER";

    private static readonly byte[] UnlockInfo = Encoding.ASCII.GetBytes("anjal-unlock-v1");
    private static readonly byte[] RecoveryInfo = Encoding.ASCII.GetBytes("anjal-recovery-v1");
    private static readonly byte[] SessionInfo = Encoding.ASCII.GetBytes("anjal-session-v1");
    private static readonly byte[] SymmetricIdLabel = Encoding.ASCII.GetBytes("anjal-key-id-v1");

    /// <summary>Make an RSA key pair: (SubjectPublicKeyInfo, PKCS#8 private key).</summary>
    /// <returns>The pair.</returns>
    public static (byte[] Public, byte[] Private) NewKeyPair()
    {
        using var rsa = RSA.Create(TestKeyBits > 0 ? TestKeyBits : RsaBits);
        return (rsa.ExportSubjectPublicKeyInfo(), rsa.ExportPkcs8PrivateKey());
    }

    /// <summary>Tests only: a smaller key size (0, the default, means <see cref="RsaBits"/>).</summary>
    internal static int TestKeyBits { get; set; }

    /// <summary>A fresh 256-bit key.</summary>
    /// <returns>The key.</returns>
    public static byte[] NewSymmetricKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>The 8-byte id of a public key: the first bytes of its SHA-256.</summary>
    /// <param name="publicKey">The public key.</param>
    /// <returns>The id.</returns>
    public static byte[] PublicKeyId(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        return SHA256.HashData(publicKey)[..8];
    }

    /// <summary>The id of a public key as text.</summary>
    /// <param name="publicKey">The public key.</param>
    /// <returns>16 hex characters.</returns>
    public static string PublicKeyIdText(byte[] publicKey) => Convert.ToHexString(PublicKeyId(publicKey)).ToLowerInvariant();

    /// <summary>The 8-byte id of a symmetric key (a hash of the key with a label; it reveals nothing of the key).</summary>
    /// <param name="key">The key.</param>
    /// <returns>The id.</returns>
    public static byte[] SymmetricKeyId(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return HMACSHA256.HashData(key, SymmetricIdLabel)[..8];
    }

    /// <summary>The id of a symmetric key as text.</summary>
    /// <param name="key">The key.</param>
    /// <returns>16 hex characters.</returns>
    public static string SymmetricKeyIdText(byte[] key) => Convert.ToHexString(SymmetricKeyId(key)).ToLowerInvariant();

    /// <summary>The pepper from the environment, or empty when none is set (development).</summary>
    /// <returns>The pepper.</returns>
    public static byte[] PepperFromEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable(PepperSetting);
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<byte>();
        }
        byte[] pepper = Convert.FromBase64String(value.Trim());
        return pepper.Length == 32 ? pepper : throw new InvalidOperationException($"{PepperSetting} must be 32 bytes, base64 (openssl rand -base64 32).");
    }

    /// <summary>
    /// Why a service must not start with the pepper as it is (null when it may): a pepper that is
    /// not 32 bytes of base64 always stops it; with the PostgreSQL store (a real deployment), a
    /// missing pepper does too - keys made without it would be locked out the day it is added.
    /// </summary>
    /// <param name="durableStore">Whether the store is PostgreSQL.</param>
    /// <returns>The reason, or null.</returns>
    public static string? PepperProblem(bool durableStore)
    {
        try
        {
            byte[] pepper = PepperFromEnvironment();
            return pepper.Length == 0 && durableStore
                ? $"{PepperSetting} is not set. Organisations' mail is encrypted with keys that depend on it: make it once with  openssl rand -base64 32  and put the same value in server.env and webmail.env (DEPLOY.md)."
                : null;
        }
        catch (FormatException)
        {
            return $"{PepperSetting} must be 32 bytes, base64 (openssl rand -base64 32).";
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// rc.25 (DES-15 §7.6): the mark of an address in a person's encrypted contacts and contact
    /// groups, so that arrival (away replies to contacts only, rules on a group) can ask "is this
    /// sender in it?" without the list itself being readable: an HMAC keyed from the pepper, which
    /// is not in the database.
    /// </summary>
    /// <param name="mailboxId">The mailbox whose list it is: the same address has another mark in every mailbox, so lists cannot be compared.</param>
    /// <param name="address">The address (case does not matter).</param>
    /// <param name="pepper">The pepper (empty in development).</param>
    /// <returns>The mark, base64url.</returns>
    public static string AddressMark(Guid mailboxId, string address, byte[] pepper)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(pepper);
        byte[] key = HKDF.DeriveKey(HashAlgorithmName.SHA256, pepper.Length > 0 ? pepper : new byte[32], 32, Array.Empty<byte>(), Encoding.ASCII.GetBytes("anjal-address-mark-v1"));
        byte[] mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(mailboxId.ToString("N") + ":" + address.Trim().ToLowerInvariant()));
        CryptographicOperations.ZeroMemory(key);
        return MailEnvelope.Base64Url(mac.AsSpan(0, 16).ToArray());
    }

    /// <summary>A new salt for the unlock key.</summary>
    /// <returns>16 random bytes.</returns>
    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(16);

    /// <summary>
    /// The person's unlock key (DES-15 §2): PBKDF2-SHA256 of the password with its own salt,
    /// then HKDF with the pepper - so a stolen database without the pepper cannot test guesses.
    /// </summary>
    /// <param name="password">The password.</param>
    /// <param name="salt">The salt.</param>
    /// <param name="pepper">The pepper (may be empty in development).</param>
    /// <returns>32 bytes.</returns>
    public static byte[] UnlockKey(string password, byte[] salt, byte[] pepper)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(pepper);
        byte[] stretched = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, UnlockRounds, HashAlgorithmName.SHA256, 32);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, stretched, 32, pepper, UnlockInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stretched);
        }
    }

    /// <summary>The key that a recovery secret (the sheet's 32 bytes) opens the organisation's recovery key with.</summary>
    /// <param name="secret">The secret.</param>
    /// <returns>32 bytes.</returns>
    public static byte[] RecoveryWrapKey(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, Array.Empty<byte>(), RecoveryInfo);
    }

    /// <summary>The key a session's cookie value opens the session's copy of the keys with.</summary>
    /// <param name="token">The cookie's 32 random bytes.</param>
    /// <returns>32 bytes.</returns>
    public static byte[] SessionWrapKey(byte[] token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, token, 32, Array.Empty<byte>(), SessionInfo);
    }

    /// <summary>Wrap a secret (a private key, a data key) with a 32-byte key.</summary>
    /// <param name="secret">The secret.</param>
    /// <param name="key">The wrapping key.</param>
    /// <returns>The wrapped form.</returns>
    public static byte[] Wrap(byte[] secret, byte[] key) => MailEnvelope.SealWithKey(secret, key);

    /// <summary>Unwrap a secret wrapped with <see cref="Wrap"/>; null when the key is wrong.</summary>
    /// <param name="wrapped">The wrapped form.</param>
    /// <param name="key">The wrapping key.</param>
    /// <returns>The secret, or null.</returns>
    public static byte[]? Unwrap(byte[] wrapped, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            return MailEnvelope.Open(wrapped, new SingleKey(key));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Load a PKCS#8 private key.</summary>
    /// <param name="pkcs8">The key.</param>
    /// <returns>The RSA object (dispose it).</returns>
    public static RSA LoadPrivate(byte[] pkcs8)
    {
        ArgumentNullException.ThrowIfNull(pkcs8);
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(pkcs8, out _);
        return rsa;
    }

    /// <summary>The public key (SubjectPublicKeyInfo) of a PKCS#8 private key.</summary>
    /// <param name="pkcs8">The private key.</param>
    /// <returns>The public key.</returns>
    public static byte[] PublicOf(byte[] pkcs8)
    {
        using RSA rsa = LoadPrivate(pkcs8);
        return rsa.ExportSubjectPublicKeyInfo();
    }

    private sealed class SingleKey(byte[] key) : IMailKeySource
    {
        private readonly string id = SymmetricKeyIdText(key);

        public RSA? PrivateKey(string keyId) => null;

        public byte[]? SymmetricKey(string keyId) => keyId == this.id ? key : null;
    }
}
