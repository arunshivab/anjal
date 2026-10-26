using System.Security.Cryptography;
using System.Text;

namespace Anjal.Store;

/// <summary>
/// rc.25 (DES-15 §4): the keys unlocked for the work in hand - a person's request in the webmail
/// (their mailbox key and data key, and those of the shared mailboxes they belong to), or a test.
/// It is ambient for the async flow: <see cref="Use"/> sets it for a scope, and the storage layer
/// (the Maildir store, the message rows) opens what it can with <see cref="Current"/>. Nothing in
/// it is ever written anywhere; it ends with the request.
/// </summary>
public sealed class MailKeyring : IMailKeySource, IDisposable
{
    private static readonly AsyncLocal<MailKeyring?> Ambient = new();

    private readonly Dictionary<string, RSA> privateKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> symmetricKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, MailboxKeys> mailboxes = new();
    private readonly Dictionary<string, byte[]> otherPrivate = new(StringComparer.Ordinal);
    private readonly MailKeyring? parent;

    /// <summary>Initializes a new instance of the <see cref="MailKeyring"/> class, empty.</summary>
    public MailKeyring()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MailKeyring"/> class that also finds the keys of another (not owned).</summary>
    /// <param name="parent">The keyring to look in after this one.</param>
    public MailKeyring(MailKeyring? parent)
    {
        this.parent = parent;
    }

    /// <summary>The keyring of the current flow, or an empty one.</summary>
    public static MailKeyring Current => Ambient.Value ?? Empty;

    /// <summary>A keyring with nothing in it (the server, a background task, an operator).</summary>
    public static MailKeyring Empty { get; } = new();

    /// <summary>The mailboxes whose keys are here.</summary>
    public IReadOnlyCollection<Guid> Mailboxes => this.mailboxes.Keys;

    /// <summary>Make this keyring the current one until the returned scope is disposed.</summary>
    /// <param name="keyring">The keyring.</param>
    /// <returns>The scope.</returns>
    public static IDisposable Use(MailKeyring keyring)
    {
        ArgumentNullException.ThrowIfNull(keyring);
        MailKeyring? before = Ambient.Value;
        Ambient.Value = keyring;
        return new Restore(before);
    }

    /// <summary>Add a mailbox's unlocked keys.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="privateKey">Its private key (PKCS#8).</param>
    /// <param name="dataKey">Its data key.</param>
    public void Add(Guid mailboxId, byte[] privateKey, byte[] dataKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentNullException.ThrowIfNull(dataKey);
        RSA rsa = MailKeys.LoadPrivate(privateKey);
        string publicId = MailKeys.PublicKeyIdText(rsa.ExportSubjectPublicKeyInfo());
        if (this.privateKeys.Remove(publicId, out RSA? old))
        {
            old.Dispose();
        }
        this.privateKeys[publicId] = rsa;
        this.symmetricKeys[MailKeys.SymmetricKeyIdText(dataKey)] = (byte[])dataKey.Clone();
        this.mailboxes[mailboxId] = new MailboxKeys(publicId, (byte[])dataKey.Clone(), (byte[])privateKey.Clone());
    }

    /// <summary>Add a private key that belongs to no mailbox (an organisation's recovery key, the break-glass key).</summary>
    /// <param name="privateKey">The key (PKCS#8).</param>
    public void AddPrivateKey(byte[] privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        RSA rsa = MailKeys.LoadPrivate(privateKey);
        string id = MailKeys.PublicKeyIdText(rsa.ExportSubjectPublicKeyInfo());
        if (this.privateKeys.Remove(id, out RSA? old))
        {
            old.Dispose();
        }
        this.privateKeys[id] = rsa;
        this.otherPrivate[id] = (byte[])privateKey.Clone();
    }

    /// <summary>A private key added with <see cref="AddPrivateKey"/> (PKCS#8), by the id of its public key, or null.</summary>
    /// <param name="publicKey">The public key it belongs to.</param>
    /// <returns>The key.</returns>
    public byte[]? PrivateKeyFor(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        return this.otherPrivate.TryGetValue(MailKeys.PublicKeyIdText(publicKey), out byte[]? k) ? k : this.parent?.PrivateKeyFor(publicKey);
    }

    /// <summary>Add a symmetric key that belongs to no mailbox (the webmail's key for mail waiting to be sent).</summary>
    /// <param name="key">The key.</param>
    public void AddSymmetricKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        this.symmetricKeys[MailKeys.SymmetricKeyIdText(key)] = (byte[])key.Clone();
    }

    /// <summary>Whether this mailbox's keys are unlocked here.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <returns>Whether they are.</returns>
    public bool Has(Guid mailboxId) => this.mailboxes.ContainsKey(mailboxId) || (this.parent?.Has(mailboxId) ?? false);

    /// <summary>The data key of an unlocked mailbox, or null.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <returns>The key.</returns>
    public byte[]? DataKey(Guid mailboxId) => this.mailboxes.TryGetValue(mailboxId, out MailboxKeys? k) ? k.DataKey : this.parent?.DataKey(mailboxId);

    /// <summary>The private key (PKCS#8) of an unlocked mailbox, or null - for re-wrapping it (a new password, a shared mailbox's member).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <returns>The key.</returns>
    public byte[]? PrivateKeyOf(Guid mailboxId) => this.mailboxes.TryGetValue(mailboxId, out MailboxKeys? k) ? k.PrivateKey : this.parent?.PrivateKeyOf(mailboxId);

    /// <inheritdoc/>
    public RSA? PrivateKey(string keyId) => this.privateKeys.TryGetValue(keyId, out RSA? rsa) ? rsa : this.parent?.PrivateKey(keyId);

    /// <inheritdoc/>
    public byte[]? SymmetricKey(string keyId) => this.symmetricKeys.TryGetValue(keyId, out byte[]? key) ? key : this.parent?.SymmetricKey(keyId);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (ReferenceEquals(this, Empty))
        {
            return;
        }
        foreach (RSA rsa in this.privateKeys.Values)
        {
            rsa.Dispose();
        }
        foreach (byte[] key in this.symmetricKeys.Values)
        {
            CryptographicOperations.ZeroMemory(key);
        }
        foreach (MailboxKeys k in this.mailboxes.Values)
        {
            CryptographicOperations.ZeroMemory(k.DataKey);
            CryptographicOperations.ZeroMemory(k.PrivateKey);
        }
        foreach (byte[] key in this.otherPrivate.Values)
        {
            CryptographicOperations.ZeroMemory(key);
        }
        this.otherPrivate.Clear();
        this.privateKeys.Clear();
        this.symmetricKeys.Clear();
        this.mailboxes.Clear();
    }

    private sealed record MailboxKeys(string PublicId, byte[] DataKey, byte[] PrivateKey);

    private sealed class Restore(MailKeyring? before) : IDisposable
    {
        public void Dispose() => Ambient.Value = before;
    }
}

