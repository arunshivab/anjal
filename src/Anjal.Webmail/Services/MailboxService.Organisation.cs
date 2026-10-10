using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// The organisation console (rc.14, boards OrgPeople, OrgSignin, OrgShared,
/// OrgRetention, OrgApps, OrgBranding): administrators, people and
/// invitations, sign-in rules, shared mailboxes, retention and legal holds,
/// applications' send-only keys, and the organisation's look. Kept as the
/// organisation's own documents. Administrators never read anyone's mail.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The kind of the document naming an organisation's administrators.</summary>
    public const string RolesKind = "roles";

    /// <summary>The kind of the document holding the sign-in rules.</summary>
    public const string SignInPolicyKind = "signin-policy";

    /// <summary>The kind of the document holding the retention rules.</summary>
    public const string RetentionKind = "retention";

    /// <summary>The kind of the document listing legal holds.</summary>
    public const string HoldsKind = "holds";

    /// <summary>The kind of the document listing shared mailboxes.</summary>
    public const string SharedKind = "shared";

    /// <summary>The kind of the document listing applications.</summary>
    public const string AppsKind = "apps";

    /// <summary>The kind of the document the operator keeps for an organisation.</summary>
    public const string OpsKind = "ops";

    /// <summary>The kind of the document holding the organisation's templates.</summary>
    public const string OrgTemplatesKind = "templates";

    /// <summary>How long an old key keeps working after a rotation.</summary>
    public static readonly TimeSpan KeyOverlap = TimeSpan.FromDays(14);

    private static readonly TimeSpan OrgCacheFor = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<(Guid, string), (DateTimeOffset At, string? Json)> tenantDocs = new();

    private readonly ConcurrentDictionary<Guid, Guid> tenantOfMailbox = new();

    private readonly ConcurrentDictionary<(Guid, Guid), (DateTimeOffset At, string? Right)> rights = new();

    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, string? Demand)> demands = new();

    /// <summary><see cref="SharedRightAsync"/>, kept for a few seconds: it is asked on every request in a shared mailbox.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="sharedId">The shared mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The right, or null.</returns>
    public async Task<string?> SharedRightCachedAsync(Guid personId, Guid sharedId, CancellationToken ct = default)
    {
        if (this.rights.TryGetValue((personId, sharedId), out (DateTimeOffset At, string? Right) c) && DateTimeOffset.UtcNow - c.At < TimeSpan.FromSeconds(10))
        {
            return c.Right;
        }
        string? right = await this.SharedRightAsync(personId, sharedId, ct).ConfigureAwait(false);
        this.rights[(personId, sharedId)] = (DateTimeOffset.UtcNow, right);
        return right;
    }

    /// <summary><see cref="SignInDemandAsync"/>, kept for a few seconds: it is asked on every page.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The demand, or null.</returns>
    public async Task<string?> SignInDemandCachedAsync(Guid personId, CancellationToken ct = default)
    {
        if (this.demands.TryGetValue(personId, out (DateTimeOffset At, string? Demand) c) && DateTimeOffset.UtcNow - c.At < TimeSpan.FromSeconds(20))
        {
            return c.Demand;
        }
        string? demand = await this.SignInDemandAsync(personId, ct).ConfigureAwait(false);
        this.demands[personId] = (DateTimeOffset.UtcNow, demand);
        return demand;
    }

    /// <summary>Forget what was worked out for a person (their password or two-step changed).</summary>
    /// <param name="personId">The person.</param>
    public void ForgetDemand(Guid personId)
    {
        this.demands.TryRemove(personId, out _);
        foreach ((Guid, Guid) key in this.rights.Keys.Where(k => k.Item1 == personId).ToList())
        {
            this.rights.TryRemove(key, out _);
        }
    }

    /// <summary>One of an organisation's documents, or null.</summary>
    /// <typeparam name="T">Its shape.</typeparam>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The document, or null.</returns>
    public async Task<T?> ReadTenantDocumentAsync<T>(Guid tenantId, string kind, CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(kind);
        string? json;
        if (this.tenantDocs.TryGetValue((tenantId, kind), out (DateTimeOffset At, string? Json) cached) && DateTimeOffset.UtcNow - cached.At < OrgCacheFor)
        {
            json = cached.Json;
        }
        else
        {
            json = await this.store.GetTenantDocumentAsync(tenantId, kind, ct).ConfigureAwait(false);
            this.tenantDocs[(tenantId, kind)] = (DateTimeOffset.UtcNow, json);
        }
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(json, DocumentJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Save one of an organisation's documents.</summary>
    /// <typeparam name="T">Its shape.</typeparam>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="value">The document.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task WriteTenantDocumentAsync<T>(Guid tenantId, string kind, T value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        string json = JsonSerializer.Serialize(value, DocumentJson);
        await this.store.SetTenantDocumentAsync(tenantId, kind, json, ct).ConfigureAwait(false);
        this.tenantDocs[(tenantId, kind)] = (DateTimeOffset.UtcNow, json);
        this.organisations.Clear();
    }

    /// <summary>
    /// True when a person administers their organisation: named in its
    /// roles, or - before anyone is named - its postmaster mailbox.
    /// </summary>
    /// <param name="personId">The person's own mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether they are an administrator.</returns>
    public async Task<bool> IsOrgAdminAsync(Guid personId, CancellationToken ct = default)
    {
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(personId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return false;
        }
        OrgRoles? roles = await this.ReadTenantDocumentAsync<OrgRoles>(context.Value.Tenant.Id, RolesKind, ct).ConfigureAwait(false);
        if (roles is not null && roles.Admins.Count > 0)
        {
            return roles.Admins.Contains(personId);
        }
        return string.Equals(context.Value.Tenant.PostmasterMailbox, context.Value.Mailbox.Address, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The organisation's administrators.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Their mailbox ids.</returns>
    public async Task<IReadOnlyList<Guid>> AdminsOfAsync(TenantRow tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        OrgRoles? roles = await this.ReadTenantDocumentAsync<OrgRoles>(tenant.Id, RolesKind, ct).ConfigureAwait(false);
        if (roles is not null && roles.Admins.Count > 0)
        {
            return roles.Admins;
        }
        if (string.IsNullOrEmpty(tenant.PostmasterMailbox) || !Anjal.Mailbox.MailboxSink.TrySplitAddress(tenant.PostmasterMailbox, out string l, out string d))
        {
            return Array.Empty<Guid>();
        }
        MailboxRow? postmaster = await this.store.GetMailboxAsync(l, d, ct).ConfigureAwait(false);
        return postmaster is null ? Array.Empty<Guid>() : new[] { postmaster.Id };
    }

    /// <summary>Make a person an administrator, or a member. Returns the error, or null.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="personId">The person.</param>
    /// <param name="admin">True for administrator.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SetAdminAsync(TenantRow tenant, Guid personId, bool admin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var admins = (await this.AdminsOfAsync(tenant, ct).ConfigureAwait(false)).ToList();
        admins.Remove(personId);
        if (admin)
        {
            admins.Add(personId);
        }
        if (admins.Count == 0)
        {
            return "An organisation needs at least one administrator.";
        }
        await this.WriteTenantDocumentAsync(tenant.Id, RolesKind, new OrgRoles { Admins = admins }, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>The sign-in rules of the organisation a mailbox belongs to (cached for half a minute).</summary>
    /// <param name="mailboxId">A mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rules; Anjal's defaults when the organisation has set none.</returns>
    public async Task<SignInPolicy> SignInPolicyForAsync(Guid mailboxId, CancellationToken ct = default)
    {
        Guid? tenant = await this.TenantOfAsync(mailboxId, ct).ConfigureAwait(false);
        return tenant is null ? new SignInPolicy() : await this.SignInPolicyOfAsync(tenant.Value, ct).ConfigureAwait(false);
    }

    /// <summary>The organisation that owns a mail domain, or <see cref="Guid.Empty"/> when none does.</summary>
    /// <param name="domain">The domain, for example anjal.co.in.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The organisation's id.</returns>
    public async Task<Guid> TenantOfDomainAsync(string domain, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(domain) ? Guid.Empty : (await this.store.GetTenantDomainAsync(domain.Trim().ToLowerInvariant(), ct).ConfigureAwait(false))?.TenantId ?? Guid.Empty;

    /// <summary>The organisation a mailbox belongs to (never changes, so kept).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The organisation's id, or null.</returns>
    public async Task<Guid?> TenantOfAsync(Guid mailboxId, CancellationToken ct = default)
    {
        if (this.tenantOfMailbox.TryGetValue(mailboxId, out Guid known))
        {
            return known;
        }
        MailboxRow? mailbox = await this.store.GetMailboxByIdAsync(mailboxId, ct).ConfigureAwait(false);
        if (mailbox is null)
        {
            return null;
        }
        this.tenantOfMailbox[mailboxId] = mailbox.TenantId;
        return mailbox.TenantId;
    }

    /// <summary>An organisation's sign-in rules.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rules.</returns>
    public async Task<SignInPolicy> SignInPolicyOfAsync(Guid tenantId, CancellationToken ct = default)
    {
        SignInPolicy? p = await this.ReadTenantDocumentAsync<SignInPolicy>(tenantId, SignInPolicyKind, ct).ConfigureAwait(false);
        return p is null || p.Problem() is not null ? new SignInPolicy() : p;
    }

    /// <summary>Save an organisation's sign-in rules. Returns the error, or null.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="policy">The rules.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SaveSignInPolicyAsync(Guid tenantId, SignInPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Problem() is string problem)
        {
            return problem;
        }
        await this.WriteTenantDocumentAsync(tenantId, SignInPolicyKind, policy, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// What the organisation asks of a person before anything else (rc.14):
    /// "password" when their password must be changed (reset by an
    /// administrator, or shorter than the rules now ask), "twostep" when
    /// two-step sign-in is required and not set up; null otherwise. Passwords
    /// no longer expire (owner, 10 Oct 2026, P3).
    /// </summary>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The demand, or null.</returns>
    public async Task<string?> SignInDemandAsync(Guid personId, CancellationToken ct = default)
    {
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(personId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }
        SignInPolicy policy = await this.SignInPolicyOfAsync(context.Value.Tenant.Id, ct).ConfigureAwait(false);
        SecurityDocument security = await this.GetSecurityAsync(personId, ct).ConfigureAwait(false);
        if (security.MustChangePassword || security.PasswordBelowRules)
        {
            return "password";
        }
        bool required = await this.TwoStepRequiredAsync(policy, personId, ct).ConfigureAwait(false);
        return required && !security.TwoStepOn ? "twostep" : null;
    }

    /// <summary>Whether the organisation requires two-step sign-in of a person: of everyone, or of administrators.</summary>
    /// <param name="policy">The organisation's sign-in rules.</param>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when required.</returns>
    public async Task<bool> TwoStepRequiredAsync(SignInPolicy policy, Guid personId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.TwoStep == "everyone" || (policy.TwoStep == "admins" && await this.IsOrgAdminAsync(personId, ct).ConfigureAwait(false));
    }

    /// <summary>The people of an organisation (its shared mailboxes are not people), with what the console shows.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="sessions">Signed-in devices.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The people, by name.</returns>
    public async Task<IReadOnlyList<PersonView>> ListPeopleAsync(TenantRow tenant, SessionRegistry sessions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(sessions);
        Dictionary<Guid, SharedMailbox> shared = await this.SharedOfTenantAsync(tenant.Id, ct).ConfigureAwait(false);
        IReadOnlyList<Guid> admins = await this.AdminsOfAsync(tenant, ct).ConfigureAwait(false);
        List<LegalHold> holds = await this.ReadTenantDocumentAsync<List<LegalHold>>(tenant.Id, HoldsKind, ct).ConfigureAwait(false) ?? new List<LegalHold>();
        SignInPolicy policy = await this.SignInPolicyOfAsync(tenant.Id, ct).ConfigureAwait(false);
        IReadOnlyList<MailboxRow> all = await this.store.ListMailboxesAsync(tenant.Id, ct).ConfigureAwait(false);
        Dictionary<Guid, string> addressOf = all.ToDictionary(m => m.Id, m => m.Address);
        var people = new List<PersonView>();
        foreach (MailboxRow m in all.Where(m => !shared.ContainsKey(m.Id)))
        {
            SecurityDocument security = await this.GetSecurityAsync(m.Id, ct).ConfigureAwait(false);
            IReadOnlyList<SessionRecord> devices = await sessions.ListAsync(m.Id, null, ct).ConfigureAwait(false);
            string status = !m.Enabled ? "disabled"
                : m.PasswordPbkdf2.Length == 0 ? "invited"
                : security.MustChangePassword || security.PasswordBelowRules ? "must-change"
                : "active";
            var ways = new List<string>();
            if (security.TotpSecret.Length > 0)
            {
                ways.Add("Authenticator");
            }
            if (security.Passkeys.Count > 0)
            {
                ways.Add(security.Passkeys.Count == 1 ? "passkey" : $"{security.Passkeys.Count} passkeys");
            }
            string sharedText = string.Join(", ", shared
                .Where(s => s.Value.Members.ContainsKey(m.Id))
                .Select(s => (addressOf.TryGetValue(s.Key, out string? a) ? a.Split('@')[0] + "@" : "?") + " (" + RightWords(s.Value.Members[m.Id]) + ")"));
            people.Add(new PersonView(m, admins.Contains(m.Id), status, string.Join(", ", ways), devices.Count, devices.Count(d => d.Shared), sharedText, holds.Any(h => h.MailboxId == m.Id)));
        }
        return people.OrderBy(p => p.Mailbox.DisplayName.Length > 0 ? p.Mailbox.DisplayName : p.Mailbox.Address, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Invite a person: their mailbox is made, without a password, and an
    /// invitation link is returned (and sent to their personal address when
    /// given). Returns the error, or the link's token.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="name">Their name.</param>
    /// <param name="address">Their new address.</param>
    /// <param name="personal">A personal address to send the invitation to, or empty.</param>
    /// <param name="admin">True to make them an administrator.</param>
    /// <param name="invitedBy">Who invites, by name.</param>
    /// <param name="baseUrl">The webmail's address, for the link.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(string? Error, string? Token)> InviteAsync(TenantRow tenant, string name, string address, string personal, bool admin, string invitedBy, string baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(personal);
        ArgumentNullException.ThrowIfNull(baseUrl);
        string a = address.Trim().ToLowerInvariant();
        if (!Anjal.Mailbox.MailboxSink.TrySplitAddress(a, out string local, out string domain) || a.Contains('+', StringComparison.Ordinal))
        {
            return ("That is not an address.", null);
        }
        IReadOnlyList<TenantDomainRow> domains = await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false);
        if (!domains.Any(d => string.Equals(d.Domain, domain, StringComparison.OrdinalIgnoreCase)))
        {
            return ($"{domain} is not one of this organisation's domains.", null);
        }
        if (await this.store.GetMailboxAsync(local, domain, ct).ConfigureAwait(false) is not null)
        {
            return ("That address is already taken.", null);
        }
        OpsRecord ops = await this.ReadTenantDocumentAsync<OpsRecord>(tenant.Id, OpsKind, ct).ConfigureAwait(false) ?? new OpsRecord();
        int count = (await this.store.ListMailboxesAsync(tenant.Id, ct).ConfigureAwait(false)).Count;
        if (count >= ops.PeopleLimit)
        {
            return ($"This organisation has its {ops.PeopleLimit} people. Ask the Anjal operator to raise the limit.", null);
        }
        string p = personal.Trim();
        if (p.Length > 0 && RecoveryProblem(p, a) is string bad)
        {
            return (bad, null);
        }
        Branding branding = await this.ReadTenantDocumentAsync<Branding>(tenant.Id, BrandingKind, ct).ConfigureAwait(false) ?? new Branding();
        MailboxRow mailbox = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = local,
            Domain = domain,
            DisplayName = Clip(name, 100),
            Theme = MailboxRow.NormalizeTheme(branding.DefaultTheme + "-light"),
            Language = branding.DefaultLanguage,
            // DES-11 D2: the size the organisation's storage plan gives.
            QuotaBytes = (await StoragePlan.ReadAsync(this.store, tenant.Id, ct).ConfigureAwait(false)).OwnLimit(Guid.Empty),
        }, ct).ConfigureAwait(false);
        // rc.15 (item 22): a new person follows the organisation's Trash setting until they choose their own.
        if (admin)
        {
            await this.SetAdminAsync(tenant, mailbox.Id, true, ct).ConfigureAwait(false);
        }
        (string token, DateTimeOffset expires) = await this.CreateInvitationAsync(mailbox.Id, invitedBy, ct).ConfigureAwait(false);
        if (p.Length > 0)
        {
            SecurityDocument doc = await this.GetSecurityAsync(mailbox.Id, ct).ConfigureAwait(false);
            doc.RecoveryAddress = p.ToLowerInvariant();
            await this.WriteDocumentAsync(mailbox.Id, SecurityKind, doc, ct).ConfigureAwait(false);
            await this.SendInvitationMailAsync(mailbox, p, token, expires, invitedBy, baseUrl, ct).ConfigureAwait(false);
        }
        return (null, token);
    }

    /// <summary>
    /// An administrator resets a person's password: the old one stops
    /// working and a link to choose a new one is made (sent to their recovery
    /// address too, when they have one). Returns the link's token.
    /// </summary>
    /// <param name="personId">The person.</param>
    /// <param name="by">The administrator, by name.</param>
    /// <param name="baseUrl">The webmail's address, for the link.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> AdminResetPasswordAsync(Guid personId, string by, string baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(by);
        ArgumentNullException.ThrowIfNull(baseUrl);
        MailboxRow? mailbox = await this.store.GetMailboxByIdAsync(personId, ct).ConfigureAwait(false);
        if (mailbox is null)
        {
            return null;
        }
        mailbox.PasswordPbkdf2 = string.Empty;
        await this.store.UpsertMailboxAsync(mailbox, ct).ConfigureAwait(false);
        (string token, DateTimeOffset expires) = await this.CreateInvitationAsync(personId, by, ct).ConfigureAwait(false);
        SecurityDocument doc = await this.GetSecurityAsync(personId, ct).ConfigureAwait(false);
        doc.TrustedDevices.Clear();
        await this.WriteDocumentAsync(personId, SecurityKind, doc, ct).ConfigureAwait(false);
        if (doc.RecoveryAddress.Length > 0)
        {
            await this.SendInvitationMailAsync(mailbox, doc.RecoveryAddress, token, expires, by, baseUrl, ct).ConfigureAwait(false);
        }
        return token;
    }

    /// <summary>Turn off a person's two-step sign-in (a lost phone): authenticator, passkeys, backup codes and trusted devices.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task AdminTurnOffTwoStepAsync(Guid personId, CancellationToken ct = default)
    {
        SecurityDocument doc = await this.GetSecurityAsync(personId, ct).ConfigureAwait(false);
        doc.TotpSecret = string.Empty;
        doc.PendingTotpSecret = string.Empty;
        doc.Passkeys.Clear();
        doc.BackupCodes.Clear();
        doc.TrustedDevices.Clear();
        await this.WriteDocumentAsync(personId, SecurityKind, doc, ct).ConfigureAwait(false);
    }

    /// <summary>Disable or enable a person. A disabled person cannot sign in, and mail to them is refused.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="enabled">False to disable.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether the mailbox was found.</returns>
    public async Task<bool> SetEnabledAsync(Guid personId, bool enabled, CancellationToken ct = default)
    {
        MailboxRow? mailbox = await this.store.GetMailboxByIdAsync(personId, ct).ConfigureAwait(false);
        if (mailbox is null)
        {
            return false;
        }
        mailbox.Enabled = enabled;
        await this.store.UpsertMailboxAsync(mailbox, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// People from a list (CSV with a name and an address column, and
    /// optionally a personal address and a role): each is invited. Returns
    /// one line per row: the address and its invitation token, or why not.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="csv">The list.</param>
    /// <param name="invitedBy">Who invites.</param>
    /// <param name="baseUrl">The webmail's address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The results.</returns>
    public async Task<IReadOnlyList<(string Address, string? Token, string? Error)>> ImportPeopleAsync(TenantRow tenant, string csv, string invitedBy, string baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(csv);
        IReadOnlyList<IReadOnlyList<string>> rows = ReadCsvRows(csv.TrimStart('﻿'));
        var results = new List<(string, string?, string?)>();
        if (rows.Count < 2)
        {
            return results;
        }
        IReadOnlyList<string> head = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int nameCol = IndexOf(head, "name", "full name", "display name");
        int addrCol = IndexOf(head, "address", "email", "e-mail", "mail");
        int personalCol = IndexOf(head, "personal", "personal address", "recovery", "personal email");
        int roleCol = IndexOf(head, "role");
        foreach (IReadOnlyList<string> row in rows.Skip(1).Take(500))
        {
            string Cell(int i) => i >= 0 && i < row.Count ? row[i].Trim() : string.Empty;
            string address = Cell(addrCol);
            if (address.Length == 0)
            {
                continue;
            }
            (string? error, string? token) = await this.InviteAsync(tenant, Cell(nameCol), address, Cell(personalCol),
                Cell(roleCol).StartsWith("admin", StringComparison.OrdinalIgnoreCase), invitedBy, baseUrl, ct).ConfigureAwait(false);
            results.Add((address, token, error));
        }
        return results;
    }

    /// <summary>The shared mailboxes of an organisation, by mailbox.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The shared mailboxes.</returns>
    public async Task<Dictionary<Guid, SharedMailbox>> SharedOfTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await this.ReadTenantDocumentAsync<Dictionary<Guid, SharedMailbox>>(tenantId, SharedKind, ct).ConfigureAwait(false) ?? new Dictionary<Guid, SharedMailbox>();

    /// <summary>A shared mailbox's settings, or null when the mailbox is not shared.</summary>
    /// <param name="mailbox">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Its settings.</returns>
    public async Task<SharedMailbox?> SharedInfoAsync(MailboxRow mailbox, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        return (await this.SharedOfTenantAsync(mailbox.TenantId, ct).ConfigureAwait(false)).TryGetValue(mailbox.Id, out SharedMailbox? s) ? s : null;
    }

    /// <summary>A person's right on a shared mailbox: "read", "send", "manage", or null.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="sharedId">The shared mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The right.</returns>
    public async Task<string?> SharedRightAsync(Guid personId, Guid sharedId, CancellationToken ct = default)
    {
        MailboxRow? person = await this.store.GetMailboxByIdAsync(personId, ct).ConfigureAwait(false);
        MailboxRow? shared = await this.store.GetMailboxByIdAsync(sharedId, ct).ConfigureAwait(false);
        if (person is null || shared is null || person.TenantId != shared.TenantId || !shared.Enabled)
        {
            return null;
        }
        SharedMailbox? info = await this.SharedInfoAsync(shared, ct).ConfigureAwait(false);
        return info is not null && info.Members.TryGetValue(personId, out string? right) ? right : null;
    }

    /// <summary>The shared mailboxes a person may open, with their right and unread mail.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The mailboxes.</returns>
    public async Task<IReadOnlyList<(MailboxRow Mailbox, string Right, long Unread)>> SharedForPersonAsync(Guid personId, CancellationToken ct = default)
    {
        MailboxRow? person = await this.store.GetMailboxByIdAsync(personId, ct).ConfigureAwait(false);
        if (person is null)
        {
            return Array.Empty<(MailboxRow, string, long)>();
        }
        var result = new List<(MailboxRow, string, long)>();
        foreach ((Guid id, SharedMailbox info) in await this.SharedOfTenantAsync(person.TenantId, ct).ConfigureAwait(false))
        {
            if (info.Members.TryGetValue(personId, out string? right) && await this.store.GetMailboxByIdAsync(id, ct).ConfigureAwait(false) is MailboxRow m && m.Enabled)
            {
                FolderRow? inbox = await this.GetFolderAsync(id, FolderRow.Inbox, ct).ConfigureAwait(false);
                long unread = inbox is null ? 0 : await this.store.CountUnreadAsync(id, inbox.Id, ct).ConfigureAwait(false);
                result.Add((m, right, unread));
            }
        }
        return result.OrderBy(r => r.Item1.Address, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The Sender header for mail written in a mailbox (rc.14): the person,
    /// when the mailbox is a shared one that sends on behalf; empty otherwise.
    /// </summary>
    /// <param name="personId">The person writing.</param>
    /// <param name="mailboxId">The mailbox it is sent from.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>"Name &lt;address&gt;", or empty.</returns>
    public async Task<string> SenderForAsync(Guid? personId, Guid mailboxId, CancellationToken ct = default)
    {
        if (personId is not Guid person || person == mailboxId)
        {
            return string.Empty;
        }
        MailboxRow? box = await this.store.GetMailboxByIdAsync(mailboxId, ct).ConfigureAwait(false);
        MailboxRow? who = await this.store.GetMailboxByIdAsync(person, ct).ConfigureAwait(false);
        SharedMailbox? info = box is null ? null : await this.SharedInfoAsync(box, ct).ConfigureAwait(false);
        return info is null || who is null || info.SendMode != "behalf" ? string.Empty : FormatFrom(who);
    }

    /// <summary>Make a shared mailbox. Returns the error, or its id.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="address">Its address.</param>
    /// <param name="name">Its name, for example "Operations".</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(string? Error, Guid? Id)> CreateSharedMailboxAsync(TenantRow tenant, string address, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(name);
        string a = address.Trim().ToLowerInvariant();
        if (!Anjal.Mailbox.MailboxSink.TrySplitAddress(a, out string local, out string domain) || a.Contains('+', StringComparison.Ordinal))
        {
            return ("That is not an address.", null);
        }
        if (!(await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false)).Any(d => string.Equals(d.Domain, domain, StringComparison.OrdinalIgnoreCase)))
        {
            return ($"{domain} is not one of this organisation's domains.", null);
        }
        if (await this.store.GetMailboxAsync(local, domain, ct).ConfigureAwait(false) is not null)
        {
            return ("That address is already taken.", null);
        }
        MailboxRow m = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = local,
            Domain = domain,
            DisplayName = Clip(name.Trim().Length > 0 ? name : local, 100),
            QuotaBytes = (await StoragePlan.ReadAsync(this.store, tenant.Id, ct).ConfigureAwait(false)).OwnLimit(Guid.Empty),
        }, ct).ConfigureAwait(false);
        Dictionary<Guid, SharedMailbox> all = await this.SharedOfTenantAsync(tenant.Id, ct).ConfigureAwait(false);
        all[m.Id] = new SharedMailbox();
        await this.WriteTenantDocumentAsync(tenant.Id, SharedKind, all, ct).ConfigureAwait(false);
        return (null, m.Id);
    }

    /// <summary>Give a person a right on a shared mailbox, or take it away (right null). Returns the error, or null.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="sharedId">The shared mailbox.</param>
    /// <param name="personId">The person.</param>
    /// <param name="right">"read", "send", "manage", or null to remove.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SetSharedRightAsync(TenantRow tenant, Guid sharedId, Guid personId, string? right, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        Dictionary<Guid, SharedMailbox> all = await this.SharedOfTenantAsync(tenant.Id, ct).ConfigureAwait(false);
        if (!all.TryGetValue(sharedId, out SharedMailbox? info))
        {
            return "That is not a shared mailbox.";
        }
        MailboxRow? person = await this.store.GetMailboxByIdAsync(personId, ct).ConfigureAwait(false);
        if (person is null || person.TenantId != tenant.Id || all.ContainsKey(personId))
        {
            return "That person is not in this organisation.";
        }
        if (right is null)
        {
            info.Members.Remove(personId);
        }
        else if (SharedMailbox.Rights.Contains(right))
        {
            info.Members[personId] = right;
        }
        else
        {
            return "Choose read only, read and send, or manage.";
        }
        await this.WriteTenantDocumentAsync(tenant.Id, SharedKind, all, ct).ConfigureAwait(false);
        // A right taken away applies on the person's very next request.
        this.rights.TryRemove((personId, sharedId), out _);
        return null;
    }

    /// <summary>How a shared mailbox sends, and whether it keeps a copy.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="sharedId">The shared mailbox.</param>
    /// <param name="sendMode">"as" or "behalf".</param>
    /// <param name="keepSentCopy">Keep sent mail in it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it was saved.</returns>
    public async Task<bool> SetSharedSendingAsync(TenantRow tenant, Guid sharedId, string sendMode, bool keepSentCopy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        Dictionary<Guid, SharedMailbox> all = await this.SharedOfTenantAsync(tenant.Id, ct).ConfigureAwait(false);
        if (!all.TryGetValue(sharedId, out SharedMailbox? info) || sendMode is not ("as" or "behalf"))
        {
            return false;
        }
        info.SendMode = sendMode;
        info.KeepSentCopy = keepSentCopy;
        await this.WriteTenantDocumentAsync(tenant.Id, SharedKind, all, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>An organisation's retention rules.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rules.</returns>
    public async Task<RetentionPolicy> RetentionOfAsync(TenantRow tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        RetentionPolicy p = await this.ReadTenantDocumentAsync<RetentionPolicy>(tenant.Id, RetentionKind, ct).ConfigureAwait(false) ?? new RetentionPolicy();
        p.EvidenceYears = Math.Max(1, (int)Math.Round(tenant.EvidenceRetentionDays / 365.0));
        return p;
    }

    /// <summary>Save an organisation's retention rules, within Anjal's limits. Returns the error, or null.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="p">The rules.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SaveRetentionAsync(TenantRow tenant, RetentionPolicy p, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(p);
        if (p.TrashDays < MinTrashDays || p.TrashDays > MaxTrashDays)
        {
            return $"Trash is kept {MinTrashDays} to {MaxTrashDays} days.";
        }
        if (p.TrashMaxDays < p.TrashDays || p.TrashMaxDays > MaxTrashDays)
        {
            return $"The longest a person may choose is from the organisation's setting ({p.TrashDays} days) up to {MaxTrashDays} days.";
        }
        if (p.JunkDays < 7 || p.JunkDays > JunkDays)
        {
            return $"Junk is kept 7 to {JunkDays} days.";
        }
        if (p.OutboxDays < 1 || p.OutboxDays > 30 || p.AppCopyDays < 1 || p.AppCopyDays > 30)
        {
            return "Outbox copies and copies passed to applications are removed within 30 days.";
        }
        if (p.EvidenceYears < 1 || p.EvidenceYears > 10)
        {
            return "Evidence is kept 1 to 10 years after deletion.";
        }
        await this.WriteTenantDocumentAsync(tenant.Id, RetentionKind, p, ct).ConfigureAwait(false);
        tenant.EvidenceRetentionDays = p.EvidenceYears * 365;
        await this.store.UpsertTenantAsync(tenant, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>The legal holds of an organisation.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The holds.</returns>
    public async Task<IReadOnlyList<LegalHold>> HoldsOfAsync(Guid tenantId, CancellationToken ct = default) =>
        await this.ReadTenantDocumentAsync<List<LegalHold>>(tenantId, HoldsKind, ct).ConfigureAwait(false) ?? new List<LegalHold>();

    /// <summary>True when a mailbox is under a legal hold: nothing may be deleted from it.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it is held.</returns>
    public async Task<bool> IsHeldAsync(Guid mailboxId, CancellationToken ct = default)
    {
        MailboxRow? m = await this.store.GetMailboxByIdAsync(mailboxId, ct).ConfigureAwait(false);
        return m is not null && (await this.HoldsOfAsync(m.TenantId, ct).ConfigureAwait(false)).Any(h => h.MailboxId == mailboxId);
    }

    /// <summary>Place or lift a legal hold. Returns the error, or null.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="hold">True to place, false to lift.</param>
    /// <param name="by">Who.</param>
    /// <param name="reason">Why (placing).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SetHoldAsync(TenantRow tenant, Guid mailboxId, bool hold, string by, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(by);
        ArgumentNullException.ThrowIfNull(reason);
        MailboxRow? m = await this.store.GetMailboxByIdAsync(mailboxId, ct).ConfigureAwait(false);
        if (m is null || m.TenantId != tenant.Id)
        {
            return "That mailbox is not in this organisation.";
        }
        List<LegalHold> holds = (await this.HoldsOfAsync(tenant.Id, ct).ConfigureAwait(false)).ToList();
        holds.RemoveAll(h => h.MailboxId == mailboxId);
        if (hold)
        {
            if (reason.Trim().Length == 0)
            {
                return "Give the reason for the hold; it is kept with it.";
            }
            holds.Add(new LegalHold(mailboxId, DateTimeOffset.UtcNow, by, Clip(reason, 300)));
        }
        await this.WriteTenantDocumentAsync(tenant.Id, HoldsKind, holds, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>An organisation's applications.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The applications.</returns>
    public async Task<List<OrgApp>> AppsOfAsync(Guid tenantId, CancellationToken ct = default) =>
        await this.ReadTenantDocumentAsync<List<OrgApp>>(tenantId, AppsKind, ct).ConfigureAwait(false) ?? new List<OrgApp>();

    /// <summary>Add an application and its first key. Returns the error, or the key's user name and secret (shown once).</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="name">Its name.</param>
    /// <param name="description">What it does.</param>
    /// <param name="sendsAs">The address it sends as.</param>
    /// <param name="receives">What it receives, as a note.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(string? Error, string? AppId, string? Username, string? Secret)> AddAppAsync(TenantRow tenant, string name, string description, string sendsAs, string receives, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(sendsAs);
        ArgumentNullException.ThrowIfNull(receives);
        string a = sendsAs.Trim().ToLowerInvariant();
        if (name.Trim().Length == 0)
        {
            return ("Give the application a name.", null, null, null);
        }
        if (!Anjal.Mailbox.MailboxSink.TrySplitAddress(a, out _, out string domain)
            || !(await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false)).Any(d => string.Equals(d.Domain, domain, StringComparison.OrdinalIgnoreCase)))
        {
            return ("An application sends as an address on one of this organisation's domains.", null, null, null);
        }
        List<OrgApp> apps = await this.AppsOfAsync(tenant.Id, ct).ConfigureAwait(false);
        var app = new OrgApp { Id = SessionRegistry.NewId(), Name = Clip(name, 100), Description = Clip(description, 200), SendsAs = a, Receives = Clip(receives, 200), Created = DateTimeOffset.UtcNow };
        (AppKey key, string secret) = await this.NewAppKeyAsync(app, ct).ConfigureAwait(false);
        app.Keys.Add(key);
        apps.Add(app);
        await this.WriteTenantDocumentAsync(tenant.Id, AppsKind, apps, ct).ConfigureAwait(false);
        return (null, app.Id, key.Username, secret);
    }

    /// <summary>
    /// A new key for an application. With <paramref name="rotate"/> the
    /// current keys stop working in two weeks, so the application can move
    /// over. Returns the user name and secret (shown once), or null.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="rotate">True to retire the current keys.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(string Username, string Secret)?> NewKeyAsync(TenantRow tenant, string appId, bool rotate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        List<OrgApp> apps = await this.AppsOfAsync(tenant.Id, ct).ConfigureAwait(false);
        OrgApp? app = apps.Find(x => x.Id == appId);
        if (app is null)
        {
            return null;
        }
        if (rotate)
        {
            foreach (AppKey k in app.Keys.Where(k => k.EndsAt is null))
            {
                k.EndsAt = DateTimeOffset.UtcNow + KeyOverlap;
            }
        }
        (AppKey key, string secret) = await this.NewAppKeyAsync(app, ct).ConfigureAwait(false);
        app.Keys.Add(key);
        await this.WriteTenantDocumentAsync(tenant.Id, AppsKind, apps, ct).ConfigureAwait(false);
        return (key.Username, secret);
    }

    /// <summary>Revoke one key at once.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="keyId">The key.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it was found.</returns>
    public async Task<bool> RevokeKeyAsync(TenantRow tenant, string appId, string keyId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        List<OrgApp> apps = await this.AppsOfAsync(tenant.Id, ct).ConfigureAwait(false);
        AppKey? key = apps.Find(x => x.Id == appId)?.Keys.Find(k => k.Id == keyId);
        if (key is null)
        {
            return false;
        }
        await this.messageStore.DeleteSmtpUserAsync(key.Username, ct).ConfigureAwait(false);
        apps.Find(x => x.Id == appId)!.Keys.Remove(key);
        await this.WriteTenantDocumentAsync(tenant.Id, AppsKind, apps, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Remove keys whose time has ended (after a rotation), in every organisation.</summary>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many keys were removed.</returns>
    public async Task<int> RetireEndedKeysAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        int removed = 0;
        foreach (TenantRow tenant in await this.store.ListTenantsAsync(ct).ConfigureAwait(false))
        {
            List<OrgApp> apps = await this.AppsOfAsync(tenant.Id, ct).ConfigureAwait(false);
            bool changed = false;
            foreach (OrgApp app in apps)
            {
                foreach (AppKey key in app.Keys.Where(k => k.EndsAt is DateTimeOffset end && end <= now).ToList())
                {
                    await this.messageStore.DeleteSmtpUserAsync(key.Username, ct).ConfigureAwait(false);
                    app.Keys.Remove(key);
                    changed = true;
                    removed++;
                }
            }
            if (changed)
            {
                await this.WriteTenantDocumentAsync(tenant.Id, AppsKind, apps, ct).ConfigureAwait(false);
            }
        }
        return removed;
    }

    /// <summary>The organisation's templates (rc.14): shared with everyone in it.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The templates.</returns>
    public async Task<IReadOnlyList<MailTemplate>> OrgTemplatesAsync(Guid tenantId, CancellationToken ct = default) =>
        await this.ReadTenantDocumentAsync<List<MailTemplate>>(tenantId, OrgTemplatesKind, ct).ConfigureAwait(false) ?? new List<MailTemplate>();

    /// <summary>Save a message as one of the organisation's templates (administrators). Returns the error, or null.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="name">Its name.</param>
    /// <param name="subject">Its subject.</param>
    /// <param name="body">Its text.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SaveOrgTemplateAsync(Guid tenantId, string name, string subject, string body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(body);
        string title = Clip(name.Trim().Length > 0 ? name : subject, 100);
        if (title.Length == 0 && body.Trim().Length == 0)
        {
            return "Write something first: a template is made from the message you are writing.";
        }
        List<MailTemplate> all = (await this.OrgTemplatesAsync(tenantId, ct).ConfigureAwait(false)).ToList();
        all.RemoveAll(t => string.Equals(t.Name, title, StringComparison.OrdinalIgnoreCase));
        if (all.Count >= MaxOwnTemplates)
        {
            return "The organisation has the most templates allowed. Remove one first.";
        }
        all.Add(new MailTemplate("org/" + Guid.NewGuid().ToString("N"), OrgTemplatesGroup, title.Length > 0 ? title : "Template", Clip(subject, 300), Clip(body, 20_000)));
        await this.WriteTenantDocumentAsync(tenantId, OrgTemplatesKind, all, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Remove one of the organisation's templates.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="key">Its key.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it was removed.</returns>
    public async Task<bool> DeleteOrgTemplateAsync(Guid tenantId, string key, CancellationToken ct = default)
    {
        List<MailTemplate> all = (await this.OrgTemplatesAsync(tenantId, ct).ConfigureAwait(false)).ToList();
        int removed = all.RemoveAll(t => t.Key == key);
        if (removed > 0)
        {
            await this.WriteTenantDocumentAsync(tenantId, OrgTemplatesKind, all, ct).ConfigureAwait(false);
        }
        return removed > 0;
    }

    /// <summary>The group the organisation's templates are listed under.</summary>
    public const string OrgTemplatesGroup = "Organisation";

    /// <summary>
    /// The DNS records a domain needs, and - when <paramref name="lookup"/>
    /// can answer - what is published (rc.14, board OrgDomains).
    /// </summary>
    /// <param name="domain">The domain.</param>
    /// <param name="lookup">Finds TXT records by name, or MX hosts for "MX:" names; null to only list what is needed.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>One line per record.</returns>
    public async Task<IReadOnlyList<DnsRecordCheck>> DomainRecordsAsync(string domain, Func<string, Task<IReadOnlyList<string>?>>? lookup, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domain);
        string d = domain.Trim().ToLowerInvariant();
        string dkimValue = "(no DKIM key yet: create one with the admin API)";
        string selector = "default";
        if (await this.messageStore.GetDkimKeyAsync(d, ct).ConfigureAwait(false) is DkimKeyRow key)
        {
            selector = key.Selector;
            try
            {
                // DES-11 S6: a sealed key carries its public half in the clear; the private half is never opened here.
                byte[]? sealedPublic = KeySeal.DkimPublicKey(key.PrivateKeyPem);
                if (sealedPublic is null)
                {
                    using var rsa = RSA.Create();
                    rsa.ImportFromPem(key.PrivateKeyPem);
                    sealedPublic = rsa.ExportSubjectPublicKeyInfo();
                }
                dkimValue = "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(sealedPublic);
            }
            catch (CryptographicException)
            {
                dkimValue = "(the DKIM key could not be read)";
            }
        }
        var wanted = new List<(string Kind, string Name, string Type, string Value, Func<IReadOnlyList<string>, string> Judge)>
        {
            ("MX", "@", "MX", "10 " + this.hostName, found => found.Any(f => string.Equals(f.TrimEnd('.'), this.hostName, StringComparison.OrdinalIgnoreCase)) ? "pass" : found.Count == 0 ? "missing" : "different"),
            ("SPF", "@", "TXT", "v=spf1 mx -all", found => found.Any(f => f.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase) && (f.Contains(" mx", StringComparison.OrdinalIgnoreCase) || f.Contains(this.hostName, StringComparison.OrdinalIgnoreCase))) ? "pass" : found.Any(f => f.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)) ? "different" : "missing"),
            ("DKIM", selector + "._domainkey", "TXT", dkimValue, found => found.Any(f => dkimValue.StartsWith("v=DKIM1", StringComparison.Ordinal) && f.Replace(" ", string.Empty, StringComparison.Ordinal).Contains(dkimValue.Split("p=")[1], StringComparison.Ordinal)) ? "pass" : found.Count == 0 ? "missing" : "different"),
            ("DMARC", "_dmarc", "TXT", "v=DMARC1; p=quarantine; rua=mailto:dmarc-reports@" + d, found => found.Any(f => f.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)) ? "pass" : "missing"),
            ("MTA-STS", "_mta-sts", "TXT", "v=STSv1; id=" + DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "01", found => found.Any(f => f.StartsWith("v=STSv1", StringComparison.OrdinalIgnoreCase)) ? (MtaStsPolicy.ConfiguredMode() == "enforce" ? "pass" : "testing") : "missing"),
            ("TLS reports", "_smtp._tls", "TXT", "v=TLSRPTv1; rua=mailto:tls-reports@" + d, found => found.Any(f => f.StartsWith("v=TLSRPTv1", StringComparison.OrdinalIgnoreCase)) ? "pass" : "missing"),
        };
        var result = new List<DnsRecordCheck>();
        foreach ((string kind, string name, string type, string value, Func<IReadOnlyList<string>, string> judge) in wanted)
        {
            string status = "unchecked";
            if (lookup is not null)
            {
                string fqdn = name == "@" ? d : name + "." + d;
                IReadOnlyList<string>? found = await lookup(type == "MX" ? "MX:" + fqdn : fqdn).ConfigureAwait(false);
                status = found is null ? "unchecked" : judge(found);
            }
            result.Add(new DnsRecordCheck(kind, name, type, value, status));
        }
        return result;
    }

    private async Task<(AppKey Key, string Secret)> NewAppKeyAsync(OrgApp app, CancellationToken ct)
    {
        string secret = WebAuthn.ToBase64Url(RandomNumberGenerator.GetBytes(24));
        string id = SessionRegistry.NewId()[..10].ToLowerInvariant().Replace('-', 'x').Replace('_', 'y');
        string username = "app-" + id + "@" + app.SendsAs.Split('@')[1];
        await this.messageStore.UpsertSmtpUserAsync(new SmtpUserRow
        {
            Username = username,
            PasswordPbkdf2 = Anjal.Smtp.Pbkdf2Hasher.Hash(secret),
            AllowedFromDomains = new[] { app.SendsAs },
            Enabled = true,
        }, ct).ConfigureAwait(false);
        return (new AppKey { Id = id, Username = username, Fingerprint = Sha256Hex(secret)[..12], Created = DateTimeOffset.UtcNow }, secret);
    }

    private async Task SendInvitationMailAsync(MailboxRow mailbox, string to, string token, DateTimeOffset expires, string by, string baseUrl, CancellationToken ct)
    {
        string org = (await this.GetContextAsync(mailbox.Id, ct).ConfigureAwait(false)) is { } context && context.Tenant.DisplayName.Trim().Length > 0
            ? context.Tenant.DisplayName.Trim()
            : mailbox.Domain;
        string link = baseUrl.TrimEnd('/') + "/invite/" + token;
        string text = $"{by} has invited you to {org} mail. Your address is {mailbox.Address}.\r\n\r\n"
            + $"Choose your password here:\r\n\r\n    {link}\r\n\r\n"
            + $"The link works once, until {expires.UtcDateTime:d MMM yyyy} (UTC). If you were not expecting it, you can ignore this message.\r\n\r\n{org} mail";
        await this.SendSystemMailAsync(mailbox, to, $"Your {org} mail address is ready", text, ct).ConfigureAwait(false);
    }

    private static string RightWords(string right) => right switch { "manage" => "manage", "send" => "read and send", _ => "read only" };

    private static int IndexOf(IReadOnlyList<string> head, params string[] names)
    {
        for (int i = 0; i < head.Count; i++)
        {
            if (names.Contains(head[i]))
            {
                return i;
            }
        }
        return -1;
    }
}
