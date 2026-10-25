using System.Security.Cryptography;
using System.Text;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// rc.24 (item 90, D-198; owner, 25 Oct 2026: no SMS anywhere): an individual on anjalmail.com must
/// have a recovery address, proved by a link mailed to it. Sign-up sends the link; an account made by
/// registering from an invitation already proved its address by opening that invitation. Existing
/// individuals are asked at their next sign-in, and may choose "Later" for the first seven days.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>How long a recovery link works.</summary>
    public static readonly TimeSpan RecoveryLinkLife = TimeSpan.FromHours(24);

    /// <summary>How long an individual may choose "Later" after first being asked.</summary>
    public static readonly TimeSpan RecoveryGrace = TimeSpan.FromDays(7);

    /// <summary>The shortest time between two links, so the button cannot send a flood.</summary>
    public static readonly TimeSpan RecoveryResendGap = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Send the link that proves a recovery address. With <paramref name="newAddress"/> the address is changed
    /// first, which needs the password. Returns the error, or null.
    /// </summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="newAddress">A new address, or null to prove the one set.</param>
    /// <param name="password">The password, when the address changes.</param>
    /// <param name="baseUrl">The webmail's address, for the link.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> SendRecoveryCheckAsync(Guid mailboxId, string? newAddress, string? password, string baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return "Mailbox is not available.";
        }
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        string a = (newAddress ?? string.Empty).Trim().ToLowerInvariant();
        if (a.Length > 0 && !string.Equals(a, doc.RecoveryAddress, StringComparison.OrdinalIgnoreCase))
        {
            if (!Anjal.Smtp.Pbkdf2Hasher.Verify(password ?? string.Empty, context.Value.Mailbox.PasswordPbkdf2))
            {
                return "The password is not correct.";
            }
            if (RecoveryProblem(a, context.Value.Mailbox.Address) is string bad)
            {
                return bad;
            }
            doc.RecoveryAddress = a;
        }
        if (doc.RecoveryAddress.Length == 0)
        {
            return "Give a personal address other than this mailbox.";
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (doc.RecoveryCheckSentAt is DateTimeOffset sent && now - sent < RecoveryResendGap
            && string.Equals(doc.RecoveryCheckAddress, doc.RecoveryAddress, StringComparison.OrdinalIgnoreCase))
        {
            await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
            return "A link was sent a moment ago. Wait two minutes before asking for another.";
        }
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        doc.RecoveryCheckHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
        doc.RecoveryCheckAddress = doc.RecoveryAddress;
        doc.RecoveryCheckExpires = now + RecoveryLinkLife;
        doc.RecoveryCheckSentAt = now;
        await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        this.ForgetDemand(mailboxId);
        string link = baseUrl.TrimEnd('/') + "/recovery/confirm?m=" + mailboxId.ToString("N") + "&t=" + secret;
        string text = "Someone - most likely you - gave this address as the recovery address of the mailbox "
            + context.Value.Mailbox.Address + ".\r\n\r\nOpen this link to confirm it (it works for 24 hours):\r\n" + link
            + "\r\n\r\nA forgotten password is then reset with a code sent here. If this was not you, ignore this mail; nothing changes.\r\n";
        return await this.SendSystemMailAsync(context.Value.Mailbox, doc.RecoveryAddress, "Confirm your recovery address for " + context.Value.Mailbox.Address, text, ct).ConfigureAwait(false);
    }

    /// <summary>Open a recovery link: the address is proved when the link is right, unused, in time, and for the address still set.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="secret">The link's secret.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when the address is now proved.</returns>
    public async Task<bool> ConfirmRecoveryAsync(Guid mailboxId, string? secret, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length > 100 || await this.store.GetMailboxByIdAsync(mailboxId, ct).ConfigureAwait(false) is null)
        {
            return false;
        }
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
        bool right = doc.RecoveryCheckHash.Length > 0
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(doc.RecoveryCheckHash))
            && doc.RecoveryCheckExpires is DateTimeOffset until && until > DateTimeOffset.UtcNow
            && string.Equals(doc.RecoveryCheckAddress, doc.RecoveryAddress, StringComparison.OrdinalIgnoreCase);
        if (!right)
        {
            return false;
        }
        doc.RecoveryConfirmedAddress = doc.RecoveryAddress;
        doc.RecoveryConfirmedAt = DateTimeOffset.UtcNow;
        doc.RecoveryCheckHash = string.Empty;
        doc.RecoveryCheckExpires = null;
        await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        this.ForgetDemand(mailboxId);
        return true;
    }

    /// <summary>Record a recovery address as proved without a link: the person opened an invitation mailed to it.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task MarkRecoveryProvedAsync(Guid mailboxId, CancellationToken ct = default)
    {
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        if (doc.RecoveryAddress.Length > 0)
        {
            doc.RecoveryConfirmedAddress = doc.RecoveryAddress;
            doc.RecoveryConfirmedAt = DateTimeOffset.UtcNow;
            await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Whether this person must prove a recovery address before their mail opens: an individual without a proved one.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when the question waits.</returns>
    public async Task<bool> RecoveryWaitingAsync(Guid mailboxId, CancellationToken ct = default)
    {
        // An individual's own account - not User management, which runs the individuals' tenant.
        if (!(await this.ProfileOfAsync(mailboxId, ct).ConfigureAwait(false)).Individual)
        {
            return false;
        }
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        if (doc.RecoveryConfirmed)
        {
            return false;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (doc.RecoveryAskedAt is null)
        {
            doc.RecoveryAskedAt = now;
            await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        }
        return !(doc.RecoveryLaterUntil is DateTimeOffset later && later > now);
    }

    /// <summary>"Later": the question waits until the next sign-in (12 hours), within the first seven days only.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when "Later" was allowed.</returns>
    public async Task<bool> RecoveryLaterAsync(Guid mailboxId, CancellationToken ct = default)
    {
        SecurityDocument doc = await this.GetSecurityAsync(mailboxId, ct).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!RecoveryLaterAllowed(doc, now))
        {
            return false;
        }
        doc.RecoveryLaterUntil = now.AddHours(12);
        await this.WriteDocumentAsync(mailboxId, SecurityKind, doc, ct).ConfigureAwait(false);
        this.ForgetDemand(mailboxId);
        return true;
    }

    /// <summary>Whether "Later" may still be chosen: within seven days of first being asked.</summary>
    /// <param name="doc">The person's security document.</param>
    /// <param name="now">Now.</param>
    /// <returns>True when allowed.</returns>
    public static bool RecoveryLaterAllowed(SecurityDocument doc, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return doc.RecoveryAskedAt is null || now - doc.RecoveryAskedAt.Value < RecoveryGrace;
    }
}
