using Npgsql;

namespace Anjal.Store;

/// <summary>
/// <see cref="IMailboxStore"/> half of the PostgreSQL store. Tables:
/// <c>tenants</c>, <c>tenant_domains</c>, <c>mailboxes</c>, <c>folders</c>,
/// <c>messages</c> - see <c>tools/sql/schema.sql</c>.
/// </summary>
public sealed partial class PostgresMessageStore
{
    private const string TenantColumns = "id, slug, display_name, enabled, spam_threshold, created_at";
    private const string SenderRuleColumns = "id, tenant_id, pattern, action, created_at";
    private const string TenantDomainColumns = "id, tenant_id, domain, verified, created_at";
    private const string MailboxColumns = "id, tenant_id, local_part, domain, password_pbkdf2, display_name, enabled, quota_bytes, used_bytes, created_at, updated_at";
    private const string FolderColumns = "id, mailbox_id, name, created_at";
    private const string MessageColumns = "id, mailbox_id, folder_id, maildir_file, envelope_from, message_id, from_header, to_header, subject, date_header, size_bytes, seen, flagged, answered, spam_score, received_at";

    /// <inheritdoc/>
    public async Task<TenantRow> UpsertTenantAsync(TenantRow tenant, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(tenant);

        const string sql = @"
INSERT INTO tenants (slug, display_name, enabled, spam_threshold)
VALUES (lower(@slug), @display_name, @enabled, @spam_threshold)
ON CONFLICT (slug) DO UPDATE
    SET display_name   = EXCLUDED.display_name,
        enabled        = EXCLUDED.enabled,
        spam_threshold = EXCLUDED.spam_threshold
RETURNING " + TenantColumns + ";";

        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("slug", tenant.Slug.Trim());
        cmd.Parameters.AddWithValue("display_name", tenant.DisplayName);
        cmd.Parameters.AddWithValue("enabled", tenant.Enabled);
        cmd.Parameters.AddWithValue("spam_threshold", tenant.SpamThreshold);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadTenant(reader);
    }

    /// <inheritdoc/>
    public async Task<TenantRow?> GetTenantAsync(string slug, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(slug);

        const string sql = "SELECT " + TenantColumns + " FROM tenants WHERE slug = lower(@slug) LIMIT 1;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("slug", slug);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return ReadTenant(reader);
    }

    /// <inheritdoc/>
    public async Task<TenantRow?> GetTenantByIdAsync(System.Guid id, CancellationToken ct = default)
    {
        const string sql = "SELECT " + TenantColumns + " FROM tenants WHERE id = @id LIMIT 1;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return ReadTenant(reader);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TenantRow>> ListTenantsAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT " + TenantColumns + " FROM tenants ORDER BY slug;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<TenantRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadTenant(reader));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteTenantAsync(string slug, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(slug);

        const string sql = "DELETE FROM tenants WHERE slug = lower(@slug);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("slug", slug);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<TenantDomainRow> UpsertTenantDomainAsync(TenantDomainRow domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);

        const string sql = @"
INSERT INTO tenant_domains (tenant_id, domain, verified)
VALUES (@tenant_id, lower(@domain), @verified)
ON CONFLICT (domain) DO UPDATE
    SET tenant_id = EXCLUDED.tenant_id,
        verified  = EXCLUDED.verified
RETURNING " + TenantDomainColumns + ";";

        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tenant_id", domain.TenantId);
        cmd.Parameters.AddWithValue("domain", domain.Domain.Trim());
        cmd.Parameters.AddWithValue("verified", domain.Verified);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadTenantDomain(reader);
    }

    /// <inheritdoc/>
    public async Task<TenantDomainRow?> GetTenantDomainAsync(string domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);

