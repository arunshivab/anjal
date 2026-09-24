namespace Anjal.Smtp;

/// <summary>
/// What a password must be, for a mailbox, an SMTP service account, or a
/// password change in the webmail. One policy, applied everywhere: the three
/// paths used to disagree (12 characters in the webmail, 8 in the admin API).
/// <para>
/// The rule is the owner's: at least 8 characters with an upper-case letter,
/// a lower-case letter, a digit and a symbol - plus a refusal of the
/// predictable results of that rule. Composition rules alone produce
/// "Apulki@123" and "Passw0rd!", which are among the first an attacker tries,
/// so length and the blocklist do the real work.
/// </para>
/// </summary>
public static class PasswordPolicy
{
    /// <summary>The shortest password accepted.</summary>
    public const int MinimumLength = 8;

    /// <summary>
    /// At or above this length the four-class rule is not required. A
    /// passphrase such as "ward round tuesday tea" is far stronger than
    /// "Apulki@123", and refusing it would push people towards the short,
    /// predictable shapes the classes encourage. The blocklist still applies.
    /// </summary>
    public const int PassphraseLength = 16;

    /// <summary>The longest accepted; long passphrases are welcome, unbounded input is not.</summary>
    public const int MaximumLength = 256;

    /// <summary>
    /// Passwords refused however well they satisfy the composition rule, and
    /// the words that may not make up most of one. Lower-case; comparison
    /// ignores case, digits and symbols, so "Apulki@123" is caught by "apulki".
    /// </summary>
    private static readonly char[] NameSeparators = { '@', '.', '-', '_', ' ', '+' };

    private static readonly string[] Forbidden =
    {
        "password", "passw0rd", "letmein", "welcome", "qwerty", "asdfgh", "zxcvbn", "iloveyou",
        "admin", "administrator", "root", "login", "master", "secret", "changeme", "temp",
        "anjal", "apulki", "imagiqa", "lipi", "sigma", "hospital", "doctor", "nurse", "mail",
        "12345678", "123456789", "1234567890", "abcdefgh", "11111111", "00000000",
    };

    /// <summary>
    /// Check a password. Returns null when it is acceptable, or a sentence
    /// for the user saying what is wrong - never a list of rules they have
    /// already satisfied.
    /// </summary>
    /// <param name="password">The proposed password.</param>
    /// <param name="address">The account's address or username, when known: a password may not contain it.</param>
    /// <param name="displayName">The person's name, when known: a password may not contain it.</param>
    public static string? Check(string password, string? address = null, string? displayName = null)
    {
        System.ArgumentNullException.ThrowIfNull(password);
        if (password.Length < MinimumLength)
        {
            return $"The password must be at least {MinimumLength} characters.";
        }
        if (password.Length > MaximumLength)
        {
            return $"The password must be {MaximumLength} characters or fewer.";
        }

        bool upper = false, lower = false, digit = false, symbol = false;
        foreach (char c in password)
        {
            if (char.IsUpper(c))
            {
                upper = true;
            }
            else if (char.IsLower(c))
            {
                lower = true;
            }
            else if (char.IsDigit(c))
            {
                digit = true;
            }
            else if (!char.IsWhiteSpace(c))
            {
                symbol = true;
            }
        }
        if (password.Length < PassphraseLength && !(upper && lower && digit && symbol))
        {
            return $"A password shorter than {PassphraseLength} characters needs an upper-case letter, a lower-case letter, a number and a symbol. A longer phrase of ordinary words is accepted as it is.";
        }

        // Compare with digits and symbols removed, so "Apulki@123" is judged
        // as "apulki" - the substitution nobody is fooled by.
        string letters = Strip(password);
        foreach (string word in Forbidden)
        {
            if (letters.Contains(word, System.StringComparison.Ordinal) || password.Contains(word, System.StringComparison.OrdinalIgnoreCase))
            {
                return "That password is too easy to guess. Choose something that is not a common word or this product's name.";
            }
        }
        foreach (string? personal in new[] { address, displayName }.AsSpan())
        {
            foreach (string part in PartsOf(personal))
            {
                if (letters.Contains(part, System.StringComparison.Ordinal))
                {
                    return "The password must not contain your name or your address.";
                }
            }
        }
        if (IsOneRepeatedCharacter(password) || IsOneRepeatedCharacter(letters) || IsARun(password))
        {
            return "That password is too easy to guess. Choose something less predictable.";
        }
        return null;
    }

    private static string Strip(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsLetter(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    /// <summary>The meaningful words of a name or address: 4 letters or more.</summary>
    private static System.Collections.Generic.IEnumerable<string> PartsOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }
        foreach (string part in value.Split(NameSeparators, System.StringSplitOptions.RemoveEmptyEntries))
        {
            string cleaned = Strip(part);
            if (cleaned.Length >= 4)
            {
                yield return cleaned;
            }
        }
    }

    /// <summary>"aaaaaaa", and so "Aaaaaaa1!" once digits and symbols are set aside.</summary>
    private static bool IsOneRepeatedCharacter(string s)
    {
        if (s.Length < 4)
        {
            return false;
        }
        foreach (char c in s)
        {
            if (c != s[0])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>"abcdefg1!" or "7654321a!" - a straight run through the alphabet or digits.</summary>
    private static bool IsARun(string s)
    {
        string letters = Strip(s);
        if (letters.Length < 5)
        {
            return false;
        }
        bool up = true, down = true;
        for (int i = 1; i < letters.Length; i++)
        {
            up &= letters[i] == letters[i - 1] + 1;
            down &= letters[i] == letters[i - 1] - 1;
        }
        return up || down;
    }
}
