using System.Globalization;
using System.Text.RegularExpressions;

namespace Anjal.Webmail.Services;

/// <summary>A child a letter is about ("my son Nafees").</summary>
/// <param name="Name">The child's name as typed, with a capital, or empty.</param>
/// <param name="Relation">son, daughter or child.</param>
/// <param name="Pronoun">he, she, or empty when the words do not say.</param>
public sealed record LajnaChild(string Name, string Relation, string Pronoun);

/// <summary>The days a letter is about, read from the words.</summary>
/// <param name="Display">For a subject line: "17–19 October", "Friday", "tomorrow".</param>
/// <param name="Spoken">For a sentence: "from 17 to 19 October", "on Friday", "tomorrow".</param>
/// <param name="Span">True for more than one day.</param>
/// <param name="Found">The words the days were read from, as typed.</param>
public sealed record LajnaDates(string Display, string Spoken, bool Span, string Found)
{
    /// <summary>No days given.</summary>
    public static LajnaDates None { get; } = new(string.Empty, string.Empty, false, string.Empty);

    /// <summary>True when days were given.</summary>
    public bool Any => this.Display.Length > 0;
}

/// <summary>Why, as the person put it: "for my sister's wedding", "as he has fever".</summary>
/// <param name="Connector">for, as or due to.</param>
/// <param name="Text">The reason itself.</param>
public sealed record LajnaReason(string Connector, string Text)
{
    /// <summary>The reason as a phrase that follows a sentence: " for X", ", as X", " due to X".</summary>
    public string Phrase => this.Connector switch
    {
        "as" => ", as " + this.Text,
        "due to" => " due to " + this.Text,
        _ => " for " + this.Text,
    };
}

/// <summary>What a person asked Lajna to write, read from their own words.</summary>
/// <param name="Who">Who the letter is to, as a name or an address ("HR", "Principal", "Meera").</param>
/// <param name="What">What the letter says or asks, as typed.</param>
/// <param name="Kind">leave, tell, ask, request, remind, about, thank or note.</param>
/// <param name="Child">The child a school leave letter is for, or null.</param>
/// <param name="School">True when the letter goes to a school (principal, teacher).</param>
/// <param name="Dates">The days.</param>
/// <param name="Reason">Why, or null.</param>
/// <param name="Content">What the letter says, without the joining word ("saying", "asking").</param>
/// <param name="Tone">formal or short when the words asked for it, otherwise empty.</param>
/// <param name="LeaveType">sick, casual, medical and so on when the words said so, otherwise empty.</param>
public sealed record LajnaAsk(string Who, string What, string Kind, LajnaChild? Child, bool School, LajnaDates Dates, LajnaReason? Reason, string Content, string Tone, string LeaveType)
{
    /// <summary>True for a leave letter (the person's own, or a child's).</summary>
    public bool Leave => this.Kind == "leave";
}

/// <summary>
/// rc.21 (owner, 15 Oct 2026: "Chat bot mail drafting is not working properly"; "I want to sent a
/// leave letter to my child Nafees's principal saying he is on leave from 17th october to 19th
/// october" and "i want to sent a leave letter to hr from 20th october to 22nd october" were not
/// understood; a language model is for next year, "improve it as much as you can within this"):
/// reads a request to write a mail the way people type it - with "I want to", "can you", "please",
/// typing slips such as "sent", a letter named first ("a leave letter to"), the person it goes to
/// before or after what it says, days in any of the usual forms, a reason, and a child's school leave
/// letter - without anything leaving Anjal.
/// </summary>
public static partial class LajnaRequest
{
    private static readonly string[] SchoolRoles = { "principal", "vice principal", "teacher", "class teacher", "headmaster", "headmistress", "head master", "head mistress", "head teacher", "school", "class coordinator", "coordinator", "tutor", "warden" };

