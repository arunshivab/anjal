using System.Text.Json;

namespace Anjal.Store;

/// <summary>An application's public encryption key (DES-14 §18), as the organisation's "apps" document keeps it.</summary>
public sealed class AppEncryptionKey
{
    /// <summary>The public key, PEM.</summary>
    public string Pem { get; set; } = string.Empty;

    /// <summary>Its fingerprint: the hex SHA-256 of its DER form.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>When it was added.</summary>
    public System.DateTimeOffset Added { get; set; }
}

/// <summary>
/// rc.26: what the mail server needs of an application - read from the organisation's "apps"
/// document, which the webmail's console writes (its <c>OrgApp</c> has these fields and more).
/// </summary>
public sealed class AppRecord
{
    /// <summary>The application's id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Its name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When it was retired, or null.</summary>
    public System.DateTimeOffset? Retired { get; set; }

    /// <summary>Its encryption keys; new mail uses the newest.</summary>
    public List<AppEncryptionKey> EncryptionKeys { get; set; } = new();

    /// <summary>Its inbox's storage limit, in bytes (2 GB unless the administrator changed it).</summary>
    public long InboxQuotaBytes { get; set; } = AppRecords.DefaultInboxQuota;

    /// <summary>Its hidden mailbox, once it has inbox addresses.</summary>
    public System.Guid? InboxMailboxId { get; set; }

    /// <summary>The operator's patient-data mark (DES-14 §20).</summary>
    public bool PatientData { get; set; }

    /// <summary>The key new mail is encrypted to, or null when none is uploaded.</summary>
    public AppEncryptionKey? NewestKey => this.EncryptionKeys.OrderByDescending(k => k.Added).FirstOrDefault();
}

/// <summary>rc.26: reading an application from the organisation's "apps" document.</summary>
public static class AppRecords
{
    /// <summary>The inbox storage limit an application starts with: 2 GB (D-228).</summary>
    public const long DefaultInboxQuota = 2L * 1024 * 1024 * 1024;

    /// <summary>One application of an organisation, or null.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The application, or null.</returns>
    public static async Task<AppRecord?> GetAsync(IMailboxStore store, System.Guid tenantId, string appId, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(appId);
        string? json = await store.GetTenantDocumentAsync(tenantId, DoNotSend.AppsKind, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return (JsonSerializer.Deserialize<List<AppRecord>>(json) ?? new List<AppRecord>()).Find(a => a.Id == appId);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
