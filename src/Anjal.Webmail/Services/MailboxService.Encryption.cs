using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// Mail that reached the server unencrypted (v1.0.0-rc.7, owner's decision
/// of 27 Sep 2026): it is always received, shown with a red open lock and a
/// warning until the recipient trusts the sender.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The senders this mailbox trusts although their mail arrives unencrypted.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Lower-case addresses.</returns>
    public async Task<IReadOnlySet<string>> TrustedSendersAsync(Guid mailboxId, CancellationToken ct = default) =>
        new HashSet<string>(await this.store.ListTrustedSendersAsync(mailboxId, ct).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when a message should carry the unencrypted warning: it came from
    /// outside without TLS and its sender is not trusted. Messages stored
    /// before v1.0.0-rc.7, and mail from signed-in accounts, never do.
    /// </summary>
    /// <param name="row">The message.</param>
    /// <param name="trusted">The mailbox's trusted senders.</param>
    /// <returns>True to show the lock and warning.</returns>
    public static bool ShowsUnencryptedWarning(MessageRow row, IReadOnlySet<string> trusted)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(trusted);
        return row.TransportEncrypted == false && !trusted.Contains(SenderAddressOf(row));
    }

    /// <summary>Trust the sender of one of this mailbox's messages.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">A message in it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The address now trusted, or null when the message is not this mailbox's.</returns>
    public async Task<string?> TrustSenderOfAsync(Guid mailboxId, Guid messageId, CancellationToken ct = default)
    {
        MessageRow? row = await this.GetOwnedRowAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }
        string address = SenderAddressOf(row);
        if (address.Length == 0)
        {
            return null;
        }
        await this.store.AddTrustedSenderAsync(mailboxId, address, ct).ConfigureAwait(false);
        return address;
    }
}