    /// <summary>Read a request to write a mail, or null when the words do not ask for one.</summary>
    /// <param name="question">The person's words.</param>
    /// <returns>The request, or null.</returns>
    public static LajnaAsk? Parse(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        string s = Regex.Replace(question.Trim(), @"\s+", " ").TrimEnd('.', '!', '?', ' ');
        for (int i = 0; i < 6; i++)
        {
            Match lead = LeadRegex().Match(s);
            if (!lead.Success || lead.Length == 0)
            {
                break;
            }
            s = s[lead.Length..];
        }
        string tone = string.Empty;
        bool leaveLetter = false;
        string kindHint = string.Empty;
        string forText = string.Empty;
        string objText = string.Empty;
        string who;
        string what;
        Match open = OpenRegex().Match(s);
        if (open.Success && (open.Groups["verb"].Success || open.Groups["obj"].Success))
        {
            tone = Tone(open.Groups["tone"].Value);
            leaveLetter = Regex.IsMatch(open.Groups["obj"].Value, @"\b(?:leave|absence|sick)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            forText = open.Groups["for"].Value;
            objText = open.Groups["obj"].Value;
            (who, what) = Split(open.Groups["rest"].Value);
        }
        else if (DirectRegex().Match(s) is { Success: true } direct)
        {
            (who, what) = Split(direct.Groups["rest"].Value);
            if (!(Regex.IsMatch(what, @"^(?:saying|asking|that|telling|about|regarding|requesting|informing|to\s+(?:say|ask|tell|inform|request|let))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) || LeaveWordsRegex().IsMatch(what)))
            {
                return null;
            }
        }
        else if (VerbDirectRegex().Match(s) is { Success: true } said)
        {
            // "inform my team that…", "ask Meera to send the report", "remind Rao about the meeting".
            who = said.Groups["who"].Value;
            what = said.Groups["what"].Value;
            kindHint = said.Groups["v"].Value.ToLowerInvariant() switch
            {
                "ask" or "request" => "ask",
                "remind" => "remind",
                "thank" => "thank",
                _ => "tell",
            };
            if (said.Groups["v"].Value.Equals("let", StringComparison.OrdinalIgnoreCase) && !what.StartsWith("know", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        else
        {
            return null;
        }
        string about = (forText.Length > 0 ? forText + " " : string.Empty) + what;
        LajnaChild? child = Child(who + " " + about);
        string role = Recipient(who);
        if (role.Length == 0)
        {
            return null;
        }
        bool school = SchoolRoles.Contains(role.ToLowerInvariant()) || PossessiveRoleRegex().IsMatch(who);
        if (!school)
        {
            child = null;
        }
        return Build(role, about, leaveLetter, child, school, tone, kindHint, objText);
    }

    /// <summary>Read what a letter says when the person it goes to is already known (the older way in).</summary>
    /// <param name="what">What the letter says or asks.</param>
    /// <param name="leave">True for a leave request.</param>
    /// <returns>The request.</returns>
    public static LajnaAsk FromWhat(string what, bool leave)
    {
        ArgumentNullException.ThrowIfNull(what);
        return Build(string.Empty, what, leave, null, false, string.Empty, string.Empty, string.Empty);
    }

    /// <summary>Read the days in some words.</summary>
    /// <param name="words">The words.</param>
    /// <returns>The days, or <see cref="LajnaDates.None"/>.</returns>
    public static LajnaDates ReadDates(string words)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (NumericRangeRegex().Match(words) is { Success: true } nr
            && Num(nr, "d1") is int nd1 && Num(nr, "n1") is int nm1 && Num(nr, "d2") is int nd2 && Num(nr, "n2") is int nm2
            && Valid(nd1, nm1) && Valid(nd2, nm2))
        {
            return Range(nd1, Abbrev(nm1), nd2, Abbrev(nm2), string.Empty, nr.Value);
        }
        if (DayFirstRangeRegex().Match(words) is { Success: true } dr && Num(dr, "d1") is int d1 && Num(dr, "d2") is int d2 && d1 is >= 1 and <= 31 && d2 is >= 1 and <= 31)
        {
            string m2 = Capital(dr.Groups["m2"].Value);
            string m1 = dr.Groups["m1"].Success ? Capital(dr.Groups["m1"].Value) : m2;
            return Range(d1, m1, d2, m2, dr.Groups["y"].Value, dr.Value);
        }
        if (MonthFirstRangeRegex().Match(words) is { Success: true } mr && Num(mr, "d1") is int md1 && Num(mr, "d2") is int md2 && md1 is >= 1 and <= 31 && md2 is >= 1 and <= 31)
        {
            string m1 = Capital(mr.Groups["m1"].Value);
            string m2 = mr.Groups["m2"].Success ? Capital(mr.Groups["m2"].Value) : m1;
            return Range(md1, m1, md2, m2, mr.Groups["y"].Value, mr.Value);
        }
        if (NumericOneRegex().Match(words) is { Success: true } n1 && Num(n1, "d") is int nd && Num(n1, "n") is int nm && Valid(nd, nm))
        {
            string day = nd.ToString(CultureInfo.InvariantCulture) + " " + Abbrev(nm);
            return new LajnaDates(day, "on " + day, false, n1.Value);
        }
        if (DayFirstOneRegex().Match(words) is { Success: true } d && Num(d, "d") is int dd && dd is >= 1 and <= 31)
        {
            string day = dd.ToString(CultureInfo.InvariantCulture) + " " + Capital(d.Groups["m"].Value) + Year(d.Groups["y"].Value);
            return new LajnaDates(day, "on " + day, false, d.Value);
        }
        if (MonthFirstOneRegex().Match(words) is { Success: true } m && Num(m, "d") is int mdd && mdd is >= 1 and <= 31)
        {
            string day = mdd.ToString(CultureInfo.InvariantCulture) + " " + Capital(m.Groups["m"].Value) + Year(m.Groups["y"].Value);
            return new LajnaDates(day, "on " + day, false, m.Value);
        }
        if (WeekdayRegex().Match(words) is { Success: true } w)
        {
            string first = (w.Groups["near"].Success ? w.Groups["near"].Value.ToLowerInvariant() + " " : string.Empty) + Capital(w.Groups["w1"].Value.ToLowerInvariant());
            if (w.Groups["w2"].Success)
            {
                string last = Capital(w.Groups["w2"].Value.ToLowerInvariant());
                if (Regex.IsMatch(w.Value, @"(?:&|\band\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    return new LajnaDates(first + " and " + last, "on " + first + " and " + last, true, w.Value);
                }
                return new LajnaDates(first + "–" + last, "from " + first + " to " + last, true, w.Value);
            }
            return new LajnaDates(first, w.Groups["near"].Success ? first : "on " + first, false, w.Value);
        }
        if (NearRegex().Match(words) is { Success: true } near)
        {
            string n = near.Value.ToLowerInvariant();
            return new LajnaDates(n, n, n == "next week", near.Value);
        }
        return LajnaDates.None;
    }

    private static LajnaAsk Build(string who, string what, bool leaveLetter, LajnaChild? child, bool school, string tone, string kindHint, string letterWords)
    {
        string w = what.Trim().TrimEnd('.', '?', '!').Trim();
        bool leave = leaveLetter || LeaveWordsRegex().IsMatch(w);
        LajnaDates dates = ReadDates(w);
        string withoutDates = dates.Found.Length > 0 ? w.Replace(dates.Found, " ", StringComparison.Ordinal) : w;
        // "for 3 days from next Monday": how long, then from when.
        if (DurationRegex().Match(w) is { Success: true } lasts && !(dates.Span && dates.Display.Contains('–', StringComparison.Ordinal)))
        {
            string length = lasts.Groups["n"].Value.ToLowerInvariant() + " " + lasts.Groups["u"].Value.ToLowerInvariant();
            dates = dates.Any
                ? new LajnaDates(length + " from " + dates.Display, "for " + length + " from " + dates.Display, true, dates.Found)
                : new LajnaDates(length, "for " + length, true, lasts.Value);
            withoutDates = withoutDates.Replace(lasts.Value, " ", StringComparison.Ordinal);
        }
        string leaveType = LeaveTypeRegex().Match(letterWords + " " + w) is { Success: true } lt ? Regex.Replace(lt.Groups["t"].Value.ToLowerInvariant(), @"[\s-]+", "-") : string.Empty;
        LajnaReason? reason = Reason(Regex.Replace(withoutDates, @"\s+", " "));
        Match join = JoinRegex().Match(w);
        string joined = join.Success ? Regex.Replace(join.Groups["j"].Value.ToLowerInvariant(), @"\s+", " ") : string.Empty;
        string content = join.Success ? w[join.Length..].Trim() : w;
        string kind;
        if (leave)
        {
            kind = "leave";
        }
        else if (kindHint.Length > 0)
        {
            kind = kindHint;
        }
        else if (joined is "about" or "regarding" or "re")
        {
            kind = "about";
        }
        else if (joined.StartsWith("request", StringComparison.Ordinal) || joined == "to request")
        {
            kind = "request";
        }
        else if (joined.StartsWith("ask", StringComparison.Ordinal) || joined == "to ask")
        {
            kind = "ask";
        }
        else if (joined.StartsWith("remind", StringComparison.Ordinal) || joined == "to remind")
        {
            kind = "remind";
        }
        else if (joined.StartsWith("thank", StringComparison.Ordinal))
        {
            kind = "thank";
        }
        else if (joined.Length > 0)
        {
            kind = "tell";
        }
        else
        {
            kind = "note";
        }
        if (kind == "tell")
        {
            content = Regex.Replace(content, @"^(?:know\s+)?(?:that\s+)?", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        if (kind != "leave")
        {
            return new LajnaAsk(who, w, kind, null, false, dates, null, content, tone, string.Empty);
        }
        if (child is null)
        {
            school = false;
        }
        // "I'm sick today and won't come": the reason is that they are unwell.
        if (reason is null && leaveType.Length == 0 && UnwellRegex().IsMatch(w))
        {
            string whoIs = child is null ? "I am" : child.Pronoun switch { "he" => "he is", "she" => "she is", _ => "my " + child.Relation + " is" };
            reason = new LajnaReason("as", whoIs + " unwell");
        }
        return new LajnaAsk(who, w, kind, child, school, dates, reason, content, tone, leaveType);
    }

    // The person it goes to, and what it says: "my child Nafees's principal | saying he is on leave from…".
    private static (string Who, string What) Split(string rest)
    {
        Match b = BoundaryRegex().Match(rest);
        if (b.Success && b.Index > 0)
        {
            return (rest[..b.Index].Trim(' ', ','), rest[b.Index..].Trim(' ', ','));
        }
        return (rest.Trim(' ', ','), string.Empty);
    }

    // "my child Nafees's principal" -> "Principal"; "the principal of Nafees's school" -> "Principal";
    // "my manager" -> "manager"; "HR" and addresses as they are.
    private static string Recipient(string who)
    {
        string w = who.Trim();
        if (w.Contains('@', StringComparison.Ordinal))
        {
            return w;
        }
        if (RoleOfRegex().Match(w) is { Success: true } of)
        {
            return Title(of.Groups["role"].Value);
        }
        if (PossessiveRoleRegex().Match(w) is { Success: true } owned)
        {
            return Title(owned.Groups["role"].Value);
        }
        w = Regex.Replace(w, @"^(?:(?:my|our|the|your|his|her|their)\s+)+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        return SchoolRoles.Contains(w.ToLowerInvariant()) ? Title(w) : w;
    }

    private static LajnaChild? Child(string words)
    {
        string pronoun = Regex.IsMatch(words, @"\b(?:he|him|his)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "he"
            : Regex.IsMatch(words, @"\b(?:she|her|hers)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "she" : string.Empty;
        if (MyChildRegex().Match(words) is { Success: true } my)
        {
            string relation = my.Groups["rel"].Value.ToLowerInvariant() switch
            {
                "son" => "son",
                "daughter" => "daughter",
                _ => pronoun switch { "he" => "son", "she" => "daughter", _ => "child" },
            };
            string name = my.Groups["name"].Success ? Capital(my.Groups["name"].Value) : string.Empty;
            return new LajnaChild(name, relation, relation switch { "son" => "he", "daughter" => "she", _ => pronoun });
        }
        if (PossessiveRoleRegex().Match(words) is { Success: true } owned && owned.Groups["owner"].Success)
        {
            string name = Capital(owned.Groups["owner"].Value);
            return new LajnaChild(name, pronoun switch { "he" => "son", "she" => "daughter", _ => "child" }, pronoun);
        }
        return null;
    }

    private static LajnaReason? Reason(string words)
    {
        Match m = ReasonRegex().Match(words);
        if (!m.Success)
        {
            return null;
        }
        string r = Regex.Replace(m.Groups["r"].Value.Trim(), @"\s+(?:from|on|between|till|until)\s*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim(' ', ',');
        // "as he is on leave" says nothing the letter does not already say.
        if (r.Length == 0 || Regex.IsMatch(r, @"^(?:(?:he|she|i|they|we)\s+(?:is|am|are|will\s+be)\s+)?(?:on\s+)?leave\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return null;
        }
        string connector = Regex.Replace(m.Groups["c"].Value.ToLowerInvariant(), @"\s+", " ") switch
        {
            "because" or "since" or "as" => "as",
            "due to" or "owing to" or "because of" => "due to",
            _ => "for",
        };
        return new LajnaReason(connector, r);
    }

    private static string Tone(string word) => word.ToLowerInvariant() switch
    {
        "formal" or "polite" => "formal",
        "short" or "quick" or "brief" or "small" => "short",
        _ => string.Empty,
    };

    private static LajnaDates Range(int d1, string m1, int d2, string m2, string year, string found)
    {
        string a = d1.ToString(CultureInfo.InvariantCulture);
        string b = d2.ToString(CultureInfo.InvariantCulture);
        string y = Year(year);
        // "23rd and 24th October": the two days, not a span.
        if (Regex.IsMatch(found, @"\d\s*(?:st|nd|rd|th)?(?:\s+\p{L}+)?\s*(?:&|\band\b)\s*\p{L}*\s*\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            string both = string.Equals(m1, m2, StringComparison.OrdinalIgnoreCase) ? a + " and " + b + " " + m2 + y : a + " " + m1 + " and " + b + " " + m2 + y;
            return new LajnaDates(both, "on " + both, true, found);
        }
        if (string.Equals(m1, m2, StringComparison.OrdinalIgnoreCase))
        {
            return new LajnaDates(a + "–" + b + " " + m2 + y, "from " + a + " to " + b + " " + m2 + y, true, found);
        }
        return new LajnaDates(a + " " + m1 + "–" + b + " " + m2 + y, "from " + a + " " + m1 + " to " + b + " " + m2 + y, true, found);
    }

    private static string Year(string y) => y.Length == 4 ? " " + y : string.Empty;

    private static int? Num(Match m, string group) =>
        m.Groups[group].Success && int.TryParse(m.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;

    private static bool Valid(int day, int month) => day is >= 1 and <= 31 && month is >= 1 and <= 12;

    private static string Abbrev(int month) => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month);

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Title(string s) => string.Join(' ', s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => Capital(x.ToLowerInvariant())));

    private const string Month = @"(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)";
    private const string Ord = @"(?:st|nd|rd|th)?";
    private const string To = @"\s*(?:-|–|—|&|\bto\b|\btill\b|\buntil\b|\bthrough\b|\bthru\b|\bupto\b|\bup\s+to\b|\band\b)\s*";

    [GeneratedRegex(@"^(?:[\s,]+|(?:please|pls|plz|kindly|hi|hey|hello|ok|okay|lajna|just|quickly|can\s+you|could\s+you|would\s+you|will\s+you|can\s+u|could\s+u|i\s+want\s+to|i\s+wanna|i\s+need\s+to|i\s+would\s+like\s+to|i'd\s+like\s+to|i\s+have\s+to|i\s+must|i\s+should|i\s+wish\s+to|i\s+need|i\s+want|i\s+would\s+like|i'd\s+like|need\s+to|need|want\s+to|go\s+ahead\s+and|help\s+me(?:\s+to)?|let\s+me|let'?s)\b[\s,]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadRegex();

    [GeneratedRegex(@"^(?:(?<verb>write|wrte|wirte|draft|compose|send|sent|snd|prepare|create|make|type|drop|shoot)\s+(?:(?:me|up|out)\s+)?)?(?:(?:a|an|the|one|some)\s+)?(?:(?<tone>short|formal|quick|polite|simple|brief|small|proper)\s+)?(?<obj>(?:(?:sick|casual|leave|absence|official)\s+)*(?:letter|mail|e-?mail|message|note|application|request|reply|msg|leave)s?)?(?:\s+for\s+(?<for>(?:(?:my|our)\s+)?(?:child|son|daughter|kid|ward|boy|girl)\b[^,]*?|(?:\w+\s+)?leave))?\s*\bto\s+(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenRegex();

    [GeneratedRegex(@"^(?:e-?mail|mail|message|msg)\s+(?!to\b|me\b)(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectRegex();

    [GeneratedRegex(@"\s*,\s*|\s+(?=(?:saying|says|said|that|asking|ask|about|regarding|re|requesting|request|informing|inform|telling|tell|reminding|to\s+(?:say|ask|tell|inform|request|let|remind)|for|stating|since|because|as|due\s+to|owing\s+to|explaining|mentioning|on|from|between|this|next|coming|tomorrow|today|thanking|thank|\d)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoundaryRegex();

    [GeneratedRegex(@"^(?<j>saying|says|said|that|asking|ask|reminding|remind|to\s+remind|about|regarding|re|requesting|request|informing|inform|telling|tell|to\s+say|to\s+ask|to\s+tell|to\s+inform|to\s+request|to\s+let\s+(?:him|her|them|you)\s+know|stating|explaining|mentioning|thanking|thank)\b\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JoinRegex();

    [GeneratedRegex(@"\b(?:leave|absent|absence|day\s+off|days\s+off|time\s+off|holiday|vacation|not\s+(?:be\s+)?(?:able\s+to\s+)?(?:come|attend)|can'?t\s+(?:come|attend)|cannot\s+(?:come|attend)|won'?t\s+(?:be\s+able\s+to\s+)?(?:come|attend))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeaveWordsRegex();

    [GeneratedRegex(@"\bmy\s+(?<rel>child|son|daughter|kid|ward|boy|girl)\b(?:\s*,?\s+(?<name>(?!(?:is|has|was|will|would|can|could|should|who|and|for|to|from|in|at|on|had|have|being|needs?|wants?|got|isn'?t|won'?t|can'?t)\b)\p{L}+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MyChildRegex();

    [GeneratedRegex(@"(?:^|\b)(?<owner>\p{L}+)['’]s?\s+(?<role>(?:vice\s+)?principal|(?:class\s+)?teacher|head\s*(?:master|mistress|teacher)|school|(?:class\s+)?coordinator|tutor|warden)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PossessiveRoleRegex();

    [GeneratedRegex(@"^(?:the\s+)?(?<role>(?:vice\s+)?principal|(?:class\s+)?teacher|head\s*(?:master|mistress|teacher)|(?:class\s+)?coordinator|tutor|warden)\s+of\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RoleOfRegex();

    [GeneratedRegex(@"\b(?<c>because\s+of|because|since|as|due\s+to|owing\s+to|for)(?>\s+)(?!(?:a\s+|the\s+)?(?:(?:sick|casual|medical|earned|annual|privilege|maternity|paternity|half[\s-]*day)\s+leave|leave|the\s+days?|these\s+days|today|tomorrow|\d|(?:a|one|two|three|four|five|six|seven|eight|nine|ten|a\s+few|few|some|couple\s+of)\s+(?:days?|weeks?)|because|since|as|due|owing|for)\b)(?<r>[^,.;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReasonRegex();

    [GeneratedRegex(@"\b(?<d1>\d{1,2})[/.\-](?<n1>\d{1,2})(?:[/.\-]\d{2,4})?" + To + @"(?<d2>\d{1,2})[/.\-](?<n2>\d{1,2})(?:[/.\-]\d{2,4})?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumericRangeRegex();

    [GeneratedRegex(@"\b(?<d1>\d{1,2})" + Ord + @"(?:\s+(?:of\s+)?(?<m1>" + Month + @")\b,?)?(?:\s*\d{4})?" + To + @"(?:the\s+)?(?<d2>\d{1,2})" + Ord + @"\s+(?:of\s+)?(?<m2>" + Month + @")\b(?:,?\s*(?<y>\d{4}))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DayFirstRangeRegex();

    [GeneratedRegex(@"\b(?<m1>" + Month + @")\s+(?<d1>\d{1,2})" + Ord + @"\b" + To + @"(?:(?<m2>" + Month + @")\s+)?(?<d2>\d{1,2})" + Ord + @"\b(?:,?\s*(?<y>\d{4}))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonthFirstRangeRegex();

    [GeneratedRegex(@"\b(?<d>\d{1,2})[/.\-](?<n>\d{1,2})(?:[/.\-]\d{2,4})?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumericOneRegex();

    [GeneratedRegex(@"\b(?<d>\d{1,2})" + Ord + @"\s+(?:of\s+)?(?<m>" + Month + @")\b(?:,?\s*(?<y>\d{4}))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DayFirstOneRegex();

    [GeneratedRegex(@"\b(?<m>" + Month + @")\s+(?<d>\d{1,2})" + Ord + @"\b(?:,?\s*(?<y>\d{4}))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonthFirstOneRegex();

    [GeneratedRegex(@"\b(?:(?<near>this|next|coming)\s+)?(?<w1>monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b(?:" + To + @"(?<w2>monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeekdayRegex();

    [GeneratedRegex(@"\b(?:today|tomorrow|next\s+week)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NearRegex();

    [GeneratedRegex(@"\bfor\s+(?:the\s+next\s+)?(?<n>\d{1,2}|two|three|four|five|six|seven|eight|nine|ten|a\s+few|few)\s+(?<u>days?|weeks?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"^(?<v>inform|tell|notify|ask|request|remind|thank|let)\s+(?!(?:me|us|you|how|what|why|when|where|which|who|lajna|anjal|for|about|it|this)\b)(?<who>.+?)\s+(?<what>(?:to|that|about|regarding|for|whether|if|when|know)\b.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerbDirectRegex();

    [GeneratedRegex(@"\b(?<t>sick|casual|medical|earned|annual|privilege|maternity|paternity|half[\s-]*day)\s+leave\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeaveTypeRegex();

    [GeneratedRegex(@"\b(?:sick|unwell|not\s+well|ill|fever|cold|flu)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnwellRegex();
}
