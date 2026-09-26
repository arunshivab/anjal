using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// Decision 1B (26 Sep 2026): the webmail says plainly whether a message went
/// through the incoming checks. Before rc.5 a blank score meant either
/// "checked, scored 0" or "never checked", and a user's own sent mail read
/// "Spam score 0" although it was never scored.
/// </summary>
public static class SpamCheck
{
    /// <summary>
    /// Whether the message went through the incoming checks. Uses what was
    /// recorded at delivery; for messages stored before that was recorded,
    /// decides by folder: mail in INBOX, Junk or a user folder came from
    /// outside and was scored, while Sent, Drafts and Trash cannot be told
    /// apart and count as not checked.
    /// </summary>
    /// <param name="row">The message.</param>
    /// <param name="folderName">The folder it is in.</param>
    public static bool WasChecked(MessageRow row, string folderName)
    {
        System.ArgumentNullException.ThrowIfNull(row);
        System.ArgumentNullException.ThrowIfNull(folderName);
        return row.SpamChecked ?? !(folderName is "Sent" or "Drafts" or "Trash");
    }

    /// <summary>
    /// What the message page says when the message was not checked: the
    /// user's own mail, or - stated as the rule, not as a guess about the
    /// sender - that only mail from outside is checked.
    /// </summary>
    /// <param name="folderName">The folder it is in.</param>
    public static string NotCheckedNote(string folderName)
    {
        System.ArgumentNullException.ThrowIfNull(folderName);
        return folderName is "Sent" or "Drafts"
            ? "Your own message - incoming checks do not apply."
            : "Not checked on arrival: incoming checks apply only to mail from outside this server.";
    }
}
