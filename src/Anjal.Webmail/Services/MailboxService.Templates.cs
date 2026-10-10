using System.Text;
using Anjal.Mime;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>Templates (rc.12, item 62): the starter set, and the person's own.</summary>
public sealed partial class MailboxService
{
    /// <summary>The kind of the document holding the person's own templates.</summary>
    public const string TemplatesKind = "templates";

    /// <summary>The group the person's own templates are listed under.</summary>
    public const string OwnTemplatesGroup = "Yours";

    /// <summary>At most this many own templates.</summary>
    public const int MaxOwnTemplates = 200;

    /// <summary>The person's own templates.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<MailTemplate>> ListOwnTemplatesAsync(Guid mailboxId, CancellationToken ct = default) =>
        await this.ReadDocumentAsync<List<MailTemplate>>(mailboxId, TemplatesKind, ct).ConfigureAwait(false) ?? new List<MailTemplate>();

    /// <summary>
    /// Save a message as one of the person's own templates (the board:
    /// "Save the current message as a template"). Blanks written in braces
    /// stay blanks. Returns a message, or null on success.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="name">The template's name; the subject when empty.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="body">The text.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SaveOwnTemplateAsync(Guid mailboxId, string name, string subject, string body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(body);
        string title = Clip(name.Trim().Length > 0 ? name : subject, 100);
        if (title.Length == 0 && body.Trim().Length == 0)
        {
            return "Write something first: a template is made from the message you are writing.";
        }
        if (title.Length == 0)
        {
            title = "My template";
        }
        List<MailTemplate> all = (await this.ListOwnTemplatesAsync(mailboxId, ct).ConfigureAwait(false)).ToList();
        all.RemoveAll(t => string.Equals(t.Name, title, StringComparison.OrdinalIgnoreCase));
        if (all.Count >= MaxOwnTemplates)
        {
            return "You have the most templates allowed. Remove one first.";
        }
        all.Add(new MailTemplate("own/" + Guid.NewGuid().ToString("N"), OwnTemplatesGroup, title, Clip(subject, 300), Clip(body, 20_000)));
        await this.WriteDocumentAsync(mailboxId, TemplatesKind, all, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Remove one of the person's own templates.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="key">The template's key.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> DeleteOwnTemplateAsync(Guid mailboxId, string key, CancellationToken ct = default)
    {
        List<MailTemplate> all = (await this.ListOwnTemplatesAsync(mailboxId, ct).ConfigureAwait(false)).ToList();
        int removed = all.RemoveAll(t => t.Key == key);
        if (removed > 0)
        {
            await this.WriteDocumentAsync(mailboxId, TemplatesKind, all, ct).ConfigureAwait(false);
        }
        return removed > 0;
    }

    /// <summary>A template by key: the starter set first, then the person's own.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="key">The key.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<MailTemplate?> FindTemplateAsync(Guid mailboxId, string? key, CancellationToken ct = default)
    {
        if (TemplateCatalogue.Find(key) is MailTemplate starter)
        {
            return starter;
        }
        if (key is not null && key.StartsWith("org/", StringComparison.Ordinal))
        {
            return (await this.OrgTemplatesForAsync(mailboxId, ct).ConfigureAwait(false)).FirstOrDefault(t => t.Key == key);
        }
        return (await this.ListOwnTemplatesAsync(mailboxId, ct).ConfigureAwait(false)).FirstOrDefault(t => t.Key == key);
    }

    /// <summary>The templates the person's organisation shares with everyone in it (rc.14).</summary>
    /// <param name="mailboxId">The person's mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The organisation's templates.</returns>
    public async Task<IReadOnlyList<MailTemplate>> OrgTemplatesForAsync(Guid mailboxId, CancellationToken ct = default) =>
        await this.TenantOfAsync(mailboxId, ct).ConfigureAwait(false) is Guid tenant
            ? await this.OrgTemplatesAsync(tenant, ct).ConfigureAwait(false)
            : Array.Empty<MailTemplate>();

    /// <summary>
    /// The blanks Anjal can fill for a draft: {Your name} and {Organisation}
    /// from the profile, {Name} from the first recipient's contact (or the
    /// name they were written with), and the festival's own line.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="to">The draft's To line.</param>
    /// <param name="festival">The festival chosen, for Festival wishes.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyDictionary<string, string>> KnownBlanksAsync(Guid mailboxId, string to, string? festival, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(to);
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is not null)
        {
            if (context.Value.Mailbox.DisplayName.Trim().Length > 0)
            {
                known["Your name"] = context.Value.Mailbox.DisplayName.Trim();
            }
            string org = context.Value.Tenant.DisplayName.Trim();
            if (org.Length > 0)
            {
                known["Organisation"] = org;
            }
        }
        IReadOnlyList<MailAddress> people = AddressParser.Parse(to);
        if (people.Count > 0)
        {
            Contact? c = await this.FindContactAsync(mailboxId, people[0].Address, ct).ConfigureAwait(false);
            string first = c is not null && c.FirstName.Length > 0 ? c.FirstName
                : people[0].DisplayName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (first.Length == 0)
            {
                IReadOnlyDictionary<string, string> names = await this.NamesForAsync(mailboxId, new[] { people[0].Address }, ct).ConfigureAwait(false);
                first = names.TryGetValue(people[0].Address, out string? n) ? n.Split(' ')[0] : string.Empty;
            }
            if (first.Length > 0)
            {
                known["Name"] = first;
            }
        }
        (string Name, string Line) fest = TemplateCatalogue.Festivals.FirstOrDefault(f => f.Name == festival);
        if (fest.Name is not null)
        {
            known["Festival"] = fest.Name;
            known["Festival line"] = fest.Line;
        }
        return known;
    }

    /// <summary>
    /// Use a template in a draft: its subject and text replace the draft's,
    /// with the blanks filled; the recipients and attachments stay. The
    /// suggested background, if any, is applied. Returns the draft's new id.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="draftId">The draft.</param>
    /// <param name="template">The template.</param>
    /// <param name="values">Every blank's value (known and typed).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<Guid?> ApplyTemplateAsync(Guid mailboxId, Guid draftId, MailTemplate template, IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        ComposeRequest? draft = await this.LoadDraftAsync(mailboxId, draftId, ct).ConfigureAwait(false);
        if (draft is null)
        {
            return null;
        }
        IReadOnlyList<AttachmentView> files = await this.ListAttachmentsAsync(mailboxId, draftId, ct).ConfigureAwait(false) ?? Array.Empty<AttachmentView>();
        draft.CarryFrom = draftId;
        foreach (AttachmentView a in files)
        {
            draft.CarryIndexes.Add(a.Index);
        }
        await this.AddCarriedAttachmentsAsync(mailboxId, draft, ct).ConfigureAwait(false);
        draft.Subject = TemplateCatalogue.Fill(template.Subject, values);
        draft.Body = TemplateCatalogue.Fill(template.Body, values);
        draft.BodyHtml = TemplateHtml(draft.Body, template.Background);
        draft.DraftId = draftId;
        return await this.SaveDraftAsync(mailboxId, draft, ct).ConfigureAwait(false);
    }

    /// <summary>A template's text as editor HTML, inside its suggested background when it has one.</summary>
    /// <param name="text">The filled text.</param>
    /// <param name="background">A picture's name, or empty.</param>
    public static string TemplateHtml(string text, string background)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder();
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            sb.Append("<div>").Append(line.Length == 0 ? "<br>" : HtmlSanitizer.Escape(line)).Append("</div>");
        }
        (string Name, string Colour, string Picture) pic = MailBackgrounds.Pictures.FirstOrDefault(p => p.Name == background);
        return pic.Name is null
            ? sb.ToString()
            : $"<div class=\"anjal-mailbg\" style=\"background-color:{pic.Colour};background-image:url({pic.Picture});padding:20px 24px;border-radius:10px\">{sb}</div>";
    }
}
