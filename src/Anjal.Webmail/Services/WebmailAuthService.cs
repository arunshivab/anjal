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

    /// <summary>Claim type for the person's time zone (rc.11, DEF-088).</summary>
    public const string TimeZoneClaim = "anjal:tz";

    /// <summary>Claim type for the person's date format (rc.11).</summary>
    public const string DateFormatClaim = "anjal:datefmt";

    /// <summary>Claim type for the person's own clock choice, "24", "12" or empty to follow the organisation (owner, 8 Oct 2026).</summary>
    public const string ClockClaim = "anjal:clock";

    /// <summary>Claim type for the person's language (rc.11).</summary>
    public const string LanguageClaim = "anjal:lang";

    /// <summary>
    /// Claim type for the shared mailbox the person has opened (rc.14). While
    /// it is present, the mail pages show that mailbox; settings, contacts and
    /// security stay the person's own.
    /// </summary>
    public const string ActingClaim = "anjal:acting";

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

        return await this.WithClockAsync(PrincipalFor(mailbox, tenant), mailbox, tenant, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The principal for a mailbox that is enabled, in an enabled organisation,
    /// and can sign in; null otherwise (rc.13: after a second step, or an invitation).
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The principal, or null.</returns>
    public async Task<ClaimsPrincipal?> PrincipalForAsync(Guid mailboxId, CancellationToken ct = default)
    {
        MailboxRow? mailbox = await this.store.GetMailboxByIdAsync(mailboxId, ct).ConfigureAwait(false);
        if (mailbox is null || !mailbox.Enabled || mailbox.PasswordPbkdf2.Length == 0)
        {
            return null;
        }
        TenantRow? tenant = await this.store.GetTenantByIdAsync(mailbox.TenantId, ct).ConfigureAwait(false);
        return tenant is null || !tenant.Enabled ? null : await this.WithClockAsync(PrincipalFor(mailbox, tenant), mailbox, tenant, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Add the session's claims to a principal (rc.13): its id, and whether
    /// the computer is shared.
    /// </summary>
    /// <param name="user">The principal.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="shared">True on a shared computer.</param>
    /// <returns>The principal with the claims.</returns>
    public static ClaimsPrincipal WithSession(ClaimsPrincipal user, string sessionId, bool shared)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(sessionId);
        var identity = new ClaimsIdentity("AnjalWebmail");
        foreach (Claim c in user.Claims)
        {
            if (c.Type != SessionRegistry.SessionClaim && c.Type != SessionRegistry.SharedClaim)
            {
                identity.AddClaim(new Claim(c.Type, c.Value));
            }
        }
        identity.AddClaim(new Claim(SessionRegistry.SessionClaim, sessionId));
        identity.AddClaim(new Claim(SessionRegistry.SharedClaim, shared ? "1" : "0"));
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// A principal with a clock choice (owner, 8 Oct 2026): the person's own, or empty to follow
    /// their organisation - whose choice is noted for every page as well.
    /// </summary>
    /// <param name="user">The current principal.</param>
    /// <param name="own">"24", "12", or empty.</param>
    /// <returns>The principal with the claim.</returns>
    public static ClaimsPrincipal WithClock(ClaimsPrincipal user, string? own)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = new ClaimsIdentity("AnjalWebmail");
        foreach (Claim c in user.Claims)
        {
            if (!string.Equals(c.Type, ClockClaim, StringComparison.Ordinal))
            {
                identity.AddClaim(new Claim(c.Type, c.Value));
            }
        }
        identity.AddClaim(new Claim(ClockClaim, Clocks.Choice(own)));
        return new ClaimsPrincipal(identity);
    }

    private async Task<ClaimsPrincipal> WithClockAsync(ClaimsPrincipal user, MailboxRow mailbox, TenantRow tenant, CancellationToken ct)
    {
        string own = string.Empty;
        try
        {
            string? json = await this.store.GetMailboxDocumentAsync(mailbox.Id, MailboxService.MailSettingsKind, ct).ConfigureAwait(false);
            own = json is null ? string.Empty : System.Text.Json.JsonSerializer.Deserialize<MailSettings>(json)?.Clock ?? string.Empty;
            string? brand = await this.store.GetTenantDocumentAsync(tenant.Id, MailboxService.BrandingKind, ct).ConfigureAwait(false);
            Clocks.SetDefault(tenant.Slug, brand is null ? null : System.Text.Json.JsonSerializer.Deserialize<Branding>(brand)?.DefaultClock);
        }
#pragma warning disable CA1031 // A settings document that cannot be read never stops anyone signing in; the 24-hour clock is used.
        catch (Exception)
        {
        }
#pragma warning restore CA1031
        return WithClock(user, own);
    }

    private static ClaimsPrincipal PrincipalFor(MailboxRow mailbox, TenantRow tenant)
    {
        var identity = new ClaimsIdentity("AnjalWebmail");
        identity.AddClaim(new Claim(ClaimTypes.Name, mailbox.Address));
        identity.AddClaim(new Claim(MailboxIdClaim, mailbox.Id.ToString()));
        identity.AddClaim(new Claim(TenantSlugClaim, tenant.Slug));
        identity.AddClaim(new Claim(ThemeClaim, MailboxRow.NormalizeTheme(mailbox.Theme)));
        MailboxPreferences preferences = MailboxPreferences.Of(mailbox).Normalized();
        identity.AddClaim(new Claim(TimeZoneClaim, preferences.TimeZone));
        identity.AddClaim(new Claim(DateFormatClaim, preferences.DateFormat));
        identity.AddClaim(new Claim(LanguageClaim, preferences.Language));
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// The language the person's pages are shown in: their own choice when the
    /// owner has switched that language on (D-92), English otherwise.
    /// </summary>
    /// <param name="user">The current principal, or null.</param>
    /// <param name="words">The words built into the program.</param>
    /// <returns>A language code that is switched on.</returns>
    public static string LanguageOf(ClaimsPrincipal? user, Words words)
    {
        ArgumentNullException.ThrowIfNull(words);
        string? chosen = user?.FindFirst(LanguageClaim)?.Value;
        // rc.15 (item 41): a language not yet switched on is shown only to the service's operators.
        return words.IsEnabled(chosen) || (words.IsPreview(chosen) && Operators.Is(user?.Identity?.Name)) ? chosen! : "en";
    }

    /// <summary>
    /// Read the signed-in mailbox id from a principal, or
    /// <see langword="null"/> if the principal is anonymous.
    /// </summary>
    /// <param name="user">The current principal.</param>
    public static Guid? MailboxIdOf(ClaimsPrincipal? user)
    {
        string? acting = user?.FindFirst(ActingClaim)?.Value;
        if (acting is not null && Guid.TryParse(acting, out Guid shared))
        {
            return shared;
        }
        return PersonIdOf(user);
    }

    /// <summary>
    /// The signed-in person's own mailbox id (rc.14), whatever shared mailbox
    /// is open; null for an anonymous principal.
    /// </summary>
    /// <param name="user">The current principal.</param>
    /// <returns>The person's mailbox id.</returns>
    public static Guid? PersonIdOf(ClaimsPrincipal? user)
    {
        string? raw = user?.FindFirst(MailboxIdClaim)?.Value;
        return raw is not null && Guid.TryParse(raw, out Guid id) ? id : null;
    }

    /// <summary>The shared mailbox open, or null when the person's own is (rc.14).</summary>
    /// <param name="user">The current principal.</param>
    /// <returns>The shared mailbox's id.</returns>
    public static Guid? ActingOf(ClaimsPrincipal? user)
    {
        string? acting = user?.FindFirst(ActingClaim)?.Value;
        return acting is not null && Guid.TryParse(acting, out Guid shared) ? shared : null;
    }

    /// <summary>Open a shared mailbox, or go back to the person's own (null), keeping every other claim.</summary>
    /// <param name="user">The current principal.</param>
    /// <param name="sharedId">The shared mailbox, or null.</param>
    /// <returns>The principal.</returns>
    public static ClaimsPrincipal WithActing(ClaimsPrincipal user, Guid? sharedId)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = new ClaimsIdentity("AnjalWebmail");
        foreach (Claim c in user.Claims)
        {
            if (c.Type != ActingClaim)
            {
                identity.AddClaim(new Claim(c.Type, c.Value));
            }
        }
        if (sharedId is Guid id)
        {
            identity.AddClaim(new Claim(ActingClaim, id.ToString()));
        }
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// The signed-in mailbox's theme, or the default for an anonymous
    /// request.
    /// </summary>
    /// <param name="user">The current principal.</param>
    public static string ThemeOf(ClaimsPrincipal? user)
    {
        // A cookie issued before rc.11 may still carry an old theme name; it is
        // converted here so the person sees their theme without signing in again.
        return MailboxRow.NormalizeTheme(user?.FindFirst(ThemeClaim)?.Value);
    }

    /// <summary>
    /// The signed-in person's clock: their time zone and date format. A cookie
    /// issued before rc.11 carries neither, and gets India time until the next
    /// sign-in or settings change.
    /// </summary>
    /// <param name="user">The current principal, or null.</param>
    /// <returns>The clock every date on the page is shown with.</returns>
    public static ZonedClock ClockOf(ClaimsPrincipal? user) =>
        ZonedClock.For(user?.FindFirst(TimeZoneClaim)?.Value, user?.FindFirst(DateFormatClaim)?.Value)
            .In(Words.Current is Words words ? words.For(LanguageOf(user, words)) : null)
            .WithHours(Clocks.For(user?.FindFirst(ClockClaim)?.Value, user?.FindFirst(TenantSlugClaim)?.Value));

    /// <summary>
    /// Rebuild a principal with new time-zone and date-format claims, so a
    /// settings change shows on the very next page without another sign-in.
    /// </summary>
    /// <param name="user">The current principal.</param>
    /// <param name="preferences">The saved preferences.</param>
    /// <returns>The principal with the new claims.</returns>
    public static ClaimsPrincipal WithPreferences(ClaimsPrincipal user, MailboxPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(preferences);
        MailboxPreferences p = preferences.Normalized();
        var identity = new ClaimsIdentity("AnjalWebmail");
        foreach (Claim c in user.Claims)
        {
            if (!string.Equals(c.Type, TimeZoneClaim, StringComparison.Ordinal) && !string.Equals(c.Type, DateFormatClaim, StringComparison.Ordinal) && !string.Equals(c.Type, LanguageClaim, StringComparison.Ordinal))
            {
                identity.AddClaim(new Claim(c.Type, c.Value));
            }
        }
        identity.AddClaim(new Claim(TimeZoneClaim, p.TimeZone));
        identity.AddClaim(new Claim(DateFormatClaim, p.DateFormat));
        identity.AddClaim(new Claim(LanguageClaim, p.Language));
        return new ClaimsPrincipal(identity);
    }

    /// <summary>Rebuild a principal with a different language claim (an administrator's change, D-131).</summary>
    /// <param name="user">The current principal.</param>
    /// <param name="language">The new language.</param>
    /// <returns>The principal.</returns>
    public static ClaimsPrincipal WithLanguage(ClaimsPrincipal user, string language)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(language);
        var identity = new ClaimsIdentity("AnjalWebmail");
        foreach (Claim c in user.Claims)
        {
            if (!string.Equals(c.Type, LanguageClaim, StringComparison.Ordinal))
            {
                identity.AddClaim(new Claim(c.Type, c.Value));
            }
        }
        identity.AddClaim(new Claim(LanguageClaim, language));
        return new ClaimsPrincipal(identity);
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
