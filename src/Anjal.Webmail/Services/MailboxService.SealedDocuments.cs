using System.Text.Json;
using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// rc.25 (DES-15 §3): a person's own documents that say who they deal with - contacts, contact
/// groups, their templates - are encrypted like their mail. Arrival still needs to ask "is this
/// sender a contact?" (away replies to contacts only) and "is this sender in that group?" (mail
/// rules), so beside the encrypted contacts and groups a list of address marks is kept: keyed
/// hashes that answer the question for one address without revealing the list (DES-15 §7.6).
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The mailbox documents kept encrypted.</summary>
    internal static readonly string[] SealedDocumentKinds = { ContactsKind, GroupsKind, TemplatesKind };

    private byte[]? addressPepper;

    private byte[] AddressPepper => this.addressPepper ??= MailKeys.PepperFromEnvironment();

    // The document sealed: with the data key when the request holds it, else to the mailbox's
    // public key; plain only for a mailbox that is not encrypted. Contacts and groups also get
    // their address marks.
    private async Task<string> SealDocumentAsync(Guid mailboxId, string kind, string json, CancellationToken ct)
    {
        if (this.KeyManager is not MailKeyManager km)
        {
            return json;
        }
        string sealedJson;
        if (MailKeyring.Current.DataKey(mailboxId) is byte[] dataKey)
        {
            sealedJson = MailEnvelope.FieldWithKey(json, dataKey);
        }
        else if (await km.Store.GetMailboxKeysAsync(mailboxId, ct).ConfigureAwait(false) is MailboxKeyRow keys)
        {
            sealedJson = MailEnvelope.FieldToPublicKey(json, keys.PublicKey);
        }
        else
        {
            return json;
        }
        await this.WriteMarksAsync(mailboxId, kind, json, ct).ConfigureAwait(false);
        return sealedJson;
    }

    private async Task WriteMarksAsync(Guid mailboxId, string kind, string json, CancellationToken ct)
    {
        if (kind == ContactsKind)
        {
            List<Contact> contacts = JsonSerializer.Deserialize<List<Contact>>(json, DocumentJson) ?? new List<Contact>();
            string marks = JsonSerializer.Serialize(contacts.Select(c => MailKeys.AddressMark(mailboxId, c.Address, this.AddressPepper)).Distinct().ToList());
            await this.store.SetMailboxDocumentAsync(mailboxId, MailboxSink.ContactMarksKind, marks, ct).ConfigureAwait(false);
        }
        else if (kind == GroupsKind)
        {
            List<ContactGroup> groups = JsonSerializer.Deserialize<List<ContactGroup>>(json, DocumentJson) ?? new List<ContactGroup>();
            string marks = JsonSerializer.Serialize(groups.Select(g => new { g.Name, Marks = g.Addresses.Select(a => MailKeys.AddressMark(mailboxId, a, this.AddressPepper)).Distinct().ToList() }).ToList());
            await this.store.SetMailboxDocumentAsync(mailboxId, MailboxSink.GroupMarksKind, marks, ct).ConfigureAwait(false);
        }
    }

    // Housekeeping (DES-15 §11): documents written before encryption are sealed now.
    private async Task<int> SealPlainDocumentsAsync(Guid mailboxId, CancellationToken ct)
    {
        int done = 0;
        foreach (string kind in SealedDocumentKinds)
        {
            string? json = await this.store.GetMailboxDocumentAsync(mailboxId, kind, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json) || MailEnvelope.IsField(json))
            {
                continue;
            }
            await this.store.SetMailboxDocumentAsync(mailboxId, kind, await this.SealDocumentAsync(mailboxId, kind, json, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            done++;
        }
        return done;
    }
}
