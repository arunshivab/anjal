using Anjal.Api.Dto;
using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Api.Endpoints;

/// <summary>
/// Endpoint handlers for <c>/api/mailboxes</c> and <c>/api/messages</c>.
/// Passwords are accepted as plaintext in POST bodies, hashed via PBKDF2
/// before persisting, and NEVER returned. Creating a mailbox also lays
/// out its Maildir with the four default folders (INBOX, Sent, Drafts,
/// Trash) so the directory exists before the first delivery.
/// </summary>
public sealed class MailboxesHandler
{
    /// <summary>Folders created for every new mailbox.</summary>
    public static readonly System.Collections.Generic.IReadOnlyList<string> DefaultFolders =
        new[] { FolderRow.Inbox, "Sent", "Drafts", MailboxSink.JunkFolder, "Trash" };

    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 500;

    private readonly IMailboxStore store;
    private readonly IMaildirStore maildir;

    /// <summary>Construct.</summary>
    /// <param name="store">Backing store.</param>
    /// <param name="maildir">Filesystem store for message bodies.</param>
    public MailboxesHandler(IMailboxStore store, IMaildirStore maildir)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(maildir);
        this.store = store;
        this.maildir = maildir;
    }

    /// <summary><c>POST /api/mailboxes</c> - create or update.</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task PostAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        string body = await ctx.ReadBodyAsync().ConfigureAwait(false);
        MailboxRequest? req;
        try
        {
            req = ApiJson.Deserialize<MailboxRequest>(body);
        }
        catch (System.Text.Json.JsonException ex)
        {
            await ctx.WriteErrorAsync(400, "invalid_json", ex.Message).ConfigureAwait(false);
            return;
        }
        if (req is null)
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "Body is empty.").ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(req.TenantSlug))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "tenantSlug is required.").ConfigureAwait(false);
            return;
        }
        // Checked before anything is written: the local part becomes a
        // directory name, and a hostile one used to commit the row and then
        // fail on the maildir (DEF-043).
        if (!MailboxAddressRules.TryValidate(req.Address, out string local, out string domain) ||
            req.Address.Contains('+', System.StringComparison.Ordinal))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "address must be local@domain without a +tag; the local part may use letters, digits and . _ % - (no spaces or slashes) and the domain must be a real domain name.").ConfigureAwait(false);
            return;
        }
        if (req.Password.Length > 0 && req.Password.Length < 8)
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "password must be at least 8 characters.").ConfigureAwait(false);
            return;
        }
        if (req.QuotaBytes is long q && q < 0)
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "quotaBytes must not be negative.").ConfigureAwait(false);
            return;
        }

        TenantRow? tenant = await this.store.GetTenantAsync(req.TenantSlug.Trim()).ConfigureAwait(false);
        if (tenant is null)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No tenant '{req.TenantSlug}'.").ConfigureAwait(false);
            return;
        }
        TenantDomainRow? domainRow = await this.store.GetTenantDomainAsync(domain).ConfigureAwait(false);
        if (domainRow is null || domainRow.TenantId != tenant.Id)
        {
            await ctx.WriteErrorAsync(409, "domain_not_owned", $"Domain '{domain}' is not registered to tenant '{tenant.Slug}'.").ConfigureAwait(false);
            return;
        }

        string hash = req.Password.Length > 0 ? Anjal.Smtp.Pbkdf2Hasher.Hash(req.Password) : string.Empty;
        long quota = req.QuotaBytes is long qb && qb > 0 ? qb : MailboxRow.DefaultQuotaBytes;

        // Storage first, then the row: if the filesystem refuses the name
        // there is no mailbox left behind with nowhere to put mail (DEF-043).
        string address = local + "@" + domain;
        try
        {
            foreach (string folder in DefaultFolders)
            {
                this.maildir.EnsureFolder(tenant.Slug, address, folder);
            }
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.ArgumentException or System.NotSupportedException or System.UnauthorizedAccessException)
        {
            await ctx.WriteErrorAsync(400, "invalid_request", $"'{address}' cannot be used as a mailbox name on this server.").ConfigureAwait(false);
            return;
        }

        MailboxRow saved = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = local,
            Domain = domain,
            PasswordPbkdf2 = hash,
            DisplayName = req.DisplayName.Trim(),
            Enabled = req.Enabled,
            QuotaBytes = quota,
        }).ConfigureAwait(false);

        foreach (string folder in DefaultFolders)
        {
            await this.store.EnsureFolderAsync(saved.Id, folder).ConfigureAwait(false);
        }

        await ctx.WriteJsonAsync(200, ToResponse(saved)).ConfigureAwait(false);
    }

    /// <summary><c>GET /api/mailboxes[?tenant=slug]</c> - list.</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task ListAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.Guid? tenantId = null;
        string? filter = ctx.Query("tenant");
        if (!string.IsNullOrWhiteSpace(filter))
        {
            TenantRow? tenant = await this.store.GetTenantAsync(filter).ConfigureAwait(false);
            if (tenant is null)
            {
                await ctx.WriteErrorAsync(404, "not_found", $"No tenant '{filter}'.").ConfigureAwait(false);
                return;
            }
            tenantId = tenant.Id;
        }
        System.Collections.Generic.IReadOnlyList<MailboxRow> all = await this.store.ListMailboxesAsync(tenantId).ConfigureAwait(false);
        var responses = new System.Collections.Generic.List<MailboxResponse>(all.Count);
        foreach (MailboxRow m in all)
        {
            responses.Add(ToResponse(m));
        }
        await ctx.WriteJsonAsync(200, responses).ConfigureAwait(false);
    }

    /// <summary><c>GET /api/mailboxes/{address}</c> - fetch one.</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="address">Address from URL.</param>
    public async System.Threading.Tasks.Task GetAsync(RequestContext ctx, string address)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(address);
        MailboxRow? found = await this.FindAsync(ctx, address).ConfigureAwait(false);
        if (found is null)
        {
            return;
        }
        await ctx.WriteJsonAsync(200, ToResponse(found)).ConfigureAwait(false);
    }

    /// <summary><c>DELETE /api/mailboxes/{address}</c> - remove the mailbox and its index rows (Maildir files stay on disk).</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="address">Address from URL.</param>
    public async System.Threading.Tasks.Task DeleteAsync(RequestContext ctx, string address)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(address);
        if (!MailboxSink.TrySplitAddress(address, out string local, out string domain))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "address must be local@domain.").ConfigureAwait(false);
            return;
        }
        bool removed = await this.store.DeleteMailboxAsync(local, domain).ConfigureAwait(false);
        if (!removed)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No mailbox '{address}'.").ConfigureAwait(false);
            return;
        }
        await ctx.WriteEmptyAsync(204).ConfigureAwait(false);
    }

    /// <summary><c>GET /api/mailboxes/{address}/folders</c> - list folders with counts.</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="address">Address from URL.</param>
    public async System.Threading.Tasks.Task ListFoldersAsync(RequestContext ctx, string address)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(address);
        MailboxRow? mailbox = await this.FindAsync(ctx, address).ConfigureAwait(false);
        if (mailbox is null)
        {
            return;
        }
        System.Collections.Generic.IReadOnlyList<FolderRow> folders = await this.store.ListFoldersAsync(mailbox.Id).ConfigureAwait(false);
        var responses = new System.Collections.Generic.List<FolderResponse>(folders.Count);
        foreach (FolderRow f in folders)
        {
            long count = await this.store.CountMessagesAsync(mailbox.Id, f.Id).ConfigureAwait(false);
            responses.Add(new FolderResponse
            {
                Id = f.Id,
                Name = f.Name,
                MessageCount = count,
                CreatedAt = f.CreatedAt,
            });
        }
        await ctx.WriteJsonAsync(200, responses).ConfigureAwait(false);
    }

    /// <summary><c>GET /api/mailboxes/{address}/messages[?folder=INBOX&amp;limit=50&amp;offset=0]</c> - page message metadata, newest first.</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="address">Address from URL.</param>
    public async System.Threading.Tasks.Task ListMessagesAsync(RequestContext ctx, string address)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(address);
        MailboxRow? mailbox = await this.FindAsync(ctx, address).ConfigureAwait(false);
        if (mailbox is null)
        {
            return;
        }

        System.Guid? folderId = null;
        string? folderName = ctx.Query("folder");
        if (!string.IsNullOrEmpty(folderName))
        {
            FolderRow? folder = null;
            foreach (FolderRow f in await this.store.ListFoldersAsync(mailbox.Id).ConfigureAwait(false))
            {
                if (string.Equals(f.Name, folderName, System.StringComparison.Ordinal))
                {
                    folder = f;
                    break;
                }
            }
            if (folder is null)
            {
                await ctx.WriteErrorAsync(404, "not_found", $"No folder '{folderName}' in mailbox '{mailbox.Address}'.").ConfigureAwait(false);
                return;
            }
            folderId = folder.Id;
        }

        int limit = ParseIntOr(ctx.Query("limit"), DefaultPageSize);
        int offset = ParseIntOr(ctx.Query("offset"), 0);
        if (limit <= 0 || limit > MaxPageSize || offset < 0)
        {
            await ctx.WriteErrorAsync(400, "invalid_request", $"limit must be 1-{MaxPageSize} and offset must be >= 0.").ConfigureAwait(false);
            return;
        }

        System.Collections.Generic.IReadOnlyList<MessageRow> rows = await this.store.ListMessagesAsync(mailbox.Id, folderId, limit, offset).ConfigureAwait(false);
        long total = await this.store.CountMessagesAsync(mailbox.Id, folderId).ConfigureAwait(false);
        var page = new MessagePageResponse
        {
            Total = total,
            Offset = offset,
            Limit = limit,
        };
        foreach (MessageRow r in rows)
        {
            page.Items.Add(ToResponse(r));
        }
        await ctx.WriteJsonAsync(200, page).ConfigureAwait(false);
    }

    /// <summary><c>GET /api/messages/{id}</c> - one message's metadata.</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="idString">Identifier from URL.</param>
    public async System.Threading.Tasks.Task GetMessageAsync(RequestContext ctx, string idString)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(idString);
        MessageRow? found = await this.FindMessageAsync(ctx, idString).ConfigureAwait(false);
        if (found is null)
        {
            return;
        }
        await ctx.WriteJsonAsync(200, ToResponse(found)).ConfigureAwait(false);
    }

    /// <summary><c>GET /api/messages/{id}/raw</c> - the full message bytes, base64 encoded.</summary>
    /// <param name="ctx">Request context.</param>
    /// <param name="idString">Identifier from URL.</param>
    public async System.Threading.Tasks.Task GetMessageRawAsync(RequestContext ctx, string idString)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        System.ArgumentNullException.ThrowIfNull(idString);
        MessageRow? found = await this.FindMessageAsync(ctx, idString).ConfigureAwait(false);
        if (found is null)
        {
            return;
        }

        MailboxRow? mailbox = await this.store.GetMailboxByIdAsync(found.MailboxId).ConfigureAwait(false);
        TenantRow? tenant = mailbox is null ? null : await this.store.GetTenantByIdAsync(mailbox.TenantId).ConfigureAwait(false);
        FolderRow? folder = null;
        if (mailbox is not null)
        {
            foreach (FolderRow f in await this.store.ListFoldersAsync(mailbox.Id).ConfigureAwait(false))
            {
                if (f.Id == found.FolderId)
                {
                    folder = f;
                    break;
                }
            }
        }
        if (mailbox is null || tenant is null || folder is null)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"Message {found.Id} has no resolvable mailbox.").ConfigureAwait(false);
            return;
        }

        byte[]? bytes = await this.maildir.ReadAsync(tenant.Slug, mailbox.Address, folder.Name, found.MaildirFile).ConfigureAwait(false);
        if (bytes is null)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"Maildir file for message {found.Id} is missing on disk.").ConfigureAwait(false);
            return;
        }

        await ctx.WriteJsonAsync(200, new MessageRawResponse
        {
            Id = found.Id,
            RawBytesBase64 = System.Convert.ToBase64String(bytes),
        }).ConfigureAwait(false);
    }

    private async System.Threading.Tasks.Task<MailboxRow?> FindAsync(RequestContext ctx, string address)
    {
        if (!MailboxSink.TrySplitAddress(address, out string local, out string domain))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "address must be local@domain.").ConfigureAwait(false);
            return null;
        }
        MailboxRow? found = await this.store.GetMailboxAsync(local, domain).ConfigureAwait(false);
        if (found is null)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No mailbox '{address}'.").ConfigureAwait(false);
        }
        return found;
    }

    private async System.Threading.Tasks.Task<MessageRow?> FindMessageAsync(RequestContext ctx, string idString)
    {
        if (!System.Guid.TryParse(idString, out System.Guid id))
        {
            await ctx.WriteErrorAsync(400, "invalid_request", "Message id is not a valid UUID.").ConfigureAwait(false);
            return null;
        }
        MessageRow? found = await this.store.GetMessageByIdAsync(id).ConfigureAwait(false);
        if (found is null)
        {
            await ctx.WriteErrorAsync(404, "not_found", $"No message with id {id}.").ConfigureAwait(false);
        }
        return found;
    }

    private static int ParseIntOr(string? value, int fallback)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }
        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : -1;
    }

    private static MailboxResponse ToResponse(MailboxRow m) => new()
    {
        Id = m.Id,
        TenantId = m.TenantId,
        Address = m.Address,
        LocalPart = m.LocalPart,
        Domain = m.Domain,
        DisplayName = m.DisplayName,
        Enabled = m.Enabled,
        CanAuthenticate = m.PasswordPbkdf2.Length > 0,
        QuotaBytes = m.QuotaBytes,
        UsedBytes = m.UsedBytes,
        CreatedAt = m.CreatedAt,
        UpdatedAt = m.UpdatedAt,
    };

    private static MessageResponse ToResponse(MessageRow r) => new()
    {
        Id = r.Id,
        MailboxId = r.MailboxId,
        FolderId = r.FolderId,
        EnvelopeFrom = r.EnvelopeFrom,
        MessageId = r.MessageId,
        From = r.FromHeader,
        To = r.ToHeader,
        Subject = r.Subject,
        Date = r.DateHeader,
        SizeBytes = r.SizeBytes,
        Seen = r.Seen,
        Flagged = r.Flagged,
        Answered = r.Answered,
        SpamScore = r.SpamScore,
        ReceivedAt = r.ReceivedAt,
    };
}
