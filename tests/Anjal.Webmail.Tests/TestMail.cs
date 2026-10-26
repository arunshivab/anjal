using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.25 (DES-15): the mail of encrypted mailboxes as their people see it. A test reads it the way
/// an organisation can: through each organisation's recovery key - here its temporary copy, which
/// exists until a recovery sheet is confirmed - so no password is needed.
/// </summary>
internal static class TestMail
{
    /// <summary>A keyring holding the keys of every mailbox in the store that has them.</summary>
    /// <param name="store">The store.</param>
    /// <param name="sheet">A recovery sheet, for an organisation whose temporary copy has gone.</param>
    /// <returns>The keyring (dispose it).</returns>
    public static MailKeyring Ring(InMemoryMessageStore store, string? sheet = null)
    {
        var km = new MailKeyManager(store, store, Array.Empty<byte>());
        var ring = new MailKeyring();
        var recovery = new Dictionary<Guid, byte[]?>();
        foreach (MailboxRow m in store.AllMailboxes)
        {
            if (store.GetMailboxKeysAsync(m.Id).GetAwaiter().GetResult() is not MailboxKeyRow keys)
            {
                continue;
            }
            if (!recovery.TryGetValue(m.TenantId, out byte[]? rec))
            {
                rec = km.OpenRecoveryAsync(m.TenantId, Guid.Empty, new UnlockedKeys(Array.Empty<byte>(), Array.Empty<byte>())).GetAwaiter().GetResult()
                    ?? (sheet is null ? null : km.OpenWithSheetAsync(m.TenantId, sheet).GetAwaiter().GetResult());
                recovery[m.TenantId] = rec;
            }
            if (rec is null || km.OpenMailboxPrivateByOrgAsync(m.Id, rec).GetAwaiter().GetResult() is not byte[] priv)
            {
                continue;
            }
            using var own = new MailKeyring();
            own.AddPrivateKey(priv);
            if (MailEnvelope.Open(keys.DataKey, own) is byte[] data)
            {
                ring.Add(m.Id, priv, data);
            }
        }
        return ring;
    }

    /// <summary>The message rows with subjects and text opened (copies).</summary>
    /// <param name="store">The store.</param>
    /// <param name="sheet">A recovery sheet, for an organisation whose temporary copy has gone.</param>
    /// <returns>The rows.</returns>
    public static IReadOnlyList<MessageRow> Seen(this InMemoryMessageStore store, string? sheet = null)
    {
        using MailKeyring ring = Ring(store, sheet);
        using (MailKeyring.Use(ring))
        {
            return store.MailboxMessagesOpened;
        }
    }

    /// <summary>A stored mail file opened (plain files are returned as they are).</summary>
    /// <param name="store">The store.</param>
    /// <param name="bytes">The file's bytes.</param>
    /// <returns>The mail.</returns>
    public static byte[] Open(InMemoryMessageStore store, byte[] bytes)
    {
        if (!MailEnvelope.IsEnvelope(bytes))
        {
            return bytes;
        }
        using MailKeyring ring = Ring(store);
        return MailEnvelope.Open(bytes, ring) ?? throw new InvalidOperationException("The file does not open with the organisation's keys.");
    }

    /// <summary>A stored mail file opened, as text.</summary>
    /// <param name="store">The store.</param>
    /// <param name="path">The file.</param>
    /// <returns>The mail.</returns>
    public static string OpenText(InMemoryMessageStore store, string path) =>
        System.Text.Encoding.UTF8.GetString(Open(store, File.ReadAllBytes(path)));
}
