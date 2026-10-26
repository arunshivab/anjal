using System.Text.RegularExpressions;

namespace Anjal.Store;

/// <summary>A message row's text as stored: plain for a mailbox without keys, envelopes for an organisation's mailbox.</summary>
/// <param name="Subject">The subject.</param>
/// <param name="BodyText">The body text (for search).</param>
/// <param name="Snippet">The list's preview (first 160 characters).</param>
public sealed record StoredText(string Subject, string BodyText, string Snippet);

/// <summary>A message row's text exactly as stored, for the session's housekeeping (re-keying; moving existing mail).</summary>
/// <param name="Id">The message.</param>
/// <param name="FolderId">Its folder.</param>
/// <param name="MaildirFile">Its file.</param>
/// <param name="Subject">The stored subject.</param>
/// <param name="BodyText">The stored body text.</param>
/// <param name="Snippet">The stored snippet.</param>
public sealed record StoredMessageText(Guid Id, Guid FolderId, string MaildirFile, string Subject, string BodyText, string Snippet);

/// <summary>
/// rc.25 (DES-15 §3): a message row's subject, body text and preview - sealed to the mailbox's public
/// key when it has one, opened with the keys of the current flow (<see cref="MailKeyring.Current"/>).
/// A row that cannot be opened here reads as empty text: nothing readable leaves the store.
/// </summary>
public static class MessageCipher
{
    /// <summary>How many characters of the body the list shows.</summary>
    public const int SnippetLength = 160;

    /// <summary>The start of a stored field that arrived for the RSA key (kind 1) and is not yet re-keyed (§5.3).</summary>
    public const string ArrivalPrefix = "e1.QU5KRQEB";

    /// <summary>The start of a stored field that is re-keyed to the data key (kind 2).</summary>
    public const string RekeyedPrefix = "e1.QU5KRQEC";

    private static readonly Regex ReplyPrefix = new("^\\s*((re|fwd?|aw)\\s*:\\s*)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The stored form of a new row's text.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="bodyText">The body text.</param>
    /// <param name="publicKey">The mailbox's public key, or null for a mailbox without keys.</param>
    /// <returns>The stored text.</returns>
    public static StoredText Seal(string? subject, string? bodyText, byte[]? publicKey)
    {
        string s = subject ?? string.Empty;
        string b = bodyText ?? string.Empty;
        if (publicKey is null)
        {
            return new StoredText(s, b, string.Empty);
        }
        string snippet = b.Length > SnippetLength ? b[..SnippetLength] : b;
        return new StoredText(MailEnvelope.FieldToPublicKey(s, publicKey), MailEnvelope.FieldToPublicKey(b, publicKey), MailEnvelope.FieldToPublicKey(snippet, publicKey));
    }

    /// <summary>The stored form of text written in a session (the data key; no RSA).</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="bodyText">The body text.</param>
    /// <param name="dataKey">The mailbox's data key.</param>
    /// <returns>The stored text.</returns>
    public static StoredText SealWithKey(string? subject, string? bodyText, byte[] dataKey)
    {
        ArgumentNullException.ThrowIfNull(dataKey);
        string s = subject ?? string.Empty;
        string b = bodyText ?? string.Empty;
        string snippet = b.Length > SnippetLength ? b[..SnippetLength] : b;
        return new StoredText(MailEnvelope.FieldWithKey(s, dataKey), MailEnvelope.FieldWithKey(b, dataKey), MailEnvelope.FieldWithKey(snippet, dataKey));
    }

    /// <summary>A stored field opened with the current keys: the text, or empty when it cannot be opened here.</summary>
    /// <param name="stored">The stored field.</param>
    /// <returns>The text.</returns>
    public static string Open(string? stored) => MailEnvelope.OpenField(stored, MailKeyring.Current) ?? string.Empty;

    /// <summary>Whether a stored field is encrypted.</summary>
    /// <param name="stored">The stored field.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsSealed(string? stored) => MailEnvelope.IsField(stored);

    /// <summary>The key a subject sorts by: without "Re:", "Fwd:", "Aw:", in lower case.</summary>
    /// <param name="subject">The subject.</param>
    /// <returns>The key.</returns>
    public static string SortKey(string? subject) => ReplyPrefix.Replace(subject ?? string.Empty, string.Empty).ToLowerInvariant();

    /// <summary>Whether a decrypted row's text contains the words searched for (any case).</summary>
    /// <param name="row">The row, opened.</param>
    /// <param name="fullBody">Its whole body text, opened.</param>
    /// <param name="query">What was searched for.</param>
    /// <returns>Whether it matches.</returns>
    public static bool Matches(MessageRow row, string fullBody, string query)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(fullBody);
        ArgumentNullException.ThrowIfNull(query);
        string q = query.Trim();
        return q.Length == 0
            || row.Subject.Contains(q, StringComparison.OrdinalIgnoreCase)
            || row.FromHeader.Contains(q, StringComparison.OrdinalIgnoreCase)
            || row.ToHeader.Contains(q, StringComparison.OrdinalIgnoreCase)
            || row.EnvelopeFrom.Contains(q, StringComparison.OrdinalIgnoreCase)
            || fullBody.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}
