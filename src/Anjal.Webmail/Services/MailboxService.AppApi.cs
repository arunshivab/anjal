using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>An application inbox address as the console shows it.</summary>
/// <param name="Address">The address.</param>
/// <param name="SharedWith">The mailbox that also receives it, or null.</param>
/// <param name="AddedAt">When it was added.</param>
public sealed record AppInboxAddressView(string Address, MailboxRow? SharedWith, DateTimeOffset AddedAt);

/// <summary>An application's inbox on the console: its addresses, what waits, its storage.</summary>
/// <param name="Addresses">Its addresses.</param>
/// <param name="Waiting">Mails not yet acknowledged.</param>
/// <param name="Oldest">When the oldest waiting mail arrived.</param>
/// <param name="UsedBytes">The storage used.</param>
/// <param name="QuotaBytes">The storage limit.</param>
public sealed record AppInboxView(IReadOnlyList<AppInboxAddressView> Addresses, long Waiting, DateTimeOffset? Oldest, long UsedBytes, long QuotaBytes);

/// <summary>
/// rc.26 (DES-14, SPEC-17 Parts 1-3): the application API's settings, which only the console changes -
/// HTTP keys, allowed senders, limits, Allowed from, signing keys, encryption keys, inbox addresses,
/// webhooks, and the operator's patient-data mark. "A program cannot widen its own rights" (§24).
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>Anjal's ceiling on an application's sends an hour (the operator may raise it per organisation).</summary>
    public const int AppHourCeiling = 2000;

    /// <summary>Anjal's ceiling on an application's sends a day.</summary>
    public const int AppDayCeiling = 20000;

    /// <summary>Webhooks an application may have.</summary>
    public const int MaxWebhooks = 5;

    /// <summary>Inbox addresses an application may have.</summary>
    public const int MaxInboxAddresses = 20;

    /// <summary>Allowed from ranges an application may have.</summary>
    public const int MaxAllowedFrom = 20;

    /// <summary>The domain of applications' hidden mailboxes: never a real domain, so nothing routes to them.</summary>
    public const string AppMailboxDomain = "apps.invalid";

    /// <summary>Every event type an application may receive.</summary>
    public static readonly IReadOnlyList<string> AppEventTypes = new[]
    {
        "message.queued", "message.delivered", "message.deferred", "message.bounced", "message.failed", "message.suppressed",
        "message.replied", "message.complained", "inbox.received", "suppression.added", "suppression.removed", "webhook.test",
    };

    /// <summary>Raised whenever an organisation's applications change, so the API's key cache is read again.</summary>
    public static int AppsVersion => appsVersion;

    private static int appsVersion;

    private static readonly char[] ListSeparators = { '\n', '\r', ',', ' ' };

    /// <summary>One application of an organisation, or null.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The application, or null.</returns>
    public async Task<OrgApp?> AppOfAsync(Guid tenantId, string appId, CancellationToken ct = default) =>
        (await this.AppsOfAsync(tenantId, ct).ConfigureAwait(false)).Find(a => a.Id == appId);

    /// <summary>Change one application and save; the error from the change, or null.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="change">The change; returns an error to save nothing.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> ChangeAppAsync(Guid tenantId, string appId, Func<OrgApp, string?> change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        // Read, change and write as one step (rc.26 review): a key retired at the moment a request
        // notes the key's last use must stay retired.
        string? error = null;
        bool written = await this.store.ChangeTenantDocumentAsync(tenantId, AppsKind, json =>
        {
            List<OrgApp> apps = string.IsNullOrWhiteSpace(json) ? new List<OrgApp>() : System.Text.Json.JsonSerializer.Deserialize<List<OrgApp>>(json, DocumentJson) ?? new List<OrgApp>();
            OrgApp? app = apps.Find(a => a.Id == appId);
            if (app is null)
            {
                error = "That application is not this organisation's.";
                return null;
            }
            error = change(app);
            return error is null ? System.Text.Json.JsonSerializer.Serialize(apps, DocumentJson) : null;
        }, ct).ConfigureAwait(false);
        if (written)
        {
            this.tenantDocs.TryRemove((tenantId, AppsKind), out _);
            this.organisations.Clear();
            Interlocked.Increment(ref appsVersion);
        }
        return error;
    }

    // The apps document as stored now (the 30-second cache is for reading pages, not for changes).
    private async Task<List<OrgApp>> ReadFreshAppsAsync(Guid tenantId, CancellationToken ct)
    {
        string? json = await this.store.GetTenantDocumentAsync(tenantId, AppsKind, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json) ? new List<OrgApp>() : System.Text.Json.JsonSerializer.Deserialize<List<OrgApp>>(json) ?? new List<OrgApp>();
    }

    /// <summary>
    /// A new HTTP key (DES-14 §2): <c>ak_live_</c> or, for the sandbox, <c>ak_test_</c>, and 32 random
    /// characters. Shown once; Anjal keeps a salted hash and the fingerprint.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="sandbox">True for a sandbox key.</param>
    /// <param name="note">Where it is used.</param>
    /// <param name="expires">When it stops working by itself, or null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or the key and its fingerprint.</returns>
    public async Task<(string? Error, string? Key, string? Fingerprint)> NewHttpKeyAsync(TenantRow tenant, string appId, bool sandbox, string note, DateTimeOffset? expires, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(note);
        string key = (sandbox ? "ak_test_" : "ak_live_") + WebAuthn.ToBase64Url(RandomNumberGenerator.GetBytes(24));
        string fingerprint = HttpKeyFingerprint(key);
        string? error = await this.ChangeAppAsync(tenant.Id, appId, app =>
        {
            if (app.Retired is not null)
            {
                return "This application is retired; it cannot have a new key.";
            }
            if (expires is DateTimeOffset e && e <= DateTimeOffset.UtcNow)
            {
                return "Choose an end date in the future, or none.";
            }
            // D-286 (OPS-15 §3): two live keys of each kind, so that a change-over never stops sending.
            if (app.HttpKeys.Count(k => k.Sandbox == sandbox && k.Retired is null && (k.Expires is null || k.Expires > DateTimeOffset.UtcNow)) >= 2)
            {
                return "Two keys of this kind are live already. Retire one first.";
            }
            app.HttpKeys.Add(new AppHttpKey
            {
                Id = SessionRegistry.NewId()[..10],
                Fingerprint = fingerprint,
                Hash = Anjal.Smtp.Pbkdf2Hasher.Hash(key),
                Sandbox = sandbox,
                Note = Clip(note, 200),
                Created = DateTimeOffset.UtcNow,
                Expires = expires,
            });
            return null;
        }, ct).ConfigureAwait(false);
        return error is null ? (null, key, fingerprint) : (error, null, null);
    }

    /// <summary>An HTTP key's fingerprint: the first 8 characters of its SHA-256.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The fingerprint.</returns>
    public static string HttpKeyFingerprint(string key) => Sha256Hex(key)[..8];

    /// <summary>Retire an HTTP key: it answers 403 key_retired from now on.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="keyId">The key.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public Task<string?> RetireHttpKeyAsync(TenantRow tenant, string appId, string keyId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return this.ChangeAppAsync(tenant.Id, appId, app =>
        {
            AppHttpKey? key = app.HttpKeys.Find(k => k.Id == keyId);
            if (key is null)
            {
                return "That key is not this application's.";
            }
            key.Retired ??= DateTimeOffset.UtcNow;
            return null;
        }, ct);
    }

    /// <summary>The domains an organisation has, lower case.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The domains.</returns>
    public async Task<IReadOnlyList<string>> DomainsOfAsync(Guid tenantId, CancellationToken ct = default) =>
        (await this.store.ListTenantDomainsAsync(tenantId, ct).ConfigureAwait(false)).Select(d => d.Domain.ToLowerInvariant()).ToList();

    /// <summary>
    /// Save an application's sending settings (DES-14 §4, §6, §11, §12): the addresses it may send as,
    /// its limits, where requests may come from, and whether signing is required. The error, or null.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="senders">The addresses, one per line ("@domain" for any address on a domain).</param>
    /// <param name="perHour">Sends an hour.</param>
    /// <param name="perDay">Sends a day.</param>
    /// <param name="allowedFrom">CIDR ranges, one per line.</param>
    /// <param name="signingRequired">True for "Signing required".</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> SaveAppApiSettingsAsync(TenantRow tenant, string appId, string senders, int perHour, int perDay, string allowedFrom, bool signingRequired, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(senders);
        ArgumentNullException.ThrowIfNull(allowedFrom);
        IReadOnlyList<string> domains = await this.DomainsOfAsync(tenant.Id, ct).ConfigureAwait(false);
        OpsRecord ops = await this.ReadTenantDocumentAsync<OpsRecord>(tenant.Id, OpsKind, ct).ConfigureAwait(false) ?? new OpsRecord();
        int hourCeiling = ops.AppHourCeiling > 0 ? ops.AppHourCeiling : AppHourCeiling;
        int dayCeiling = ops.AppDayCeiling > 0 ? ops.AppDayCeiling : AppDayCeiling;
        var list = new List<string>();
        foreach (string line in senders.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string a = line.ToLowerInvariant();
            string domain = a.StartsWith('@') ? a[1..] : a.Contains('@', StringComparison.Ordinal) ? a[(a.LastIndexOf('@') + 1)..] : string.Empty;
            if (domain.Length == 0 || !domains.Contains(domain))
            {
                return "Each sender must be an address, or @domain, on one of this organisation's domains: " + line;
            }
            if (!list.Contains(a))
            {
                list.Add(a);
            }
        }
        if (perHour < 1 || perHour > hourCeiling || perDay < 1 || perDay > dayCeiling)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Choose up to {hourCeiling:N0} an hour and {dayCeiling:N0} a day.");
        }
        var ranges = new List<string>();
        foreach (string line in allowedFrom.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPNetwork.TryParse(line, out IPNetwork net))
            {
                return "Write each range in CIDR form, such as 10.0.0.0/8 or 2001:db8::/32: " + line;
            }
            if (!ops.AllowPrivateAddresses && IsPrivate(net.BaseAddress))
            {
                return "A private or loopback range needs the Anjal operator's permission for this organisation: " + line;
            }
            ranges.Add(net.ToString());
        }
        if (ranges.Count > MaxAllowedFrom)
        {
            return "At most 20 ranges.";
        }
        return await this.ChangeAppAsync(tenant.Id, appId, app =>
        {
            app.AllowedSenders = list;
            app.PerHour = perHour;
            app.PerDay = perDay;
            app.AllowedFrom = ranges;
            app.SigningRequired = signingRequired;
            return null;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Whether an address is private, loopback or link-local (DES-14 §9 point 4).</summary>
    /// <param name="address">The address.</param>
    /// <returns>True when it is.</returns>
    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast)
        {
            return true;
        }
        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] == 0
                || (b[0] == 192 && b[1] == 0 && b[2] == 0) || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) || b[0] >= 224;
        }
        // IPv6 forms that carry an IPv4 address or reach one through a gateway (NAT64 64:ff9b::/96 and
        // 64:ff9b:1::/48, 6to4 2002::/16, Teredo 2001::/32) are treated as private: a webhook has no need of them.
        return address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None)
            || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)
            || (b[0] == 0x20 && b[1] == 0x02)
            || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)
            || b.Take(10).All(x => x == 0);
    }

    /// <summary>Add the public half of a signing key (ECDSA P-256, PEM); the error, or null.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="pem">The public key.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public Task<string?> AddSigningKeyAsync(TenantRow tenant, string appId, string pem, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(pem);
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            return Task.FromResult<string?>("That is a private key. Paste the public key only; the private key stays with the application.");
        }
        string fingerprint;
        try
        {
            using ECDsa ec = ECDsa.Create();
            ec.ImportFromPem(pem);
            if (ec.KeySize != 256 || ec.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                return Task.FromResult<string?>("The key must be ECDSA on the P-256 curve.");
            }
            fingerprint = Convert.ToHexStringLower(SHA256.HashData(ec.ExportSubjectPublicKeyInfo()));
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return Task.FromResult<string?>("That is not an ECDSA public key in PEM form (-----BEGIN PUBLIC KEY-----).");
        }
        return this.ChangeAppAsync(tenant.Id, appId, app =>
        {
            if (app.SigningKeys.Exists(k => k.Fingerprint == fingerprint && k.Retired is null))
            {
                return "That key is already added.";
            }
            if (app.SigningKeys.Count(k => k.Retired is null) >= 2)
            {
                return "Two signing keys are live already. Retire one first.";
            }
            app.SigningKeys.Add(new AppSigningKey { Id = "sk_" + fingerprint[..12], Pem = pem.Trim(), Fingerprint = fingerprint, Created = DateTimeOffset.UtcNow });
            return null;
        }, ct);
    }

    /// <summary>Retire a signing key; the error, or null.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="keyId">The key (sk_...).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public Task<string?> RetireSigningKeyAsync(TenantRow tenant, string appId, string keyId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return this.ChangeAppAsync(tenant.Id, appId, app =>
        {
            AppSigningKey? key = app.SigningKeys.Find(k => k.Id == keyId && k.Retired is null);
            if (key is null)
            {
                return "That key is not a live key of this application.";
            }
            key.Retired = DateTimeOffset.UtcNow;
            return null;
        }, ct);
    }

    /// <summary>Add an encryption key (RSA, 3,072 bits or more, PEM); the error, or null. Two may be held.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="pem">The public key.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public Task<string?> AddEncryptionKeyAsync(TenantRow tenant, string appId, string pem, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(pem);
        if (AppEnvelope.Check(pem, out string fingerprint) is string bad)
        {
            return Task.FromResult<string?>(bad);
        }
        return this.ChangeAppAsync(tenant.Id, appId, app =>
        {
            if (app.EncryptionKeys.Exists(k => k.Fingerprint == fingerprint))
            {
                return "That key is already added.";
            }
            if (app.EncryptionKeys.Count >= 2)
            {
                return "Two keys are held already. Remove the older one first.";
            }
            app.EncryptionKeys.Add(new AppEncryptionKey { Pem = pem.Trim(), Fingerprint = fingerprint, Added = DateTimeOffset.UtcNow });
            return null;
        }, ct);
    }

    /// <summary>Remove an encryption key; the last one cannot go while the application has inbox addresses.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="fingerprint">The key's fingerprint.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> RemoveEncryptionKeyAsync(TenantRow tenant, string appId, string fingerprint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        bool hasAddresses = this.messageStore is IAppApiStore apps && (await apps.ListAppInboxAddressesAsync(tenant.Id, ct).ConfigureAwait(false)).Any(a => a.AppId == appId);
        return await this.ChangeAppAsync(tenant.Id, appId, app =>
        {
            AppEncryptionKey? key = app.EncryptionKeys.Find(k => k.Fingerprint == fingerprint);
            if (key is null)
            {
                return "That key is not this application's.";
            }
            if (app.EncryptionKeys.Count == 1 && hasAddresses)
            {
                return "Mail to this application is encrypted to this key. Add the new key first, or remove the inbox addresses.";
            }
            app.EncryptionKeys.Remove(key);
            return null;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Who already holds an address (DES-14 §13): a person's or shared mailbox, an extra address or a
    /// group - the mailbox delivery reaches; null when nobody does.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The mailbox, or null.</returns>
    public async Task<MailboxRow?> HolderOfAsync(string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        var sink = new Anjal.Mailbox.MailboxSink(this.store, this.maildir);
        return (await sink.ResolveAsync(address, ct).ConfigureAwait(false))?.Mailbox;
    }

    /// <summary>
    /// Give an application an inbox address (DES-14 §13). An address someone already holds needs
    /// <paramref name="share"/>: from then on each mail goes to both (D-227). Returns the error, or the
    /// holder to confirm with when <paramref name="share"/> was not given.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="adminAddress">The administrator, for the activity log.</param>
    /// <param name="appId">The application.</param>
    /// <param name="address">The address.</param>
    /// <param name="share">True when the administrator confirmed sharing with the holder.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, and the holder needing confirmation.</returns>
    public async Task<(string? Error, MailboxRow? ConfirmHolder)> AddInboxAddressAsync(TenantRow tenant, string adminAddress, string appId, string address, bool share, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(appId);
        if (this.messageStore is not IAppApiStore apps)
        {
            return ("This server's store cannot keep application inboxes.", null);
        }
        string a = address.Trim().ToLowerInvariant();
        if (!Anjal.Mailbox.MailboxSink.TrySplitAddress(a, out string local, out string domain) || !(await this.DomainsOfAsync(tenant.Id, ct).ConfigureAwait(false)).Contains(domain))
        {
            return ("An inbox address is on one of this organisation's domains.", null);
        }
        if (AppBounceAddress.IsBounceAddress(a) || local.Equals(Anjal.Mailbox.MailboxSink.UnsubscribeLocal, StringComparison.OrdinalIgnoreCase) || Anjal.Mailbox.MailboxSink.IsRoleAddress(local))
        {
            return ("That address is kept for Anjal's own use.", null);
        }
        OrgApp? app = await this.AppOfAsync(tenant.Id, appId, ct).ConfigureAwait(false);
        if (app is null || app.Retired is not null)
        {
            return ("That application is not this organisation's.", null);
        }
        if (app.EncryptionKeys.Count == 0)
        {
            return ("Upload the application's encryption key first: its mail is always encrypted to it.", null);
        }
        if (await apps.GetAppInboxAddressAsync(a, ct).ConfigureAwait(false) is AppInboxAddress taken)
        {
            return (taken.AppId == appId ? "The application has this address already." : "Another application has this address.", null);
        }
        if ((await apps.ListAppInboxAddressesAsync(tenant.Id, ct).ConfigureAwait(false)).Count(x => x.AppId == appId) >= MaxInboxAddresses)
        {
            return ("At most 20 inbox addresses.", null);
        }
        MailboxRow? holder = await this.HolderOfAsync(a, ct).ConfigureAwait(false);
        if (holder is not null && !share)
        {
            return (null, holder);
        }
        Guid inboxId = await this.EnsureAppMailboxAsync(tenant, appId, ct).ConfigureAwait(false);
        await apps.SetAppInboxAddressAsync(new AppInboxAddress { Address = a, TenantId = tenant.Id, AppId = appId, MailboxId = inboxId, SharedMailboxId = holder?.Id, AddedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
        await this.messageStore.AppendAuditAsync(new AuditEvent
        {
            Actor = adminAddress,
            Action = holder is null ? "org.app.inbox-added" : "org.app.inbox-shared",
            Subject = a,
            Detail = app.Name + (holder is null ? string.Empty : " (also delivered to " + holder.Address + ")"),
        }, ct).ConfigureAwait(false);
        return (null, null);
    }

    // The application's hidden mailbox (D-271): no sign-in, no person, no mailbox key; made once.
    private async Task<Guid> EnsureAppMailboxAsync(TenantRow tenant, string appId, CancellationToken ct)
    {
        OrgApp? app = await this.AppOfAsync(tenant.Id, appId, ct).ConfigureAwait(false);
        if (app?.InboxMailboxId is Guid known && await this.store.GetMailboxByIdAsync(known, ct).ConfigureAwait(false) is not null)
        {
            return known;
        }
        MailboxRow made = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "app-" + new string(appId.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray()),
            Domain = AppMailboxDomain,
            DisplayName = app?.Name ?? appId,
            PasswordPbkdf2 = string.Empty,
            Enabled = false,
            QuotaBytes = app?.InboxQuotaBytes ?? AppRecords.DefaultInboxQuota,
            Kind = MailboxRow.ApplicationKind,
        }, ct).ConfigureAwait(false);
        await this.ChangeAppAsync(tenant.Id, appId, a =>
        {
            a.InboxMailboxId = made.Id;
            return null;
        }, ct).ConfigureAwait(false);
        return made.Id;
    }

    /// <summary>Stop sharing an address: it goes back to its holder alone, and the application loses it.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="adminAddress">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="address">The address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> RemoveInboxAddressAsync(TenantRow tenant, string adminAddress, string appId, string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(address);
        if (this.messageStore is not IAppApiStore apps || await apps.GetAppInboxAddressAsync(address, ct).ConfigureAwait(false) is not AppInboxAddress row
            || row.TenantId != tenant.Id || row.AppId != appId)
        {
            return "That address is not this application's.";
        }
        await apps.RemoveAppInboxAddressAsync(row.Address, ct).ConfigureAwait(false);
        await this.messageStore.AppendAuditAsync(new AuditEvent
        {
            Actor = adminAddress,
            Action = row.SharedMailboxId is null ? "org.app.inbox-removed" : "org.app.inbox-unshared",
            Subject = row.Address,
            Detail = (await this.AppOfAsync(tenant.Id, appId, ct).ConfigureAwait(false))?.Name ?? appId,
        }, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>An application's inbox for the console.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="app">The application.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The inbox.</returns>
    public async Task<AppInboxView> AppInboxAsync(TenantRow tenant, OrgApp app, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(app);
        if (this.messageStore is not IAppApiStore apps)
        {
            return new AppInboxView(Array.Empty<AppInboxAddressView>(), 0, null, 0, app.InboxQuotaBytes);
        }
        var views = new List<AppInboxAddressView>();
        foreach (AppInboxAddress a in (await apps.ListAppInboxAddressesAsync(tenant.Id, ct).ConfigureAwait(false)).Where(a => a.AppId == app.Id))
        {
            MailboxRow? holder = a.SharedMailboxId is Guid h ? await this.store.GetMailboxByIdAsync(h, ct).ConfigureAwait(false) : null;
            views.Add(new AppInboxAddressView(a.Address, holder, a.AddedAt));
        }
        long waiting = 0;
        DateTimeOffset? oldest = null;
        long used = 0;
        if (app.InboxMailboxId is Guid box)
        {
            (waiting, oldest) = await apps.AppInboxWaitingAsync(box, ct).ConfigureAwait(false);
            used = (await this.store.GetMailboxByIdAsync(box, ct).ConfigureAwait(false))?.UsedBytes ?? 0;
        }
        return new AppInboxView(views, waiting, oldest, used, app.InboxQuotaBytes);
    }

    /// <summary>One mail of an application's inbox, as stored: its encrypted envelope (DES-14 §18); null when not there.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="app">The application.</param>
    /// <param name="messageId">The mail.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The envelope's bytes, or null.</returns>
    public async Task<byte[]?> AppInboxFileAsync(TenantRow tenant, OrgApp app, Guid messageId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(app);
        if (app.InboxMailboxId is not Guid box || await this.store.GetMessageByIdAsync(messageId, ct).ConfigureAwait(false) is not MessageRow m || m.MailboxId != box
            || await this.store.GetMailboxByIdAsync(box, ct).ConfigureAwait(false) is not MailboxRow hidden)
        {
            return null;
        }
        FolderRow? folder = (await this.store.ListFoldersAsync(box, ct).ConfigureAwait(false)).FirstOrDefault(f => f.Id == m.FolderId);
        return folder is null ? null : await this.maildir.ReadAsync(tenant.Slug, hidden.Address, folder.Name, m.MaildirFile, ct).ConfigureAwait(false);
    }

    /// <summary>Set an application inbox's storage limit, in GB (1 to 50, within the organisation's plan).</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="gb">The limit.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public Task<string?> SetAppInboxQuotaAsync(TenantRow tenant, string appId, int gb, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (gb < 1 || gb > 50)
        {
            return Task.FromResult<string?>("Choose from 1 to 50 GB.");
        }
        return this.ChangeAppAsync(tenant.Id, appId, app =>
        {
            app.InboxQuotaBytes = gb * 1024L * 1024 * 1024;
            return null;
        }, ct);
    }

    /// <summary>
    /// Add a webhook (DES-14 §8): https only, at most 5; a private or loopback address only where the
    /// operator allows it for the organisation. Anjal makes the secret.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="url">The address.</param>
    /// <param name="events">The event types; empty for all.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or the webhook.</returns>
    public async Task<(string? Error, AppWebhook? Hook)> AddWebhookAsync(TenantRow tenant, string appId, string url, IReadOnlyCollection<string> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(events);
        if (await this.WebhookUrlProblemAsync(tenant.Id, url, ct).ConfigureAwait(false) is string bad)
        {
            return (bad, null);
        }
        List<string> unknown = events.Where(e => !AppEventTypes.Contains(e)).ToList();
        if (unknown.Count > 0)
        {
            return ("Unknown event types: " + string.Join(", ", unknown), null);
        }
        var hook = new AppWebhook { Id = "wh_" + SessionRegistry.NewId()[..10], Url = url.Trim(), Events = events.Distinct().ToList(), Secret = "whsec_" + WebAuthn.ToBase64Url(RandomNumberGenerator.GetBytes(24)), Created = DateTimeOffset.UtcNow };
        string? error = await this.ChangeAppAsync(tenant.Id, appId, app =>
        {
            if (app.Webhooks.Count >= MaxWebhooks)
            {
                return "At most 5 webhooks.";
            }
            app.Webhooks.Add(hook);
            return null;
        }, ct).ConfigureAwait(false);
        return error is null ? (null, hook) : (error, null);
    }

    /// <summary>Whether the operator allowed this organisation's webhooks to reach private addresses.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when allowed.</returns>
    public async Task<bool> AllowsPrivateWebhooksAsync(Guid tenantId, CancellationToken ct = default) =>
        (await this.ReadTenantDocumentAsync<OpsRecord>(tenantId, OpsKind, ct).ConfigureAwait(false))?.AllowPrivateAddresses == true;

    /// <summary>What is wrong with a webhook address, or null.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="url">The address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The problem, or null.</returns>
    public async Task<string?> WebhookUrlProblemAsync(Guid tenantId, string url, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && !(AllowPlainWebhooks && uri.Scheme == Uri.UriSchemeHttp)))
        {
            return "A webhook address starts with https://.";
        }
        OpsRecord ops = await this.ReadTenantDocumentAsync<OpsRecord>(tenantId, OpsKind, ct).ConfigureAwait(false) ?? new OpsRecord();
        if (ops.AllowPrivateAddresses)
        {
            return null;
        }
        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out IPAddress? literal))
        {
            addresses = new[] { literal };
        }
        else if (uri.IsLoopback)
        {
            return "A private or loopback address needs the Anjal operator's permission for this organisation.";
        }
        else
        {
            try
            {
                addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
            }
            catch (System.Net.Sockets.SocketException)
            {
                return "That address's name does not resolve.";
            }
        }
        return addresses.Any(IsPrivate) ? "A private or loopback address needs the Anjal operator's permission for this organisation." : null;
    }

    /// <summary>Tests only: plain http:// webhooks (Anjal never allows them otherwise).</summary>
    public static bool AllowPlainWebhooks { get; set; }

    /// <summary>Remove a webhook.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="hookId">The webhook.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public Task<string?> RemoveWebhookAsync(TenantRow tenant, string appId, string hookId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return this.ChangeAppAsync(tenant.Id, appId, app => app.Webhooks.RemoveAll(h => h.Id == hookId) > 0 ? null : "That webhook is not this application's.", ct);
    }

    /// <summary>
    /// The operator sets or removes the patient-data mark (DES-14 §20, D-226), with a reason in the
    /// activity log. While it is set every request must be signed.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="on">Set or remove.</param>
    /// <param name="reason">Why.</param>
    /// <param name="operatorAddress">The operator.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> SetPatientDataAsync(TenantRow tenant, string appId, bool on, string reason, string operatorAddress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(operatorAddress);
        if (reason.Trim().Length < 5)
        {
            return "Write the reason; it goes in the activity log.";
        }
        string? error = await this.ChangeAppAsync(tenant.Id, appId, app =>
        {
            app.PatientData = on;
            app.PatientDataNote = string.Create(CultureInfo.InvariantCulture, $"{(on ? "Set" : "Removed")} by {operatorAddress} on {DateTimeOffset.UtcNow:yyyy-MM-dd}: {Clip(reason, 300)}");
            return null;
        }, ct).ConfigureAwait(false);
        if (error is null)
        {
            await this.messageStore.AppendAuditAsync(new AuditEvent
            {
                Actor = operatorAddress,
                Action = on ? "ops.app.patient-data-set" : "ops.app.patient-data-removed",
                Subject = tenant.Slug + "/" + appId,
                Detail = Clip(reason, 300),
            }, ct).ConfigureAwait(false);
        }
        return error;
    }

    /// <summary>
    /// The application API's daily work: old events, signatures, keys and sandbox sends cleared; mail
    /// acknowledged longer ago than the organisation keeps copies passed to applications (7 days by
    /// default, at most 30) cleared from application inboxes; inboxes not collected warned about at
    /// 7 and 30 days (D-228). Returns lines for the log.
    /// </summary>
    /// <param name="maildirRoot">Where mail files live.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What was done.</returns>
    public async Task<IReadOnlyList<string>> AppApiDailyAsync(string maildirRoot, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(maildirRoot);
        var lines = new List<string>();
        if (this.messageStore is not IAppApiStore apps)
        {
            return lines;
        }
        int pruned = await apps.PruneAppApiAsync(now, ct).ConfigureAwait(false);
        if (pruned > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"application API: {pruned} old rows cleared"));
        }
        foreach (TenantRow tenant in await this.store.ListTenantsAsync(ct).ConfigureAwait(false))
        {
            // The organisation's own choice (Records, retention: "Copies passed to applications", 1 to 30 days).
            TimeSpan keep = TimeSpan.FromDays((await this.RetentionOfAsync(tenant, ct).ConfigureAwait(false)).AppCopyDays);
            foreach (OrgApp app in await this.AppsOfAsync(tenant.Id, ct).ConfigureAwait(false))
            {
                if (app.InboxMailboxId is not Guid box || await this.store.GetMailboxByIdAsync(box, ct).ConfigureAwait(false) is not MailboxRow hidden)
                {
                    continue;
                }
                int cleared = 0;
                foreach (MessageRow m in await apps.ListAppInboxAsync(box, true, null, 1000, ct).ConfigureAwait(false))
                {
                    if (m.AcknowledgedAt is DateTimeOffset at && at < now - keep)
                    {
                        FolderRow? folder = (await this.store.ListFoldersAsync(box, ct).ConfigureAwait(false)).FirstOrDefault(f => f.Id == m.FolderId);
                        if (folder is not null)
                        {
                            this.maildir.Delete(tenant.Slug, hidden.Address, folder.Name, m.MaildirFile);
                        }
                        if (await this.store.DeleteMessageAsync(m.Id, ct).ConfigureAwait(false))
                        {
                            await this.store.AddMailboxUsageAsync(box, -m.SizeBytes, ct).ConfigureAwait(false);
                            cleared++;
                        }
                    }
                }
                if (cleared > 0)
                {
                    lines.Add(string.Create(CultureInfo.InvariantCulture, $"application inbox {tenant.Slug}/{app.Name}: {cleared} acknowledged mails cleared"));
                }
                (long waiting, DateTimeOffset? oldest) = await apps.AppInboxWaitingAsync(box, ct).ConfigureAwait(false);
                int days = oldest is DateTimeOffset o ? (int)(now - o).TotalDays : 0;
                int due = days >= 30 ? 30 : days >= 7 ? 7 : 0;
                if (waiting == 0 || due == 0)
                {
                    if (app.InboxWarned.Count > 0 && waiting == 0)
                    {
                        await this.ChangeAppAsync(tenant.Id, app.Id, a => { a.InboxWarned.Clear(); return null; }, ct).ConfigureAwait(false);
                    }
                    continue;
                }
                if (app.InboxWarned.Contains(due))
                {
                    continue;
                }
                await this.ChangeAppAsync(tenant.Id, app.Id, a => { a.InboxWarned.Add(due); return null; }, ct).ConfigureAwait(false);
                string text = string.Create(CultureInfo.InvariantCulture, $"{app.Name} is not collecting its mail: {waiting:N0} mails wait in its inbox, the oldest for {days} days. Nothing is deleted until the application acknowledges it; when the inbox is full, senders are asked to try later.");
                foreach (Guid admin in await this.AdminsOfAsync(tenant, ct).ConfigureAwait(false))
                {
                    if (await this.store.GetMailboxByIdAsync(admin, ct).ConfigureAwait(false) is MailboxRow a)
                    {
                        await this.SendSystemMailAsync(a, a.Address, app.Name + " is not collecting its mail", text + "\r\n\r\nAnjal", ct).ConfigureAwait(false);
                    }
                }
                if (due == 30 && await this.OperatorSenderAsync(Operators.List(), ct).ConfigureAwait(false) is MailboxRow op)
                {
                    foreach (string o2 in Operators.List())
                    {
                        await this.SendSystemMailAsync(op, o2, tenant.DisplayName + ": " + app.Name + " is not collecting its mail", text + "\r\n\r\nAnjal", ct).ConfigureAwait(false);
                    }
                }
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"application inbox {tenant.Slug}/{app.Name}: {waiting} waiting, {days} days; administrators told"));
            }
        }
        return lines;
    }
}
