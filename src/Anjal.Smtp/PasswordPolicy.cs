namespace Anjal.Smtp;

/// <summary>
/// What a password must be, for a mailbox, an SMTP service account, or a
/// password change in the webmail. One policy, applied everywhere: the three
/// paths used to disagree (12 characters in the webmail, 8 in the admin API).
/// <para>
/// Owner, 10 Oct 2026 (P1-P8): the rules meet NIST SP 800-63B-4, OWASP ASVS 4.0.3
/// and 5.0, and PCI DSS 4.0 at once. A password used alone is at least 15
/// characters; with two-step sign-in, at least 12. There is no rule about kinds
/// of characters (NIST forbids them: they produce "Apulki@123"); length, the
/// list of leaked passwords and the refusal of guessable words do the work.
/// Length counts characters (Unicode code points), after the password is
/// normalised (NFKC), so a Tamil or Hindi letter is never counted twice.
/// </para>
/// </summary>
public static class PasswordPolicy
{
    /// <summary>What a person is told when their new password is a known leaked one (rc.15, item 35).</summary>
    public const string LeakedMessage = "That password has appeared in a known data leak, so attackers already try it. Choose something of your own.";

    /// <summary>The shortest password accepted anywhere: with two-step sign-in on (owner, 10 Oct 2026).</summary>
    public const int MinimumLength = 12;

    /// <summary>The shortest password used alone, without two-step sign-in (NIST SP 800-63B-4).</summary>
    public const int AloneLength = 15;

    /// <summary>The longest accepted; long passphrases are welcome, unbounded input is not.</summary>
    public const int MaximumLength = 256;

    /// <summary>The shortest password for a person, with or without two-step sign-in.</summary>
    /// <param name="twoStep">True when the person signs in with a second step.</param>
    /// <returns>12 or 15.</returns>
    public static int LengthFor(bool twoStep) => twoStep ? MinimumLength : AloneLength;

    /// <summary>The password as it is checked and hashed: Unicode normalised (NFKC).</summary>
    /// <param name="password">The password as typed.</param>
    /// <returns>The normalised password.</returns>
    public static string Normalize(string password)
    {
        System.ArgumentNullException.ThrowIfNull(password);
        try
        {
            return password.Normalize(System.Text.NormalizationForm.FormKC);
        }
        catch (System.ArgumentException)
        {
            // Not valid Unicode (a lone surrogate): kept as it is.
            return password;
        }
    }

    /// <summary>How many characters a password has: Unicode code points, after normalising.</summary>
    /// <param name="password">The password as typed.</param>
    /// <returns>The count.</returns>
    public static int CharacterCount(string password)
    {
        System.ArgumentNullException.ThrowIfNull(password);
        int n = 0;
        foreach (System.Text.Rune unused in Normalize(password).EnumerateRunes())
        {
            n++;
        }
        return n;
    }

    private static readonly char[] NameSeparators = { '@', '.', '-', '_', ' ', '+' };

    /// <summary>
    /// Words that make a password easy to guess: common words and this product's and its
    /// customers' names. A password made up mostly of them is refused (owner, 10 Oct 2026, P4) - a
    /// long phrase that happens to contain one ("snail mail on the garden path") is not.
    /// Lower-case; compared with digits and symbols set aside, so "Apulki@123" is "apulki".
    /// </summary>
    private static readonly string[] Forbidden =
    {
        "password", "passw0rd", "letmein", "welcome", "qwerty", "asdfgh", "zxcvbn", "iloveyou",
        "admin", "administrator", "root", "login", "master", "secret", "changeme", "temp",
        "anjal", "apulki", "imagiqa", "lipi", "sigma", "hospital", "doctor", "nurse", "mail",
        "12345678", "123456789", "1234567890", "abcdefgh", "11111111", "00000000",
    };

