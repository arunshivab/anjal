using System.Security.Cryptography;
using Anjal.Store;

namespace Anjal.Mailbox;

/// <summary>
/// rc.28 (DES-20 §2): passwords made for mail apps. Sixteen letters, four groups of four, from an alphabet
/// with no look-alikes; kept as a PBKDF2 hash like account passwords; for an encrypted mailbox each one
/// wraps its own copy of the mail key (with its own salt and the pepper), so removing it removes the copy.
/// </summary>
public static class AppPasswords
{
    /// <summary>At most this many per person (D-347).</summary>
    public const int MaxPerPerson = 10;

    /// <summary>The longest name for a device.</summary>
    public const int MaxName = 60;

    // No l, i, o (they look like 1, 1, 0); lowercase only, so a phone's keyboard needs no shift.
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz";

    /// <summary>A new app password, as shown to the person once ("kfmt-wqpz-rbhd-snxa").</summary>
    /// <returns>The password.</returns>
    public static string NewPassword()
    {
        char[] c = new char[19];
        for (int i = 0, j = 0; i < 16; i++, j++)
        {
            if (i > 0 && i % 4 == 0)
            {
                c[j++] = '-';
            }
            c[j] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return new string(c);
    }

    /// <summary>The password as checked: letters only, lowercase (spaces and dashes an app kept are ignored).</summary>
    /// <param name="typed">What the app sent.</param>
    /// <returns>The letters.</returns>
    public static string Normalize(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        return new string(typed.Where(char.IsAsciiLetter).Select(char.ToLowerInvariant).ToArray());
    }

    /// <summary>Whether what was typed has the shape of an app password (sixteen letters).</summary>
    /// <param name="typed">What the app sent.</param>
    /// <returns>True for sixteen letters, with or without dashes.</returns>
    public static bool LooksLikeOne(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        return Normalize(typed).Length == 16 && typed.All(ch => char.IsAsciiLetter(ch) || ch is '-' or ' ');
    }

    /// <summary>Make the row for a new app password.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="name">The device's name.</param>
    /// <param name="password">The new password (from <see cref="NewPassword"/>).</param>
    /// <param name="privateKey">The mailbox's open mail key (PKCS#8), or null for a mailbox that is not encrypted.</param>
    /// <param name="pepper">The pepper.</param>
    /// <param name="now">The time.</param>
    /// <returns>The row to keep.</returns>
    public static AppPasswordRow Make(Guid mailboxId, string name, string password, byte[]? privateKey, byte[] pepper, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(pepper);
        string letters = Normalize(password);
        byte[] salt = MailKeys.NewSalt();
        byte[]? wrapped = null;
        if (privateKey is not null)
        {
            byte[] key = MailKeys.UnlockKey(letters, salt, pepper);
            wrapped = MailKeys.Wrap(privateKey, key);
            CryptographicOperations.ZeroMemory(key);
        }
        string trimmed = name.Trim();
        return new AppPasswordRow
        {
            MailboxId = mailboxId,
            Name = trimmed.Length > MaxName ? trimmed[..MaxName] : trimmed,
            Hash = Anjal.Smtp.Pbkdf2Hasher.Hash(letters),
            UnlockSalt = salt,
            PrivateByApp = wrapped,
            CreatedAt = now,
        };
    }

    /// <summary>Which of a mailbox's app passwords was typed; null when none.</summary>
    /// <param name="rows">The mailbox's app passwords that work.</param>
    /// <param name="typed">What the app sent.</param>
    /// <returns>The matching row, or null.</returns>
    public static AppPasswordRow? Match(IReadOnlyList<AppPasswordRow> rows, string typed)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(typed);
        if (!LooksLikeOne(typed))
        {
            return null;
        }
        string letters = Normalize(typed);
        foreach (AppPasswordRow row in rows)
        {
            if (row.RemovedAt is null && row.Hash.Length > 0 && Anjal.Smtp.Pbkdf2Hasher.Verify(letters, row.Hash))
            {
                return row;
            }
        }
        return null;
    }

    /// <summary>Open the mail key copy an app password wraps.</summary>
    /// <param name="row">The app password.</param>
    /// <param name="typed">What the app sent.</param>
    /// <param name="pepper">The pepper.</param>
    /// <returns>The mail key (PKCS#8), or null when there is no copy or it does not open.</returns>
    public static byte[]? Unlock(AppPasswordRow row, string typed, byte[] pepper)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(pepper);
        if (row.PrivateByApp is null)
        {
            return null;
        }
        byte[] key = MailKeys.UnlockKey(Normalize(typed), row.UnlockSalt, pepper);
        try
        {
            return MailKeys.Unwrap(row.PrivateByApp, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// A keyring holding a mailbox's keys, opened with an app password: the private key from the app's copy,
    /// and the mailbox's data key opened with it. Null when the mailbox has keys and they do not open.
    /// </summary>
    /// <param name="keys">The mailbox's key row, or null for a mailbox that is not encrypted.</param>
    /// <param name="row">The app password.</param>
    /// <param name="typed">What the app sent.</param>
    /// <param name="pepper">The pepper.</param>
    /// <returns>The keyring (empty for a mailbox that is not encrypted), or null.</returns>
    public static MailKeyring? OpenKeyring(MailboxKeyRow? keys, AppPasswordRow row, string typed, byte[] pepper)
    {
        ArgumentNullException.ThrowIfNull(row);
        var ring = new MailKeyring();
        if (keys is null)
        {
            return ring;
        }
        if (Unlock(row, typed, pepper) is not byte[] privateKey)
        {
            ring.Dispose();
            return null;
        }
        byte[]? dataKey;
        using (var opener = new MailKeyring())
        {
            opener.AddPrivateKey(privateKey);
            dataKey = MailEnvelope.Open(keys.DataKey, opener);
        }
        if (dataKey is null)
        {
            CryptographicOperations.ZeroMemory(privateKey);
            ring.Dispose();
            return null;
        }
        ring.Add(row.MailboxId, privateKey, dataKey);
        CryptographicOperations.ZeroMemory(privateKey);
        CryptographicOperations.ZeroMemory(dataKey);
        return ring;
    }
}