        const string sql = "SELECT " + TenantDomainColumns + " FROM tenant_domains WHERE domain = lower(@domain) LIMIT 1;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("domain", domain);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return ReadTenantDomain(reader);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TenantDomainRow>> ListTenantDomainsAsync(System.Guid? tenantId = null, CancellationToken ct = default)
    {
        const string sql = "SELECT " + TenantDomainColumns + " FROM tenant_domains WHERE (@tenant_id IS NULL OR tenant_id = @tenant_id) ORDER BY domain;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("tenant_id", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = tenantId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<TenantDomainRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadTenantDomain(reader));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteTenantDomainAsync(string domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);

        const string sql = "DELETE FROM tenant_domains WHERE domain = lower(@domain);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("domain", domain);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<MailboxRow> UpsertMailboxAsync(MailboxRow mailbox, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(mailbox);

        // Password: only replaced when a non-empty hash is supplied.
        // used_bytes: never overwritten by an upsert.
        const string sql = @"
INSERT INTO mailboxes (tenant_id, local_part, domain, password_pbkdf2, display_name, enabled, quota_bytes)
VALUES (@tenant_id, lower(@local_part), lower(@domain), @password, @display_name, @enabled, @quota)
ON CONFLICT (local_part, domain) DO UPDATE
    SET tenant_id       = EXCLUDED.tenant_id,
        password_pbkdf2 = CASE WHEN EXCLUDED.password_pbkdf2 = '' THEN mailboxes.password_pbkdf2 ELSE EXCLUDED.password_pbkdf2 END,
        display_name    = EXCLUDED.display_name,
        enabled         = EXCLUDED.enabled,
        quota_bytes     = EXCLUDED.quota_bytes,
        updated_at      = now()
RETURNING " + MailboxColumns + ";";

        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tenant_id", mailbox.TenantId);
        cmd.Parameters.AddWithValue("local_part", mailbox.LocalPart.Trim());
        cmd.Parameters.AddWithValue("domain", mailbox.Domain.Trim());
        cmd.Parameters.AddWithValue("password", mailbox.PasswordPbkdf2);
        cmd.Parameters.AddWithValue("display_name", mailbox.DisplayName);
        cmd.Parameters.AddWithValue("enabled", mailbox.Enabled);
        cmd.Parameters.AddWithValue("quota", mailbox.QuotaBytes);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadMailbox(reader);
    }

    /// <inheritdoc/>
    public async Task<MailboxRow?> GetMailboxAsync(string localPart, string domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(localPart);
        System.ArgumentNullException.ThrowIfNull(domain);

        const string sql = "SELECT " + MailboxColumns + " FROM mailboxes WHERE local_part = lower(@local_part) AND domain = lower(@domain) LIMIT 1;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("local_part", localPart);
        cmd.Parameters.AddWithValue("domain", domain);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return ReadMailbox(reader);
    }