    /// <summary>
    /// Passwords seen again and again in published leaks (rc.13), as their
    /// letters alone: "Krishna@123" and "krishna1" are both "krishna". A
    /// password whose letters are exactly one of these is refused.
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> Leaked = new(System.StringComparer.Ordinal)
    {
        "abc", "abcd", "abcde", "abcdef", "qwer", "qwert", "qwertyuiop", "asdf", "asdfghjkl", "zxcv", "zxcvbnm", "qaz", "zaq", "qazwsx",
        "pass", "test", "user", "guest", "hello", "love", "loveme", "lovely", "loveyou", "iloveu", "angel", "baby", "babygirl", "princess",
        "dragon", "monkey", "sunshine", "football", "baseball", "soccer", "cricket", "shadow", "superman", "batman", "spiderman",
        "michael", "jennifer", "jordan", "hunter", "ranger", "buster", "harley", "robert", "matthew", "daniel", "andrew", "joshua",
        "thomas", "charlie", "freedom", "whatever", "trustno", "starwars", "computer", "internet", "cheese", "summer", "winter",
        "flower", "killer", "pepper", "ginger", "cookie", "chocolate", "butterfly", "purple", "orange", "banana", "liverpool",
        "chelsea", "arsenal", "manchester", "beautiful", "family", "friends", "forever", "god", "jesus", "blessed", "lucky",
        "india", "bharat", "hindustan", "mumbai", "delhi", "chennai", "kolkata", "pune", "bangalore", "bengaluru", "hyderabad",
        "krishna", "ganesh", "ganesha", "shiva", "sairam", "saibaba", "omsairam", "om", "omnamahshivaya", "hanuman", "durga",
        "lakshmi", "jaimatadi", "jaishreeram", "jaihind", "rama", "ram", "sachin", "dhoni", "virat", "kohli", "tendulkar",
        "bollywood", "shahrukh", "salman", "priya", "pooja", "deepak", "rahul", "amit", "ankit", "sandeep", "suresh", "ramesh",
        "rajesh", "mahesh", "babu", "kumar", "singh", "sharma", "tamil", "kerala", "gujarat", "marathi", "hindi", "welcome",
        "office", "company", "default", "system", "server", "support", "manager", "account", "access", "private", "secure",
        "security", "monday", "sunday", "january", "december", "spring", "autumn", "money", "golden", "silver", "diamond",
    };

    /// <summary>
    /// Each rule, judged on its own (rc.13): the list shown beside a new
    /// password, ticked as the person types.
    /// </summary>
    /// <param name="password">The proposed password.</param>
    /// <param name="address">The account's address, when known.</param>
    /// <param name="displayName">The person's name, when known.</param>
    /// <param name="minimumLength">The shortest allowed for this person (<see cref="LengthFor"/>, or more if the organisation asks).</param>
    /// <returns>Which rules are met.</returns>
    public static PasswordRuleResults Evaluate(string password, string? address = null, string? displayName = null, int minimumLength = AloneLength)
    {
        System.ArgumentNullException.ThrowIfNull(password);
        int least = System.Math.Max(MinimumLength, minimumLength);
        int count = CharacterCount(password);
        bool longEnough = count >= least && count <= MaximumLength;
        string typed = Normalize(password);
        bool common = count > 0 && (IsCommon(typed) || PwnedPasswords.IsLeaked(typed) || MostlyWords(typed, Forbidden));
        bool personal = count > 0 && MostlyWords(typed, PersonalWords(address, displayName));
        return new PasswordRuleResults(longEnough, count > 0 && !common, count > 0 && !personal);
    }

    /// <summary>
    /// Check a password. Returns null when it is acceptable, or a sentence
    /// for the user saying what is wrong - never a list of rules they have
    /// already satisfied.
    /// </summary>
    /// <param name="password">The proposed password.</param>
    /// <param name="address">The account's address or username, when known: a password may not be mostly made of it.</param>
    /// <param name="displayName">The person's name, when known: a password may not be mostly made of it.</param>
    /// <param name="minimumLength">The shortest allowed for this person (<see cref="LengthFor"/>, or more if the organisation asks).</param>
    /// <returns>Null when acceptable, else what is wrong.</returns>
    public static string? Check(string password, string? address = null, string? displayName = null, int minimumLength = AloneLength)
    {
        System.ArgumentNullException.ThrowIfNull(password);
        int least = System.Math.Max(MinimumLength, minimumLength);
        int count = CharacterCount(password);
        if (count < least)
        {
            return $"The password must be at least {least} characters.";
        }
        if (count > MaximumLength)
        {
            return $"The password must be {MaximumLength} characters or fewer.";
        }
        string typed = Normalize(password);
        if (Leaked.Contains(Strip(typed)))
        {
            return "That password is one of the known leaked passwords. Choose something of your own.";
        }
        // rc.15 (item 35): the full list of passwords from known data leaks, checked on this
        // server; however long a password is, attackers try these first.
        if (PwnedPasswords.IsLeaked(typed))
        {
            return LeakedMessage;
        }
        if (MostlyWords(typed, PersonalWords(address, displayName)))
        {
            return "The password must not be mostly your name or your address.";
        }
        if (MostlyWords(typed, Forbidden) || IsCommon(typed))
        {
            return "That password is too easy to guess. Choose something that is not mostly a common word or this product's name.";
        }
        return null;
    }

