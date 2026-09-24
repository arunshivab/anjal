namespace Anjal.Mailbox;

/// <summary>
/// What a mailbox address may contain. The local part becomes a directory
/// name under the mail root, so it is checked strictly and up front, before
/// anything is written: "../evil@x.test" or "a b@x.test" used to pass a
/// looser check, commit the database row, and then fail while building the
/// maildir, leaving an enabled mailbox with nowhere to put mail (DEF-043).
/// </summary>
public static class MailboxAddressRules
{
    /// <summary>The longest local part accepted (RFC 5321 4.5.3.1.1).</summary>
    public const int MaxLocalPart = 64;

    /// <summary>
    /// Whether a local part is acceptable: 1 to 64 characters of ASCII
    /// letters, digits, and . _ % + -, with no leading, trailing or
    /// doubled dot. Deliberately narrower than RFC 5321 permits - quoted
    /// forms and the other specials are legal in mail but make poor
    /// directory names, and nobody needs them for a hospital mailbox.
    /// </summary>
    /// <param name="local">The part before the '@'.</param>
    public static bool IsValidLocalPart(string local)
    {
        System.ArgumentNullException.ThrowIfNull(local);
        if (local.Length == 0 || local.Length > MaxLocalPart || local[0] == '.' || local[^1] == '.')
        {
            return false;
        }
        char previous = '\0';
        foreach (char c in local)
        {
            bool allowed = char.IsAsciiLetterOrDigit(c) || c == '.' || c == '_' || c == '%' || c == '+' || c == '-';
            if (!allowed || (c == '.' && previous == '.'))
            {
                return false;
            }
            previous = c;
        }
        return true;
    }

    /// <summary>
    /// Whether a whole address is acceptable: a valid local part and a
    /// real-shaped domain (the same check sender rules use).
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="local">The local part, when valid.</param>
    /// <param name="domain">The domain, when valid.</param>
    public static bool TryValidate(string address, out string local, out string domain)
    {
        local = string.Empty;
        domain = string.Empty;
        return MailboxSink.TrySplitAddress(address, out local, out domain)
            && IsValidLocalPart(local)
            && Anjal.Spam.SenderRules.IsValidPattern("@" + domain);
    }
}
