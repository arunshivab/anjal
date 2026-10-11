using System.Globalization;
using System.Text;
using System.Text.Json;
using Anjal.Mime;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One saved contact (rc.12, items 28-30).</summary>
public sealed class Contact
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>First name.</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Last name.</summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>The mail address (unique within the book, compared without case).</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Organisation.</summary>
    public string Organisation { get; set; } = string.Empty;

    /// <summary>Phone.</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>Notes.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>When it was added.</summary>
    public DateTimeOffset Added { get; set; }

    /// <summary>The name to show: first and last name, or the address when there is none.</summary>
    public string DisplayName
    {
        get
        {
            string name = (this.FirstName.Trim() + " " + this.LastName.Trim()).Trim();
            return name.Length > 0 ? name : this.Address;
        }
    }
}

/// <summary>A named group of addresses, used as one recipient and for "Send one each" (item 64).</summary>
public sealed class ContactGroup
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The group's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The members' addresses.</summary>
    public IList<string> Addresses { get; init; } = new List<string>();
}

/// <summary>A person's contact settings (item 18).</summary>
public sealed class ContactSettings
{
    /// <summary>When on, the sender of a message you open is saved to contacts without asking.</summary>
    public bool SaveNewSendersAutomatically { get; set; }

    /// <summary>Addresses for which "Add to contacts" was dismissed, so it is not offered again.</summary>
    public IList<string> NotOffered { get; init; } = new List<string>();

    /// <summary>rc.15 (item 29): people you write to are added to your contacts. On unless turned off.</summary>
    public bool SaveRecipientsAutomatically { get; set; } = true;

    /// <summary>rc.15: messages whose new-sender window was answered "Not now" - not asked again for that mail.</summary>
    public IList<string> SkippedMessages { get; init; } = new List<string>();
}

/// <summary>rc.15: mail between the person and one contact.</summary>
/// <param name="Sent">Messages sent to them.</param>
/// <param name="Received">Messages received from them.</param>
/// <param name="Last">When the latest of either was, or null.</param>
/// <param name="LastWasSent">True when the latest was sent to them; false when it came from them.</param>
public sealed record ContactTraffic(int Sent, int Received, DateTimeOffset? Last, bool LastWasSent);

/// <summary>What an import did.</summary>
/// <param name="Added">New contacts.</param>
/// <param name="Updated">Existing contacts given details they lacked.</param>
/// <param name="Skipped">Lines with no valid address.</param>
public sealed record ImportResult(int Added, int Updated, int Skipped);

/// <summary>Contacts, contact groups and contact settings (rc.12, items 18, 28-30).</summary>
public sealed partial class MailboxService
{
    /// <summary>The kind of the contacts document.</summary>
    public const string ContactsKind = "contacts";

    /// <summary>The kind of the contact groups document.</summary>
    public const string GroupsKind = "contact-groups";

    /// <summary>The kind of the contact settings document.</summary>
    public const string ContactSettingsKind = "contact-settings";

    /// <summary>At most this many contacts in one book.</summary>
    public const int MaxContacts = 20_000;

    private static readonly System.Buffers.SearchValues<char> CsvSpecial = System.Buffers.SearchValues.Create(",\"\r\n");

    private static readonly JsonSerializerOptions DocumentJson = new() { WriteIndented = false };

