using System.Text.RegularExpressions;

namespace Anjal.Mailbox;

/// <summary>
/// rc.27 (DES-17 §2.1): how a desk finds a conversation again. A conversation is the mails whose
/// subjects are the same once "Re:", "Fwd:" and their kind are taken off (the rule the lists have
/// used since rc.12) <b>with the same person outside the desk</b> - so two patients who both write
/// "Appointment" are two pieces of work, not one. On a desk it is remembered by a keyed mark of the
/// two, never the subject or the address.
/// </summary>
public static partial class DeskMarks
{
    /// <summary>The kind of the organisation document listing its shared mailboxes (the webmail's <c>SharedKind</c>).</summary>
    public const string SharedKind = "shared";

    [GeneratedRegex(@"^\s*((re|fw|fwd|aw|wg|sv|vs|antw|tr)\s*(\[\d+\])?\s*:\s*)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex ReplyPrefixRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 200)]
    private static partial Regex SpaceRegex();

    [GeneratedRegex(@"<\s*([^<>\s]+@[^<>\s]+?)\s*>|([^\s,;:<>""]+@[^\s,;:<>""]+)", RegexOptions.CultureInvariant, 200)]
    private static partial Regex AddressRegex();

    /// <summary>A subject without its reply and forward prefixes, case and spacing folded: the conversation's key.</summary>
    /// <param name="subject">The subject.</param>
    /// <returns>The key; empty for an untitled mail.</returns>
    public static string ConversationKey(string? subject)
    {
        string s = Anjal.Mime.EncodedWordDecoder.Decode(subject ?? string.Empty);
        s = ReplyPrefixRegex().Replace(s, string.Empty);
        s = SpaceRegex().Replace(s, " ").Trim();
        return s.ToLowerInvariant();
    }

    /// <summary>The first address in a header such as From or To, lower case; empty when there is none.</summary>
    /// <param name="header">The header's value.</param>
    /// <returns>The address.</returns>
    public static string FirstAddress(string? header)
    {
        Match m = AddressRegex().Match(header ?? string.Empty);
        return !m.Success ? string.Empty : (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim().ToLowerInvariant();
    }

    /// <summary>The person outside the desk in a mail: who wrote it, or - for the desk's own reply - whom it went to.</summary>
    /// <param name="deskAddress">The desk's address.</param>
    /// <param name="from">The mail's From header.</param>
    /// <param name="to">The mail's To header.</param>
    /// <returns>Their address, lower case; empty when unknown.</returns>
    public static string OtherParty(string deskAddress, string? from, string? to)
    {
        string sender = FirstAddress(from);
        return sender.Length > 0 && !string.Equals(sender, deskAddress, System.StringComparison.OrdinalIgnoreCase) ? sender : FirstAddress(to);
    }

    /// <summary>A conversation's mark on one desk: keyed with the pepper and the desk, so it cannot be read back or compared between desks.</summary>
    /// <param name="deskId">The desk.</param>
    /// <param name="subject">The subject of any of its mails.</param>
    /// <param name="party">The person outside the desk (<see cref="OtherParty"/>).</param>
    /// <param name="messageId">The mail's id, which stands for an untitled mail's conversation of one.</param>
    /// <param name="pepper">The pepper.</param>
    /// <returns>The mark.</returns>
    public static string ThreadMark(System.Guid deskId, string? subject, string? party, System.Guid messageId, byte[] pepper)
    {
        string key = ConversationKey(subject);
        return Anjal.Store.MailKeys.AddressMark(deskId, key.Length > 0 ? "thread:" + key + "\n" + (party ?? string.Empty).ToLowerInvariant() : "mail:" + messageId.ToString("N"), pepper);
    }

    /// <summary>Whether a mailbox is one of its organisation's shared mailboxes (a desk), from the organisation's document.</summary>
    /// <param name="sharedJson">The organisation's <see cref="SharedKind"/> document, or null.</param>
    /// <param name="mailboxId">The mailbox.</param>
    /// <returns>True for a desk.</returns>
    public static bool IsDesk(string? sharedJson, System.Guid mailboxId)
    {
        if (string.IsNullOrWhiteSpace(sharedJson))
        {
            return false;
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(sharedJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.EnumerateObject().Any(p => System.Guid.TryParse(p.Name, out System.Guid id) && id == mailboxId);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
