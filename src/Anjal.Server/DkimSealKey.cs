using System.Security.Cryptography;

namespace Anjal.Server;

/// <summary>
/// DES-11 S6 (owner, 10 Oct 2026, "A"): the mail server's seal key. Its private half stays in one
/// file on this server (ANJAL_DKIM_SEAL_KEY; made at the first start) and goes into the encrypted
/// backup; its public half is published in the database, where the webmail reads it to lock each
/// DKIM key it makes. Only this process can open those keys, to sign.
/// </summary>
public static class DkimSealKey
{
    /// <summary>Where the private half is kept when ANJAL_DKIM_SEAL_KEY is not set.</summary>
    public static string DefaultPath => System.OperatingSystem.IsLinux()
        ? "/var/lib/anjal/dkim-seal.pem"
        : System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Anjal", "dkim-seal.pem");

    /// <summary>The configured path.</summary>
    public static string ConfiguredPath => System.Environment.GetEnvironmentVariable("ANJAL_DKIM_SEAL_KEY") is { Length: > 0 } p ? p : DefaultPath;

    /// <summary>
    /// Load the seal key, or make it the first time; publish its public half; and lock every DKIM
    /// key still kept unlocked (the one-time step). Returns the key, or null when it could not be
    /// read or made (the reason is logged; signing then uses only keys not sealed).
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="log">The start-up log.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The private seal key.</returns>
    public static async System.Threading.Tasks.Task<RSA?> PrepareAsync(Anjal.Store.IMessageStore store, System.Action<string> log, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(log);
        string path = ConfiguredPath;
        RSA key = RSA.Create();
        try
        {
            if (System.IO.File.Exists(path))
            {
                key.ImportFromPem(await System.IO.File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            }
            else
            {
                key.Dispose();
                key = RSA.Create(3072);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                await System.IO.File.WriteAllTextAsync(tmp, key.ExportPkcs8PrivateKeyPem(), ct).ConfigureAwait(false);
                if (!System.OperatingSystem.IsWindows())
                {
                    System.IO.File.SetUnixFileMode(tmp, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
                }
                System.IO.File.Move(tmp, path);
                log($"DKIM seal: made a new seal key at {path}. It goes into the backup; without it the DKIM keys it locks cannot be opened.");
            }
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException or CryptographicException or System.ArgumentException)
        {
            key.Dispose();
            log($"WARNING: DKIM seal: the seal key at {path} could not be read or made ({ex.Message}). Keys the webmail makes cannot be locked or used until it can.");
            return null;
        }

        string published = System.Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        Anjal.Store.SealRecord? current = await Anjal.Store.KeySeal.ReadRecordAsync(store, ct).ConfigureAwait(false);
        if (current is null || current.PublicKey != published)
        {
            await store.SetServiceRecordAsync(Anjal.Store.KeySeal.PublicRecordKind, System.Text.Json.JsonSerializer.Serialize(new Anjal.Store.SealRecord { PublicKey = published, Made = System.DateTimeOffset.UtcNow, Path = path }), ct).ConfigureAwait(false);
            log("DKIM seal: public key published for the webmail.");
        }

        // The one-time step: every key still kept unlocked (or under ANJAL_KEK) is locked with the seal key.
        int locked = 0;
        byte[] publicKey = key.ExportSubjectPublicKeyInfo();
        foreach (Anjal.Store.DkimKeyRow row in await store.ListDkimKeysAsync(ct).ConfigureAwait(false))
        {
            if (!Anjal.Store.KeySeal.IsSealed(row.PrivateKeyPem) && row.PrivateKeyPem.Contains("-----BEGIN", System.StringComparison.Ordinal))
            {
                row.PrivateKeyPem = Anjal.Store.KeySeal.Seal(row.PrivateKeyPem, publicKey, row.Domain, row.Selector);
                await store.UpsertDkimKeyAsync(row, ct).ConfigureAwait(false);
                locked++;
            }
        }
        if (locked > 0)
        {
            log($"DKIM seal: {locked} DKIM key(s) that were kept unlocked are now locked with the seal key.");
        }
        return key;
    }
}
