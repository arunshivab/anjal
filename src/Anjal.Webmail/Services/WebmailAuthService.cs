using System.Security.Claims;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// Verifies mailbox credentials for the webmail login form. A mailbox
/// signs in with its full address and the password stored on the
/// mailbox row (the same credentials used for SMTP submission). Disabled
/// tenants and mailboxes, and receive-only mailboxes with no password,
/// are refused.
/// </summary>
public sealed class WebmailAuthService
{
    /// <summary>Claim type carrying the mailbox id.</summary>
    public const string MailboxIdClaim = "anjal:mailbox_id";

    /// <summary>Claim type carrying the tenant slug.</summary>
    public const string TenantSlugClaim = "anjal:tenant_slug";

    /// <summary>
    /// Claim type carrying the mailbox's webmail theme. It rides in the
    /// cookie because the root element's <c>data-theme</c> is written
    /// before any page component runs, and a database read at that point
    /// would cost a query on every request.
    /// </summary>
    public const string ThemeClaim = "anjal:theme";

    private readonly IMailboxStore store;

    /// <summary>Construct.</summary>
    /// <param name="store">Mailbox registry.</param>
    public WebmailAuthService(IMailboxStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <summary>
    /// Validate an address/password pair. Returns the principal to sign
    /// in, or <see langword="null"/> if the credentials are not accepted.
    /// </summary>
    /// <param name="address">Mailbox address (<c>local@domain</c>).</param>
    /// <param name="password">Plaintext password.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<ClaimsPrincipal?> AuthenticateAsync(string address, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(password);

        // Every refusal below costs the same as a wrong password, so timing
        // cannot be used to learn which addresses exist or are disabled.
        if (!Anjal.Mailbox.MailboxSink.TrySplitAddress(address, out string local, out string domain) ||
            address.Contains('+', StringComparison.Ordinal))
        {
            Anjal.Smtp.Pbkdf2Hasher.VerifyAgainstDummy(password);
            return null;
        }

        MailboxRow? mailbox = await this.store.GetMailboxAsync(local, domain, ct).ConfigureAwait(false);
        if (mailbox is null || !mailbox.Enabled || mailbox.PasswordPbkdf2.Length == 0)
        {
            Anjal.Smtp.Pbkdf2Hasher.VerifyAgainstDummy(password);
            return null;
        }
        TenantRow? tenant = await this.store.GetTenantByIdAsync(mailbox.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || !tenant.Enabled)
        {
            Anjal.Smtp.Pbkdf2Hasher.VerifyAgainstDummy(password);
            return null;
        }
        if (!Anjal.Smtp.Pbkdf2Hasher.Verify(password, mailbox.PasswordPbkdf2))
        {
            return null;
        }
        if (Anjal.Smtp.Pbkdf2Hasher.NeedsRehash(mailbox.PasswordPbkdf2))
        {
            mailbox.PasswordPbkdf2 = Anjal.Smtp.Pbkdf2Hasher.Hash(password);
            await this.store.UpsertMailboxAsync(mailbox, ct).ConfigureAwait(false);
        }

        var identity = new ClaimsIdentity("AnjalWebmail");
        identity.AddClaim(new Claim(ClaimTypes.Name, mailbox.Address));
        identity.AddClaim(new Claim(MailboxIdClaim, mailbox.Id.ToString()));
        identity.AddClaim(new Claim(TenantSlugClaim, tenant.Slug));
        identity.AddClaim(new Claim(ThemeClaim, string.IsNullOrWhiteSpace(mailbox.Theme) ? MailboxRow.DefaultTheme : mailbox.Theme));
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Read the signed-in mailbox id from a principal, or
    /// <see langword="null"/> if the principal is anonymous.
    /// </summary>
    /// <param name="user">The current principal.</param>
    public static Guid? MailboxIdOf(ClaimsPrincipal? user)
    {
        string? raw = user?.FindFirst(MailboxIdClaim)?.Value;
        return raw is not null && Guid.TryParse(raw, out Guid id) ? id : null;
    }

    /// <summary>
    /// The signed-in mailbox's theme, or the default for an anonymous
    /// request.
    /// </summary>
    /// <param name="user">The current principal.</param>
    public static string ThemeOf(ClaimsPrincipal? user)
    {
        string? theme = user?.FindFirst(ThemeClaim)?.Value;
        return string.IsNullOrWhiteSpace(theme) ? MailboxRow.DefaultTheme : theme;
    }

    /// <summary>
    /// Rebuild a principal with a different theme claim, so the change
    /// takes effect on the very next render without another sign-in.
    /// </summary>
    /// <param name="user">The current principal.</param>
    /// <param name="theme">The new theme.</param>
    public static ClaimsPrincipal WithTheme(ClaimsPrincipal user, string theme)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(theme);
        var identity = new ClaimsIdentity("AnjalWebmail");
        foreach (Claim c in user.Claims)
        {
            if (!string.Equals(c.Type, ThemeClaim, StringComparison.Ordinal))
            {
                identity.AddClaim(new Claim(c.Type, c.Value));
            }
        }
        identity.AddClaim(new Claim(ThemeClaim, theme));
        return new ClaimsPrincipal(identity);
    }
}
