namespace Anjal.Server;

/// <summary>
/// SMTP submission authenticator that chains three sources: an in-memory
/// env-var default user (if configured), the persistent
/// <see cref="Anjal.Store.IMessageStore"/>-backed <c>smtp_users</c> table
/// (service accounts), and the <see cref="Anjal.Store.IMailboxStore"/>-backed
/// <c>mailboxes</c> table (a mailbox authenticates with its full address
/// and may send as its own domain). The env-var user takes priority -
/// useful for bootstrap and single-tenant deployments.
/// </summary>
public sealed class ServerSmtpAuthenticator : Anjal.Smtp.ISmtpAuthenticator
{
    private readonly string envUsername;
    private readonly string envPasswordHash;
    private readonly System.Collections.Generic.IReadOnlyList<string> envAllowedDomains;
    private readonly Anjal.Store.IMessageStore? store;
    private readonly Anjal.Store.IMailboxStore? mailboxStore;

    /// <summary>
    /// Construct with optional env-var single user and optional DB-backed
    /// store. At least one must be non-empty for AUTH to succeed.
    /// </summary>
    /// <param name="envUsername">Username from env. Empty disables the env-var user.</param>
    /// <param name="envPasswordHash">Pre-hashed (PBKDF2) password from env.</param>
    /// <param name="envAllowedDomains">Allowed-from domains for the env user.</param>
    /// <param name="store">Optional store for DB-backed multi-user lookup.</param>
    public ServerSmtpAuthenticator(
        string envUsername,
        string envPasswordHash,
        System.Collections.Generic.IReadOnlyList<string> envAllowedDomains,
        Anjal.Store.IMessageStore? store)
        : this(envUsername, envPasswordHash, envAllowedDomains, store, mailboxStore: null)
    {
    }

    /// <summary>
    /// Construct with optional env-var single user, optional DB-backed
    /// store and optional mailbox store.
    /// </summary>
    /// <param name="envUsername">Username from env. Empty disables the env-var user.</param>
    /// <param name="envPasswordHash">Pre-hashed (PBKDF2) password from env.</param>
    /// <param name="envAllowedDomains">Allowed-from domains for the env user.</param>
    /// <param name="store">Optional store for DB-backed multi-user lookup.</param>
    /// <param name="mailboxStore">Optional mailbox registry; mailboxes authenticate by full address.</param>
    public ServerSmtpAuthenticator(
        string envUsername,
        string envPasswordHash,
        System.Collections.Generic.IReadOnlyList<string> envAllowedDomains,
        Anjal.Store.IMessageStore? store,
        Anjal.Store.IMailboxStore? mailboxStore)
    {
        System.ArgumentNullException.ThrowIfNull(envUsername);
        System.ArgumentNullException.ThrowIfNull(envPasswordHash);
        System.ArgumentNullException.ThrowIfNull(envAllowedDomains);

        this.envUsername = envUsername;
        this.envPasswordHash = envPasswordHash;
        this.envAllowedDomains = envAllowedDomains;
        this.store = store;
        this.mailboxStore = mailboxStore;
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<Anjal.Smtp.AuthenticatedUser?> AuthenticateAsync(
        string username,
        string password,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(username);
        System.ArgumentNullException.ThrowIfNull(password);

        // Try env-var user first.
        if (!string.IsNullOrEmpty(this.envUsername) &&
            string.Equals(username, this.envUsername, System.StringComparison.OrdinalIgnoreCase))
        {
            if (Anjal.Smtp.Pbkdf2Hasher.Verify(password, this.envPasswordHash))
            {
                return new Anjal.Smtp.AuthenticatedUser
                {
                    Username = this.envUsername,
                    AllowedFromDomains = this.envAllowedDomains,
                };
            }
            // Username matched env user but password was wrong - don't fall
            // through to store (don't allow same username to exist twice).
            return null;
        }

        // Fall through to the smtp_users table (service accounts).
        if (this.store is not null)
        {
            Anjal.Store.SmtpUserRow? row = await this.store.GetSmtpUserAsync(username, ct).ConfigureAwait(false);
            if (row is not null)
            {
                if (!row.Enabled || !Anjal.Smtp.Pbkdf2Hasher.Verify(password, row.PasswordPbkdf2))
                {
                    // Username exists as a service account - do not fall through
                    // to mailboxes, so one name cannot be probed against two hashes.
                    return null;
                }
                return new Anjal.Smtp.AuthenticatedUser
                {
                    Username = row.Username,
                    AllowedFromDomains = row.AllowedFromDomains,
                };
            }
        }

        // Finally the mailboxes table: a mailbox logs in as local@domain
        // and may send as its own domain only.
        if (this.mailboxStore is not null &&
            Anjal.Mailbox.MailboxSink.TrySplitAddress(username, out string local, out string domain) &&
            !username.Contains('+', System.StringComparison.Ordinal))
        {
            Anjal.Store.MailboxRow? mailbox = await this.mailboxStore.GetMailboxAsync(local, domain, ct).ConfigureAwait(false);
            if (mailbox is null || !mailbox.Enabled || mailbox.PasswordPbkdf2.Length == 0)
            {
                return null;
            }
            Anjal.Store.TenantRow? tenant = await this.mailboxStore.GetTenantByIdAsync(mailbox.TenantId, ct).ConfigureAwait(false);
            if (tenant is null || !tenant.Enabled)
            {
                return null;
            }
            if (!Anjal.Smtp.Pbkdf2Hasher.Verify(password, mailbox.PasswordPbkdf2))
            {
                return null;
            }
            return new Anjal.Smtp.AuthenticatedUser
            {
                Username = mailbox.Address,
                AllowedFromDomains = new[] { mailbox.Domain },
            };
        }

        return null;
    }
}
