namespace Anjal.Server;

/// <summary>
/// SMTP submission authenticator that chains two sources: an in-memory
/// env-var default user (if configured) and the persistent
/// <see cref="Anjal.Store.IMessageStore"/>-backed user table. The env-var
/// user takes priority - useful for bootstrap and single-tenant
/// deployments. Multi-tenant deployments use the store.
/// </summary>
public sealed class ServerSmtpAuthenticator : Anjal.Smtp.ISmtpAuthenticator
{
    private readonly string envUsername;
    private readonly string envPasswordHash;
    private readonly System.Collections.Generic.IReadOnlyList<string> envAllowedDomains;
    private readonly Anjal.Store.IMessageStore? store;

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
    {
        System.ArgumentNullException.ThrowIfNull(envUsername);
        System.ArgumentNullException.ThrowIfNull(envPasswordHash);
        System.ArgumentNullException.ThrowIfNull(envAllowedDomains);

        this.envUsername = envUsername;
        this.envPasswordHash = envPasswordHash;
        this.envAllowedDomains = envAllowedDomains;
        this.store = store;
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

        // Fall through to store.
        if (this.store is null)
        {
            return null;
        }

        Anjal.Store.SmtpUserRow? row = await this.store.GetSmtpUserAsync(username, ct).ConfigureAwait(false);
        if (row is null || !row.Enabled)
        {
            return null;
        }

        if (!Anjal.Smtp.Pbkdf2Hasher.Verify(password, row.PasswordPbkdf2))
        {
            return null;
        }

        return new Anjal.Smtp.AuthenticatedUser
        {
            Username = row.Username,
            AllowedFromDomains = row.AllowedFromDomains,
        };
    }
}