    /// <summary>Every saved contact, in name order.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<Contact>> ListContactsAsync(Guid mailboxId, CancellationToken ct = default)
    {
        List<Contact> all = await this.ReadDocumentAsync<List<Contact>>(mailboxId, ContactsKind, ct).ConfigureAwait(false) ?? new List<Contact>();
        return all.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The saved contact for an address, or null.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="address">The address.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<Contact?> FindContactAsync(Guid mailboxId, string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        IReadOnlyList<Contact> all = await this.ListContactsAsync(mailboxId, ct).ConfigureAwait(false);
        return all.FirstOrDefault(c => string.Equals(c.Address, address.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Add or change a contact. A contact with the same address as another is
    /// refused, so the book never holds the same address twice. Returns a
    /// message for the person, or null on success.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="contact">The contact; an unknown id adds it.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SaveContactAsync(Guid mailboxId, Contact contact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        string address = contact.Address.Trim();
        IReadOnlyList<MailAddress> parsed = AddressParser.Parse(address);
        if (parsed.Count != 1)
        {
            return "Enter one valid mail address.";
        }
        contact.Address = parsed[0].Address;
        contact.FirstName = Clip(contact.FirstName, 100);
        contact.LastName = Clip(contact.LastName, 100);
        contact.Organisation = Clip(contact.Organisation, 200);
        contact.Phone = Clip(contact.Phone, 50);
        contact.Notes = Clip(contact.Notes, 2000);
        List<Contact> all = (await this.ListContactsAsync(mailboxId, ct).ConfigureAwait(false)).ToList();
        if (all.Any(c => c.Id != contact.Id && string.Equals(c.Address, contact.Address, StringComparison.OrdinalIgnoreCase)))
        {
            return "There is already a contact with this address.";
        }
        int index = all.FindIndex(c => c.Id == contact.Id);
        if (index >= 0)
        {
            contact.Added = all[index].Added;
            all[index] = contact;
        }
        else
        {
            if (all.Count >= MaxContacts)
            {
                return "The contact list is full.";
            }
            contact.Added = contact.Added == default ? DateTimeOffset.UtcNow : contact.Added;
            all.Add(contact);
        }
        await this.WriteDocumentAsync(mailboxId, ContactsKind, all, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// rc.15 (item 29, owner 7 Oct): everyone a message is written to - To, Cc and Bcc - who is not
    /// yet a contact is added, named from what was written ("Meera Iyer &lt;meera@...&gt;" gives
    /// Meera and Iyer). Names already in the book are never changed. Lists uploaded for
    /// "Send one each" are not in these fields, so they never fill the book.
    /// </summary>
    /// <param name="personId">Whose contacts.</param>
    /// <param name="ownAddress">The sender's own address, never added.</param>
    /// <param name="request">The message as written.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many people were added.</returns>
    public async Task<int> RememberRecipientsAsync(Guid personId, string ownAddress, ComposeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ownAddress);
        ArgumentNullException.ThrowIfNull(request);
        ContactSettings settings = await this.GetContactSettingsAsync(personId, ct).ConfigureAwait(false);
        if (!settings.SaveRecipientsAutomatically)
        {
            return 0;
        }
        List<Contact> all = (await this.ListContactsAsync(personId, ct).ConfigureAwait(false)).ToList();
        var known = new HashSet<string>(all.Select(c => c.Address), StringComparer.OrdinalIgnoreCase) { ownAddress.Trim() };
        int added = 0;
        foreach (MailAddress a in AddressParser.Parse(request.To).Concat(AddressParser.Parse(request.Cc)).Concat(AddressParser.Parse(request.Bcc)))
        {
            if (all.Count >= MaxContacts || !known.Add(a.Address))
            {
                continue;
            }
            (string first, string last) = SplitName(a.DisplayName, a.Address);
            all.Add(new Contact { Address = a.Address, FirstName = Clip(first, 100), LastName = Clip(last, 100), Added = DateTimeOffset.UtcNow });
            added++;
        }
        if (added > 0)
        {
            await this.WriteDocumentAsync(personId, ContactsKind, all, ct).ConfigureAwait(false);
        }
        return added;
    }

    /// <summary>A written name split into first and last: the last word is the last name.</summary>
    /// <param name="name">The name as written; may be empty.</param>
    /// <param name="address">The address, so an address written as the name gives no name.</param>
    public static (string First, string Last) SplitName(string? name, string address)
    {
        string n = (name ?? string.Empty).Trim().Trim('"').Trim();
        if (n.Length == 0 || string.Equals(n, address, StringComparison.OrdinalIgnoreCase))
        {
            return (string.Empty, string.Empty);
        }
        int space = n.LastIndexOf(' ');
        return space <= 0 ? (n, string.Empty) : (n[..space].Trim(), n[(space + 1)..].Trim());
    }

    /// <summary>
    /// rc.15 (owner, 7 Oct): how much mail has passed between the person and a contact - sent to
    /// them (Sent, where they are in To), received from them (any folder but Sent, Drafts, Junk and
    /// Trash) and the latest of either. Counts the newest 5,000 matches, enough for one mailbox.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="address">The contact's address.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<ContactTraffic> ContactTrafficAsync(Guid mailboxId, string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        string who = address.Trim();
        var names = new Dictionary<Guid, string>();
        foreach (FolderRow f in await this.store.ListFoldersAsync(mailboxId, ct).ConfigureAwait(false))
        {
            names[f.Id] = f.Name;
        }
        int sent = 0;
        int received = 0;
        MessageRow? last = null;
        bool lastSent = false;
        for (int offset = 0; offset < 5000; offset += 500)
        {
            IReadOnlyList<MessageRow> chunk = await this.store.SearchMessagesAsync(mailboxId, null, who, 500, offset, ct).ConfigureAwait(false);
            foreach (MessageRow m in chunk)
            {
                string folder = names.TryGetValue(m.FolderId, out string? n) ? n : string.Empty;
                bool isSent = folder == "Sent" && m.ToHeader.Contains(who, StringComparison.OrdinalIgnoreCase);
                bool isReceived = folder is not ("Sent" or "Drafts" or "Trash" or Anjal.Mailbox.MailboxSink.JunkFolder)
                    && (m.EnvelopeFrom.Equals(who, StringComparison.OrdinalIgnoreCase) || EncodedWordDecoder.Decode(m.FromHeader).Contains(who, StringComparison.OrdinalIgnoreCase));
                if (isSent)
                {
                    sent++;
                }
                if (isReceived)
                {
                    received++;
                }
                if ((isSent || isReceived) && (last is null || m.ReceivedAt > last.ReceivedAt))
                {
                    last = m;
                    lastSent = isSent;
                }
            }
            if (chunk.Count < 500)
            {
                break;
            }
        }
        return new ContactTraffic(sent, received, last?.ReceivedAt, lastSent);
    }

    /// <summary>Remove a contact. Returns true when it was there.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="contactId">The contact.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> DeleteContactAsync(Guid mailboxId, Guid contactId, CancellationToken ct = default)
    {
        List<Contact> all = (await this.ListContactsAsync(mailboxId, ct).ConfigureAwait(false)).ToList();
        int removed = all.RemoveAll(c => c.Id == contactId);
        if (removed > 0)
        {
            await this.WriteDocumentAsync(mailboxId, ContactsKind, all, ct).ConfigureAwait(false);
        }
        return removed > 0;
    }

    /// <summary>
    /// Merge contacts that share an address (from older imports): one is kept,
    /// and every detail the others had and it lacked is added to it - nothing
    /// typed is lost. Returns how many were merged away.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<int> MergeDuplicateContactsAsync(Guid mailboxId, CancellationToken ct = default)
    {
        List<Contact> all = (await this.ListContactsAsync(mailboxId, ct).ConfigureAwait(false)).ToList();
        var kept = new List<Contact>();
        int merged = 0;
        foreach (IGrouping<string, Contact> same in all.GroupBy(c => c.Address.ToLowerInvariant()))
        {
            Contact first = same.OrderBy(c => c.Added).First();
            foreach (Contact other in same.Where(c => c.Id != first.Id))
            {
                Fill(first, other);
                merged++;
            }
            kept.Add(first);
        }
        if (merged > 0)
        {
            await this.WriteDocumentAsync(mailboxId, ContactsKind, kept, ct).ConfigureAwait(false);
        }
        return merged;
    }

    /// <summary>
    /// Import contacts from a CSV (first name, last name, address, organisation,
    /// phone - a header row naming the columns is read when present) or from
    /// vCards. An address already in the book only gains the details it lacks.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="text">The file's text.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<ImportResult> ImportContactsAsync(Guid mailboxId, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        IReadOnlyList<Contact> incoming = text.Contains("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase)
            ? ParseVCards(text)
            : ParseCsv(text);
        List<Contact> all = (await this.ListContactsAsync(mailboxId, ct).ConfigureAwait(false)).ToList();
        int added = 0, updated = 0, skipped = 0;
        foreach (Contact c in incoming)
        {
            IReadOnlyList<MailAddress> parsed = AddressParser.Parse(c.Address.Trim());
            if (parsed.Count != 1)
            {
                skipped++;
                continue;
            }
            c.Address = parsed[0].Address;
            Contact? existing = all.FirstOrDefault(x => string.Equals(x.Address, c.Address, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (Fill(existing, c))
                {
                    updated++;
                }
                continue;
            }
            if (all.Count >= MaxContacts)
            {
                skipped++;
                continue;
            }
            c.Id = Guid.NewGuid();
            c.Added = DateTimeOffset.UtcNow;
            all.Add(c);
            added++;
        }
        if (added + updated > 0)
        {
            await this.WriteDocumentAsync(mailboxId, ContactsKind, all, ct).ConfigureAwait(false);
        }
        return new ImportResult(added, updated, skipped);
    }

    /// <summary>The contacts as CSV, with a header row.</summary>
    /// <param name="contacts">The contacts.</param>
    public static string ContactsToCsv(IEnumerable<Contact> contacts)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        var sb = new StringBuilder("First name,Last name,Address,Organisation,Phone,Notes\r\n");
        foreach (Contact c in contacts)
        {
            sb.Append(string.Join(',', new[] { c.FirstName, c.LastName, c.Address, c.Organisation, c.Phone, c.Notes }.Select(CsvField))).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>The contacts as vCard 3.0.</summary>
    /// <param name="contacts">The contacts.</param>
    public static string ContactsToVCard(IEnumerable<Contact> contacts)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        var sb = new StringBuilder();
        foreach (Contact c in contacts)
        {
            sb.Append("BEGIN:VCARD\r\nVERSION:3.0\r\n");
            sb.Append("N:").Append(VEscape(c.LastName)).Append(';').Append(VEscape(c.FirstName)).Append(";;;\r\n");
            sb.Append("FN:").Append(VEscape(c.DisplayName)).Append("\r\n");
            sb.Append("EMAIL;TYPE=INTERNET:").Append(VEscape(c.Address)).Append("\r\n");
            if (c.Organisation.Length > 0)
            {
                sb.Append("ORG:").Append(VEscape(c.Organisation)).Append("\r\n");
            }
            if (c.Phone.Length > 0)
            {
                sb.Append("TEL:").Append(VEscape(c.Phone)).Append("\r\n");
            }
            if (c.Notes.Length > 0)
            {
                sb.Append("NOTE:").Append(VEscape(c.Notes)).Append("\r\n");
            }
            sb.Append("END:VCARD\r\n");
        }
        return sb.ToString();
    }

    /// <summary>The contact groups, in name order.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<ContactGroup>> ListGroupsAsync(Guid mailboxId, CancellationToken ct = default)
    {
        List<ContactGroup> all = await this.ReadDocumentAsync<List<ContactGroup>>(mailboxId, GroupsKind, ct).ConfigureAwait(false) ?? new List<ContactGroup>();
        return all.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Add or change a group. Returns a message for the person, or null on success.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="group">The group; an unknown id adds it.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SaveGroupAsync(Guid mailboxId, ContactGroup group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.Name = Clip(group.Name, 100);
        if (group.Name.Length == 0)
        {
            return "Give the group a name.";
        }
        var members = group.Addresses
            .SelectMany(a => AddressParser.Parse(a))
            .Select(a => a.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var clean = new ContactGroup { Id = group.Id, Name = group.Name, Addresses = members };
        List<ContactGroup> all = (await this.ListGroupsAsync(mailboxId, ct).ConfigureAwait(false)).ToList();
        if (all.Any(g => g.Id != clean.Id && string.Equals(g.Name, clean.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return "There is already a group with this name.";
        }
        int index = all.FindIndex(g => g.Id == clean.Id);
        if (index >= 0)
        {
            all[index] = clean;
        }
        else
        {
            all.Add(clean);
        }
        await this.WriteDocumentAsync(mailboxId, GroupsKind, all, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Remove a group (its people stay in contacts).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="groupId">The group.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> DeleteGroupAsync(Guid mailboxId, Guid groupId, CancellationToken ct = default)
    {
        List<ContactGroup> all = (await this.ListGroupsAsync(mailboxId, ct).ConfigureAwait(false)).ToList();
        int removed = all.RemoveAll(g => g.Id == groupId);
        if (removed > 0)
        {
            await this.WriteDocumentAsync(mailboxId, GroupsKind, all, ct).ConfigureAwait(false);
        }
        return removed > 0;
    }

    /// <summary>The person's contact settings.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<ContactSettings> GetContactSettingsAsync(Guid mailboxId, CancellationToken ct = default) =>
        await this.ReadDocumentAsync<ContactSettings>(mailboxId, ContactSettingsKind, ct).ConfigureAwait(false) ?? new ContactSettings();

    /// <summary>Save the person's contact settings.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="ct">Cancellation.</param>
    public Task SetContactSettingsAsync(Guid mailboxId, ContactSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return this.WriteDocumentAsync(mailboxId, ContactSettingsKind, settings, ct);
    }

    /// <summary>
    /// The names to show for addresses (item 28): a saved contact's name
    /// first, then a colleague's name from the organisation, then the name
    /// the address was last written with. Addresses with no known name are
    /// left out.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="addresses">The addresses.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyDictionary<string, string>> NamesForAsync(Guid mailboxId, IEnumerable<string> addresses, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var wanted = new HashSet<string>(addresses.Select(a => a.Trim()), StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return names;
        }
        foreach (Contact c in await this.ListContactsAsync(mailboxId, ct).ConfigureAwait(false))
        {
            if (wanted.Contains(c.Address) && c.DisplayName != c.Address)
            {
                names[c.Address] = c.DisplayName;
            }
        }
        foreach ((string address, string name) in await this.ColleaguesAsync(mailboxId, ct).ConfigureAwait(false))
        {
            if (wanted.Contains(address) && !names.ContainsKey(address) && name.Length > 0)
            {
                names[address] = name;
            }
        }
        return names;
    }

    /// <summary>The organisation's other mailboxes, as (address, name).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<(string Address, string Name)>> ColleaguesAsync(Guid mailboxId, CancellationToken ct = default)
    {
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return Array.Empty<(string, string)>();
        }
        IReadOnlyList<MailboxRow> all = await this.store.ListMailboxesAsync(context.Value.Tenant.Id, ct).ConfigureAwait(false);
        return all.Where(m => m.Id != mailboxId && m.Enabled)
            .Select(m => (m.Address, m.DisplayName ?? string.Empty))
            .ToList();
    }

    private async Task<T?> ReadDocumentAsync<T>(Guid mailboxId, string kind, CancellationToken ct)
        where T : class
    {
        string? json = await this.store.GetMailboxDocumentAsync(mailboxId, kind, ct).ConfigureAwait(false);
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

    private Task WriteDocumentAsync<T>(Guid mailboxId, string kind, T value, CancellationToken ct)
    {
        if (kind == SecurityKind)
        {
            // A new password or two-step changes what the organisation still asks of the person.
            this.ForgetDemand(mailboxId);
        }
        return this.store.SetMailboxDocumentAsync(mailboxId, kind, JsonSerializer.Serialize(value, DocumentJson), ct);
    }

    private static string Clip(string? text, int max)
    {
        string t = (text ?? string.Empty).Trim();
        return t.Length <= max ? t : t[..max];
    }

    // Adds to "keep" every detail "other" has and it lacks. True when anything was added.
    private static bool Fill(Contact keep, Contact other)
    {
        bool changed = false;
        if (keep.FirstName.Length == 0 && other.FirstName.Length > 0)
        {
            keep.FirstName = other.FirstName;
            changed = true;
        }
        if (keep.LastName.Length == 0 && other.LastName.Length > 0)
        {
            keep.LastName = other.LastName;
            changed = true;
        }
        if (keep.Organisation.Length == 0 && other.Organisation.Length > 0)
        {
            keep.Organisation = other.Organisation;
            changed = true;
        }
        if (keep.Phone.Length == 0 && other.Phone.Length > 0)
        {
            keep.Phone = other.Phone;
            changed = true;
        }
        if (other.Notes.Length > 0 && !keep.Notes.Contains(other.Notes, StringComparison.Ordinal))
        {
            keep.Notes = keep.Notes.Length == 0 ? other.Notes : keep.Notes + "\n" + other.Notes;
            changed = true;
        }
        return changed;
    }

    private static string CsvField(string value)
    {
        string v = value ?? string.Empty;
        // A leading = + - @ would be run as a formula by a spreadsheet: quote it as text.
        if (v.Length > 0 && "=+-@".Contains(v[0], StringComparison.Ordinal))
        {
            v = "'" + v;
        }
        return v.AsSpan().IndexOfAny(CsvSpecial) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }

    private static string VEscape(string value) =>
        (value ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal).Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string VUnescape(string value) =>
        value.Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase).Replace("\\,", ",", StringComparison.Ordinal)
            .Replace("\\;", ";", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    /// <summary>Read CSV rows; quoted fields may hold commas, quotes and line breaks.</summary>
    /// <param name="text">The CSV text.</param>
    public static IReadOnlyList<IReadOnlyList<string>> ReadCsvRows(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows.Where(r => r.Any(f => f.Trim().Length > 0)).ToList();
    }

    private static List<Contact> ParseCsv(string text)
    {
        IReadOnlyList<IReadOnlyList<string>> rows = ReadCsvRows(text.TrimStart('﻿'));
        var result = new List<Contact>();
        if (rows.Count == 0)
        {
            return result;
        }
        // A header row decides the columns; without one: first, last, address, organisation, phone.
        int first = 0, last = 1, address = 2, org = 3, phone = 4, notes = 5;
        IReadOnlyList<string> head = rows[0];
        bool hasHeader = head.Any(h => h.Contains("mail", StringComparison.OrdinalIgnoreCase) || h.Contains("address", StringComparison.OrdinalIgnoreCase));
        if (hasHeader)
        {
            first = last = address = org = phone = notes = -1;
            for (int i = 0; i < head.Count; i++)
            {
                string h = head[i].Trim().ToLowerInvariant();
                if (h.Contains("first", StringComparison.Ordinal) || h == "given name")
                {
                    first = i;
                }
                else if (h.Contains("last", StringComparison.Ordinal) || h.Contains("surname", StringComparison.Ordinal) || h == "family name")
                {
                    last = i;
                }
                else if (h.Contains("mail", StringComparison.Ordinal) || h == "address")
                {
                    address = address < 0 ? i : address;
                }
                else if (h.Contains("organi", StringComparison.Ordinal) || h.Contains("company", StringComparison.Ordinal))
                {
                    org = i;
                }
                else if (h.Contains("phone", StringComparison.Ordinal) || h.Contains("mobile", StringComparison.Ordinal))
                {
                    phone = phone < 0 ? i : phone;
                }
                else if (h.Contains("note", StringComparison.Ordinal))
                {
                    notes = i;
                }
                else if (h == "name" && first < 0)
                {
                    first = i;
                }
            }
        }
        foreach (IReadOnlyList<string> r in rows.Skip(hasHeader ? 1 : 0))
        {
            string At(int i) => i >= 0 && i < r.Count ? r[i].Trim() : string.Empty;
            string a = At(address);
            if (a.Length == 0)
            {
                a = r.FirstOrDefault(f => f.Contains('@', StringComparison.Ordinal))?.Trim() ?? string.Empty;
            }
            result.Add(new Contact
            {
                FirstName = Clip(At(first), 100),
                LastName = Clip(At(last), 100),
                Address = a,
                Organisation = Clip(At(org), 200),
                Phone = Clip(At(phone), 50),
                Notes = Clip(At(notes), 2000),
            });
        }
        return result;
    }

    private static List<Contact> ParseVCards(string text)
    {
        var result = new List<Contact>();
        // Unfold continuation lines (RFC 6350 3.2).
        string unfolded = text.Replace("\r\n ", string.Empty, StringComparison.Ordinal).Replace("\r\n\t", string.Empty, StringComparison.Ordinal)
            .Replace("\n ", string.Empty, StringComparison.Ordinal).Replace("\n\t", string.Empty, StringComparison.Ordinal);
        Contact? current = null;
        string fullName = string.Empty;
        foreach (string raw in unfolded.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }
            string name = line[..colon].Split(';')[0].Trim().ToUpperInvariant();
            int dot = name.IndexOf('.', StringComparison.Ordinal);
            if (dot >= 0)
            {
                name = name[(dot + 1)..];
            }
            string value = line[(colon + 1)..];
            switch (name)
            {
                case "BEGIN":
                    current = new Contact();
                    fullName = string.Empty;
                    break;
                case "END":
                    if (current is not null)
                    {
                        if (current.FirstName.Length == 0 && current.LastName.Length == 0 && fullName.Length > 0)
                        {
                            current.FirstName = fullName;
                        }
                        result.Add(current);
                    }
                    current = null;
                    break;
                case "N" when current is not null:
                    string[] parts = value.Split(';');
                    current.LastName = Clip(VUnescape(parts[0]), 100);
                    current.FirstName = Clip(parts.Length > 1 ? VUnescape(parts[1]) : string.Empty, 100);
                    break;
                case "FN" when current is not null:
                    fullName = Clip(VUnescape(value), 100);
                    break;
                case "EMAIL" when current is not null && current.Address.Length == 0:
                    current.Address = VUnescape(value).Trim();
                    break;
                case "ORG" when current is not null:
                    current.Organisation = Clip(VUnescape(value.Split(';')[0]), 200);
                    break;
                case "TEL" when current is not null && current.Phone.Length == 0:
                    current.Phone = Clip(VUnescape(value), 50);
                    break;
                case "NOTE" when current is not null:
                    current.Notes = Clip(VUnescape(value), 2000);
                    break;
                default:
                    break;
            }
        }
        return result;
    }

    /// <summary>Format a count with the invariant culture.</summary>
    /// <param name="n">The count.</param>
    internal static string Count(long n) => n.ToString(CultureInfo.InvariantCulture);
}