    /// <summary>
    /// Check a new password fully (rc.15, item 35): <see cref="Check"/>, and then Have I Been
    /// Pwned's full, current list online (by k-anonymity; see <see cref="PwnedPasswords"/>). When
    /// the online service cannot be reached the password is judged without it.
    /// </summary>
    /// <param name="password">The proposed password.</param>
    /// <param name="address">The account's address or username, when known.</param>
    /// <param name="displayName">The person's name, when known.</param>
    /// <param name="minimumLength">The shortest allowed for this person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Null when acceptable, else what is wrong.</returns>
    public static async System.Threading.Tasks.Task<string?> CheckAsync(string password, string? address = null, string? displayName = null, int minimumLength = AloneLength, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(password);
        if (Check(password, address, displayName, minimumLength) is string rejected)
        {
            return rejected;
        }
        return PwnedPasswords.ChecksOnline && await PwnedPasswords.IsLeakedOnlineAsync(Normalize(password), ct).ConfigureAwait(false) == true
            ? LeakedMessage
            : null;
    }

    /// <summary>A known leaked password by its letters, one repeated character, or a straight run.</summary>
    private static bool IsCommon(string typed)
    {
        string letters = Strip(typed);
        string compact = Compact(typed);
        return Leaked.Contains(letters) || System.Array.IndexOf(Forbidden, compact) >= 0
            || IsOneRepeatedCharacter(typed) || IsOneRepeatedCharacter(letters) || IsOneRepeatedCharacter(compact) || IsARun(letters) || IsARun(compact);
    }

    /// <summary>
    /// True when the given words cover at least half of the password's letters (owner, 10 Oct
    /// 2026, P4: compare the whole password, not any part of it). Words of letters are matched
    /// against its letters; words of digits against its letters and digits.
    /// </summary>
    private static bool MostlyWords(string typed, System.Collections.Generic.IEnumerable<string> words)
    {
        return Mostly(Strip(typed), words) || Mostly(Compact(typed), words);
    }

    private static bool Mostly(string text, System.Collections.Generic.IEnumerable<string> words)
    {
        int covered = Covered(text, words);
        return covered > 0 && covered * 2 >= text.Length;
    }

    /// <summary>How many characters of the text are inside any of the words.</summary>
    private static int Covered(string text, System.Collections.Generic.IEnumerable<string> words)
    {
        bool[] hit = new bool[text.Length];
        foreach (string word in words)
        {
            if (word.Length == 0)
            {
                continue;
            }
            for (int at = text.IndexOf(word, System.StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, System.StringComparison.Ordinal))
            {
                for (int k = at; k < at + word.Length; k++)
                {
                    hit[k] = true;
                }
            }
        }
        int n = 0;
        foreach (bool h in hit)
        {
            n += h ? 1 : 0;
        }
        return n;
    }

    /// <summary>The meaningful words of the person's address and name.</summary>
    private static System.Collections.Generic.List<string> PersonalWords(string? address, string? displayName)
    {
        var words = new System.Collections.Generic.List<string>();
        foreach (string? who in new[] { address, displayName })
        {
            words.AddRange(PartsOf(who));
        }
        return words;
    }

    /// <summary>Letters and digits only, lower-case: "Pass-1234 5678" is "pass12345678".</summary>
    private static string Compact(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
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

    /// <summary>"abcdefghijklmno" or "987654321098" - a straight run through the alphabet or the digits.</summary>
    private static bool IsARun(string s)
    {
        if (s.Length < 5)
        {
            return false;
        }
        bool up = true, down = true;
        for (int i = 1; i < s.Length; i++)
        {
            up &= s[i] == s[i - 1] + 1 || (s[i] == '0' && s[i - 1] == '9');
            down &= s[i] == s[i - 1] - 1 || (s[i] == '9' && s[i - 1] == '0');
        }
        return up || down;
    }
}

/// <summary>Which password rules are met (rc.13; owner, 10 Oct 2026: no rule about kinds of characters).</summary>
/// <param name="LongEnough">Long enough, and not too long.</param>
/// <param name="NotCommon">Not a known leaked password, and not mostly a common word or this product's name.</param>
/// <param name="NotPersonal">Not mostly the person's name or address.</param>
public sealed record PasswordRuleResults(bool LongEnough, bool NotCommon, bool NotPersonal)
{
    /// <summary>True when every rule is met.</summary>
    public bool All => this.LongEnough && this.NotCommon && this.NotPersonal;
}