    /// <inheritdoc/>
    public async Task<MailboxRow?> GetMailboxByIdAsync(System.Guid id, CancellationToken ct = default)
    {
        const string sql = "SELECT " + MailboxColumns + " FROM mailboxes WHERE id = @id LIMIT 1;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return ReadMailbox(reader);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MailboxRow>> ListMailboxesAsync(System.Guid? tenantId = null, CancellationToken ct = default)
    {
        const string sql = "SELECT " + MailboxColumns + " FROM mailboxes WHERE (@tenant_id IS NULL OR tenant_id = @tenant_id) ORDER BY domain, local_part;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("tenant_id", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = tenantId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<MailboxRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadMailbox(reader));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteMailboxAsync(string localPart, string domain, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(localPart);
        System.ArgumentNullException.ThrowIfNull(domain);

        const string sql = "DELETE FROM mailboxes WHERE local_part = lower(@local_part) AND domain = lower(@domain);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("local_part", localPart);
        cmd.Parameters.AddWithValue("domain", domain);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<long?> AddMailboxUsageAsync(System.Guid mailboxId, long deltaBytes, CancellationToken ct = default)
    {
        const string sql = @"
UPDATE mailboxes SET used_bytes = GREATEST(0, used_bytes + @delta), updated_at = now()
WHERE id = @id
RETURNING used_bytes;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", mailboxId);
        cmd.Parameters.AddWithValue("delta", deltaBytes);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long used ? used : null;
    }

    /// <inheritdoc/>
    public async Task<FolderRow> EnsureFolderAsync(System.Guid mailboxId, string name, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(name);

        // ON CONFLICT DO UPDATE with a no-op assignment so RETURNING yields
        // the existing row on the second and later calls.
        const string sql = @"
INSERT INTO folders (mailbox_id, name) VALUES (@mailbox_id, @name)
ON CONFLICT (mailbox_id, name) DO UPDATE SET name = EXCLUDED.name
RETURNING " + FolderColumns + ";";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("mailbox_id", mailboxId);
        cmd.Parameters.AddWithValue("name", name);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadFolder(reader);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FolderRow>> ListFoldersAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        const string sql = "SELECT " + FolderColumns + " FROM folders WHERE mailbox_id = @mailbox_id ORDER BY (name <> 'INBOX'), name;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("mailbox_id", mailboxId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<FolderRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadFolder(reader));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<MessageRow> SaveMessageAsync(MessageRow message, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(message);

        const string sql = @"
INSERT INTO messages (mailbox_id, folder_id, maildir_file, envelope_from, message_id, from_header, to_header, subject, date_header, size_bytes, seen, flagged, answered, spam_score)
VALUES (@mailbox_id, @folder_id, @maildir_file, @envelope_from, @message_id, @from_header, @to_header, @subject, @date_header, @size_bytes, @seen, @flagged, @answered, @spam_score)
RETURNING " + MessageColumns + ";";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("mailbox_id", message.MailboxId);
        cmd.Parameters.AddWithValue("folder_id", message.FolderId);
        cmd.Parameters.AddWithValue("maildir_file", message.MaildirFile);
        cmd.Parameters.AddWithValue("envelope_from", message.EnvelopeFrom);
        cmd.Parameters.AddWithValue("message_id", message.MessageId);
        cmd.Parameters.AddWithValue("from_header", message.FromHeader);
        cmd.Parameters.AddWithValue("to_header", message.ToHeader);
        cmd.Parameters.AddWithValue("subject", message.Subject);
        cmd.Parameters.AddWithValue("date_header", message.DateHeader);
        cmd.Parameters.AddWithValue("size_bytes", message.SizeBytes);
        cmd.Parameters.AddWithValue("seen", message.Seen);
        cmd.Parameters.AddWithValue("flagged", message.Flagged);
        cmd.Parameters.AddWithValue("answered", message.Answered);
        cmd.Parameters.AddWithValue("spam_score", message.SpamScore);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadMailboxMessage(reader);
    }

    /// <inheritdoc/>
    public async Task<MessageRow?> GetMessageByIdAsync(System.Guid id, CancellationToken ct = default)
    {
        const string sql = "SELECT " + MessageColumns + " FROM messages WHERE id = @id LIMIT 1;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return ReadMailboxMessage(reader);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MessageRow>> ListMessagesAsync(System.Guid mailboxId, System.Guid? folderId, int limit, int offset, CancellationToken ct = default)
    {
        const string sql = @"
SELECT " + MessageColumns + @"
FROM messages
WHERE mailbox_id = @mailbox_id AND (@folder_id IS NULL OR folder_id = @folder_id)
ORDER BY received_at DESC, id DESC
LIMIT @limit OFFSET @offset;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("mailbox_id", mailboxId);
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("folder_id", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = folderId });
        cmd.Parameters.AddWithValue("limit", System.Math.Max(0, limit));
        cmd.Parameters.AddWithValue("offset", System.Math.Max(0, offset));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<MessageRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadMailboxMessage(reader));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<long> CountMessagesAsync(System.Guid mailboxId, System.Guid? folderId, CancellationToken ct = default)
    {
        const string sql = "SELECT count(*) FROM messages WHERE mailbox_id = @mailbox_id AND (@folder_id IS NULL OR folder_id = @folder_id);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("mailbox_id", mailboxId);
        cmd.Parameters.Add(new NpgsqlParameter<System.Guid?>("folder_id", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = folderId });
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long n ? n : 0;
    }

    /// <inheritdoc/>
    public async Task<MessageRow?> SetMessageFlagsAsync(System.Guid id, bool seen, bool flagged, bool answered, string? maildirFile, CancellationToken ct = default)
    {
        const string sql = @"
UPDATE messages
SET seen = @seen, flagged = @flagged, answered = @answered,
    maildir_file = COALESCE(@maildir_file, maildir_file)
WHERE id = @id
RETURNING " + MessageColumns + ";";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("seen", seen);
        cmd.Parameters.AddWithValue("flagged", flagged);
        cmd.Parameters.AddWithValue("answered", answered);
        cmd.Parameters.Add(new NpgsqlParameter<string?>("maildir_file", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = maildirFile });

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return ReadMailboxMessage(reader);
    }

    /// <inheritdoc/>
    public async Task<MessageRow?> MoveMessageAsync(System.Guid id, System.Guid folderId, string maildirFile, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(maildirFile);

        const string sql = @"
UPDATE messages SET folder_id = @folder_id, maildir_file = @maildir_file
WHERE id = @id
RETURNING " + MessageColumns + ";";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("folder_id", folderId);
        cmd.Parameters.AddWithValue("maildir_file", maildirFile);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return ReadMailboxMessage(reader);
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteMessageAsync(System.Guid id, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM messages WHERE id = @id;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<SenderRuleRow> UpsertSenderRuleAsync(SenderRuleRow rule, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(rule);

        const string sql = @"
INSERT INTO sender_rules (tenant_id, pattern, action)
VALUES (@tenant_id, lower(@pattern), @action)
ON CONFLICT (tenant_id, pattern) DO UPDATE SET action = EXCLUDED.action
RETURNING " + SenderRuleColumns + ";";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tenant_id", rule.TenantId);
        cmd.Parameters.AddWithValue("pattern", rule.Pattern.Trim());
        cmd.Parameters.AddWithValue("action", rule.Action == SenderRuleAction.Block ? "block" : "allow");

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadSenderRule(reader);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SenderRuleRow>> ListSenderRulesAsync(System.Guid tenantId, CancellationToken ct = default)
    {
        const string sql = "SELECT " + SenderRuleColumns + " FROM sender_rules WHERE tenant_id = @tenant_id ORDER BY pattern;";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tenant_id", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<SenderRuleRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadSenderRule(reader));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteSenderRuleAsync(System.Guid tenantId, string pattern, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(pattern);

        const string sql = "DELETE FROM sender_rules WHERE tenant_id = @tenant_id AND pattern = lower(@pattern);";
        await using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tenant_id", tenantId);
        cmd.Parameters.AddWithValue("pattern", pattern.Trim());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    private static TenantRow ReadTenant(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        Slug = r.GetString(1),
        DisplayName = r.GetString(2),
        Enabled = r.GetBoolean(3),
        SpamThreshold = r.GetInt32(4),
        CreatedAt = r.GetFieldValue<System.DateTimeOffset>(5),
    };

    private static SenderRuleRow ReadSenderRule(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        TenantId = r.GetGuid(1),
        Pattern = r.GetString(2),
        Action = string.Equals(r.GetString(3), "block", System.StringComparison.OrdinalIgnoreCase) ? SenderRuleAction.Block : SenderRuleAction.Allow,
        CreatedAt = r.GetFieldValue<System.DateTimeOffset>(4),
    };

    private static TenantDomainRow ReadTenantDomain(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        TenantId = r.GetGuid(1),
        Domain = r.GetString(2),
        Verified = r.GetBoolean(3),
        CreatedAt = r.GetFieldValue<System.DateTimeOffset>(4),
    };

    private static MailboxRow ReadMailbox(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        TenantId = r.GetGuid(1),
        LocalPart = r.GetString(2),
        Domain = r.GetString(3),
        PasswordPbkdf2 = r.GetString(4),
        DisplayName = r.GetString(5),
        Enabled = r.GetBoolean(6),
        QuotaBytes = r.GetInt64(7),
        UsedBytes = r.GetInt64(8),
        CreatedAt = r.GetFieldValue<System.DateTimeOffset>(9),
        UpdatedAt = r.GetFieldValue<System.DateTimeOffset>(10),
    };

    private static FolderRow ReadFolder(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        MailboxId = r.GetGuid(1),
        Name = r.GetString(2),
        CreatedAt = r.GetFieldValue<System.DateTimeOffset>(3),
    };

    private static MessageRow ReadMailboxMessage(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        MailboxId = r.GetGuid(1),
        FolderId = r.GetGuid(2),
        MaildirFile = r.GetString(3),
        EnvelopeFrom = r.GetString(4),
        MessageId = r.GetString(5),
        FromHeader = r.GetString(6),
        ToHeader = r.GetString(7),
        Subject = r.GetString(8),
        DateHeader = r.GetString(9),
        SizeBytes = r.GetInt64(10),
        Seen = r.GetBoolean(11),
        Flagged = r.GetBoolean(12),
        Answered = r.GetBoolean(13),
        SpamScore = r.GetInt32(14),
        ReceivedAt = r.GetFieldValue<System.DateTimeOffset>(15),
    };
}