/// <summary>
/// rc.25 (DES-15 §6.3, D-240, D-241): Shamir's secret sharing over GF(256), 2 of 3 - the
/// break-glass secret split among the owner, Sthakitha and a bank locker; any two rebuild it,
/// one alone reveals nothing. Each share is its x (1, 2 or 3) and one byte per byte of the secret.
/// </summary>
public static class SecretShares
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

#pragma warning disable CA1810 // The tables are filled once, in order.
    static SecretShares()
#pragma warning restore CA1810
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x ^= x << 1; // multiply by 3, the generator, in GF(2^8) with the AES polynomial
            if ((x & 0x100) != 0)
            {
                x ^= 0x11B;
            }
        }
        for (int i = 255; i < 512; i++)
        {
            Exp[i] = Exp[i - 255];
        }
    }

    /// <summary>Split a secret into three shares, any two of which rebuild it.</summary>
    /// <param name="secret">The secret.</param>
    /// <returns>Shares for x = 1, 2, 3: each is [x, y bytes...].</returns>
    public static IReadOnlyList<byte[]> Split(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        byte[] slope = RandomNumberGenerator.GetBytes(secret.Length);
        try
        {
            var shares = new List<byte[]>(3);
            for (byte x = 1; x <= 3; x++)
            {
                byte[] share = new byte[secret.Length + 1];
                share[0] = x;
                for (int i = 0; i < secret.Length; i++)
                {
                    share[i + 1] = (byte)(secret[i] ^ Multiply(slope[i], x));
                }
                shares.Add(share);
            }
            return shares;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(slope);
        }
    }

    /// <summary>Rebuild the secret from two different shares.</summary>
    /// <param name="a">One share.</param>
    /// <param name="b">Another share.</param>
    /// <returns>The secret.</returns>
    public static byte[] Combine(byte[] a, byte[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length != b.Length || a.Length < 2 || a[0] == b[0] || a[0] == 0 || b[0] == 0)
        {
            throw new ArgumentException("Two different shares of the same secret are needed.");
        }
        byte xa = a[0];
        byte xb = b[0];

        // Lagrange at 0: s = ya * xb/(xa^xb) ^ yb * xa/(xa^xb)  (subtraction is XOR in GF(2^8))
        byte den = (byte)(xa ^ xb);
        byte la = Divide(xb, den);
        byte lb = Divide(xa, den);
        byte[] secret = new byte[a.Length - 1];
        for (int i = 0; i < secret.Length; i++)
        {
            secret[i] = (byte)(Multiply(a[i + 1], la) ^ Multiply(b[i + 1], lb));
        }
        return secret;
    }

    /// <summary>Multiply in GF(2^8).</summary>
    /// <param name="a">One factor.</param>
    /// <param name="b">The other.</param>
    /// <returns>The product.</returns>
    public static byte Multiply(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    private static byte Divide(byte a, byte b) => a == 0 ? (byte)0 : Exp[Log[a] + 255 - Log[b]];
}

/// <summary>
/// rc.25 (DES-15 §6.2): how secrets are written on paper - hexadecimal (not base64; the lesson of
/// 1 October), in groups of four, with a check group of four made from the SHA-256 of the value,
/// so a mistyped character is found at once.
/// </summary>
public static class PaperForm
{
    /// <summary>Write bytes for paper: "ABCD EF01 … CHK1".</summary>
    /// <param name="value">The bytes.</param>
    /// <returns>The text to print.</returns>
    public static string Write(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string hex = Convert.ToHexString(value) + Check(value);
        var sb = new StringBuilder();
        for (int i = 0; i < hex.Length; i += 4)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }
            sb.Append(hex, i, Math.Min(4, hex.Length - i));
        }
        return sb.ToString();
    }

    /// <summary>Read what was typed from paper; null when it does not check.</summary>
    /// <param name="typed">The typed text (spaces and case do not matter).</param>
    /// <returns>The bytes, or null.</returns>
    public static byte[]? Read(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        string hex = new string(typed.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        if (hex.Length < 6 || hex.Length % 2 != 0)
        {
            return null;
        }
        byte[] value;
        try
        {
            value = Convert.FromHexString(hex[..^4]);
        }
        catch (FormatException)
        {
            return null;
        }
        return string.Equals(Check(value), hex[^4..], StringComparison.Ordinal) ? value : null;
    }

    private static string Check(byte[] value) => Convert.ToHexString(SHA256.HashData(value))[..4];
}
