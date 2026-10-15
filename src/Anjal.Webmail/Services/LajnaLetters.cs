using System.Text;
using System.Text.RegularExpressions;

namespace Anjal.Webmail.Services;

/// <summary>
/// rc.18 (design review [8f]): the letters Lajna drafts - a leave request, or a short note - in the
/// language of the letter (English, or Tamil for "In தமிழ்"), with the person's own details as they
/// typed them. These are letters, not the screen's words, so they are not in the word list.
/// rc.21 (owner, 15 Oct 2026): the letter is written from what <see cref="LajnaRequest"/> read - the
/// days in one form ("from 17 to 19 October"), the reason, a child's school leave letter, and a note
/// that tells, asks, thanks or is about something.
/// </summary>
public static partial class LajnaLetters
{
    private static readonly string[] Titles = { "principal", "vice principal", "teacher", "class teacher", "headmaster", "headmistress", "head master", "head mistress", "head teacher", "coordinator", "class coordinator", "tutor", "warden", "manager", "director", "dean", "supervisor", "sir", "madam", "doctor", "boss", "administrator", "officer", "superintendent", "registrar", "secretary", "matron", "accountant", "hod", "in-charge", "incharge", "head" };

    // Short names that are departments, greeted as a team ("Dear HR team"); "Rao" or "Ali" are people.
    private static readonly string[] Departments = { "hr", "it", "ops", "qa", "mrd", "pr", "ot", "icu", "opd", "er", "edp", "mis" };

    private static readonly string[] Honorifics = { "dr", "prof", "mr", "mrs", "ms", "miss", "shri", "smt" };

    /// <summary>Write a draft from what was asked.</summary>
    /// <param name="to">The address.</param>
    /// <param name="toName">Who that is.</param>
    /// <param name="what">What the mail asks or is about.</param>
    /// <param name="leave">True for a leave request.</param>
    /// <param name="cover">Who covered last time, or null.</param>
    /// <param name="myName">The person's name.</param>
    /// <param name="signature">The person's signature, as text.</param>
    /// <param name="style">normal, formal, short or ta.</param>
    /// <returns>The draft.</returns>
    public static LajnaDraft Write(string to, string toName, string what, bool leave, LajnaCover? cover, string myName, string signature, string style)
    {
        ArgumentNullException.ThrowIfNull(what);
        return Write(to, toName, LajnaRequest.FromWhat(what, leave), cover, myName, signature, style);
    }

    /// <summary>Write a draft from a request Lajna read.</summary>
    /// <param name="to">The address, or empty when Lajna does not know it.</param>
    /// <param name="toName">Who that is.</param>
    /// <param name="ask">What was asked.</param>
    /// <param name="cover">Who covered last time, or null.</param>
    /// <param name="myName">The person's name.</param>
    /// <param name="signature">The person's signature, as text.</param>
    /// <param name="style">normal, formal, short or ta.</param>
    /// <returns>The draft.</returns>
    public static LajnaDraft Write(string to, string toName, LajnaAsk ask, LajnaCover? cover, string myName, string signature, string style)
    {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(toName);
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(myName);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(style);
        string first = Greeting(to, toName);
        string sign = myName + (signature.Trim().Length > 0 ? "\n" + signature.Trim() : string.Empty);
        (string subject, string body) = ask switch
        {
            { Leave: true, Child: not null } => ChildLeave(ask, ask.Child, first, sign, style),
            { Leave: true } => OwnLeave(ask, cover, first, sign, style),
            _ => Note(ask, first, sign, style),
        };
        return new LajnaDraft(to, toName, subject, body, ask.Leave && ask.Child is null ? cover : null, style);
    }

    private static (string Subject, string Body) OwnLeave(LajnaAsk ask, LajnaCover? cover, string first, string sign, string style)
    {
        LajnaDates d = ask.Dates;
        string when = d.Any ? " " + d.Spoken : string.Empty;
        string why = ask.Reason?.Phrase ?? string.Empty;
        var b = new StringBuilder();
        string type = ask.LeaveType.Length > 0 ? ask.LeaveType + " " : string.Empty;
        string subject = (type.Length > 0 ? Capital(type) + "leave request" : "Leave request") + (d.Any ? ": " + d.Display : string.Empty);
        switch (style)
        {
            case "ta":
                subject = d.Any ? "விடுப்பு கோரிக்கை: " + d.Display : "விடுப்பு கோரிக்கை";
                b.Append("அன்புள்ள ").Append(first).Append(",\n\n");
                b.Append(ask.Reason is null ? string.Empty : ask.Reason.Text + " காரணமாக ").Append(d.Any ? d.Display + " " : string.Empty).Append("விடுப்பு வழங்குமாறு கேட்டுக்கொள்கிறேன்.\n");
                if (cover is not null)
                {
                    b.Append("கடந்த முறை போல், என் பணிகளைக் கவனிக்க ").Append(cover.Name).Append(" அவர்களிடம் ஏற்பாடு செய்கிறேன்.\n");
                }
                b.Append("\nநன்றி,\n").Append(sign);
                break;
            case "formal":
                string formalWhy = ask.Reason is null ? string.Empty : ask.Reason.Connector == "as" ? ", as " + ask.Reason.Text : ", on account of " + ask.Reason.Text;
                b.Append("Dear ").Append(first).Append(",\n\n");
                b.Append(type.Length > 0 ? "I am writing to request " + type + "leave" : "I am writing to request leave of absence").Append(when).Append(formalWhy).Append(".\n\n");
                b.Append(cover is null ? "I will ensure that my responsibilities are covered during this period" : "I will ensure that my responsibilities are covered during this period, as " + cover.Name + " kindly did on the last occasion").Append(".\n\n");
                b.Append("I would be grateful for your approval.\n\nYours sincerely,\n").Append(sign);
                break;
            case "short":
                b.Append("Hi ").Append(first).Append(",\n\n");
                b.Append("Could I take ").Append(type).Append("leave").Append(when).Append(why).Append("? ");
                b.Append(cover is null ? "I'll arrange cover." : "I'll ask " + cover.Name + " to cover, as last time.").Append("\n\nThanks,\n").Append(sign);
                break;
            default:
                b.Append("Dear ").Append(first).Append(",\n\n");
                b.Append("I would like to request ").Append(type).Append("leave").Append(when).Append(why).Append(".\n\n");
                b.Append(cover is null ? "I will arrange for my work to be covered while I am away." : "I will arrange for my work to be covered while I am away, as " + cover.Name + " kindly did last time.").Append("\n\n");
                b.Append("Thank you,\n").Append(sign);
                break;
        }
        return (subject, b.ToString());
    }

    // rc.21 (owner, 15 Oct 2026: "a leave letter to my child Nafees's principal saying he is on leave
    // from 17th october to 19th october"): a parent's letter to the school.
    private static (string Subject, string Body) ChildLeave(LajnaAsk ask, LajnaChild child, string first, string sign, string style)
    {
        LajnaDates d = ask.Dates;
        string when = d.Any ? " " + d.Spoken : string.Empty;
        string why = ask.Reason?.Phrase ?? string.Empty;
        string named = child.Name.Length > 0 ? " " + child.Name : string.Empty;
        string whom = child.Pronoun switch { "he" => "him", "she" => "her", _ => string.Empty };
        string days = !d.Any ? string.Empty : d.Span ? " for these days" : " for the day";
        string subject = "Leave of absence" + (child.Name.Length > 0 ? ": " + child.Name : string.Empty) + (d.Any ? (child.Name.Length > 0 ? ", " : ": ") + d.Display : string.Empty);
        var b = new StringBuilder();
        switch (style)
        {
            case "ta":
                string rel = child.Relation switch { "son" => "மகன்", "daughter" => "மகள்", _ => "குழந்தை" };
                subject = "விடுப்பு கோரிக்கை" + (child.Name.Length > 0 ? ": " + child.Name : string.Empty) + (d.Any ? (child.Name.Length > 0 ? ", " : ": ") + d.Display : string.Empty);
                b.Append("அன்புள்ள ").Append(first).Append(",\n\n");
                b.Append("என் ").Append(rel).Append(named).Append(d.Any ? " " + d.Display + (d.Span ? " தேதிகளில்" : " அன்று") : string.Empty).Append(" பள்ளிக்கு வர இயலாது");
                b.Append(ask.Reason is null ? string.Empty : " (" + ask.Reason.Text + ")").Append(". தயவுசெய்து விடுப்பு வழங்குமாறு கேட்டுக்கொள்கிறேன்.\n");
                b.Append("\nநன்றி,\n").Append(sign);
                break;
            case "formal":
                b.Append("Dear ").Append(first).Append(",\n\n");
                b.Append("I respectfully submit that my ").Append(child.Relation).Append(named).Append(" will not be able to attend school").Append(when).Append(why).Append(".\n\n");
                b.Append("I request you to kindly grant ").Append(whom.Length > 0 ? whom + " " : string.Empty).Append("leave").Append(d.Any ? " for the said period" : string.Empty).Append(".\n\n");
                b.Append("Yours sincerely,\n").Append(sign);
                break;
            case "short":
                b.Append("Hi ").Append(first).Append(",\n\n");
                b.Append(child.Name.Length > 0 ? child.Name : "My " + child.Relation).Append(" will be absent").Append(when).Append(why).Append(". Please excuse ").Append(whom.Length > 0 ? whom : "the absence").Append(".\n\n");
                b.Append("Thanks,\n").Append(sign);
                break;
            default:
                b.Append("Dear ").Append(first).Append(",\n\n");
                b.Append("I am writing to let you know that my ").Append(child.Relation).Append(named).Append(" will be absent from school").Append(when).Append(why).Append(".\n\n");
                b.Append("Kindly grant ").Append(whom.Length > 0 ? whom + " " : string.Empty).Append("leave").Append(days).Append(".\n\n");
                b.Append("Thank you,\n").Append(sign);
                break;
        }
        return (subject, b.ToString());
    }

    private static (string Subject, string Body) Note(LajnaAsk ask, string first, string sign, string style)
    {
        string content = ask.Content.Trim().TrimEnd('.', '?', '!');
        string sentence;
        string subject;
        switch (ask.Kind)
        {
            case "tell":
                sentence = "I would like to let you know that " + Lower(content) + ".";
                subject = Capital(content);
                break;
            case "ask":
            case "request":
                if (Regex.Match(content, @"^for\s+(?<x>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) is { Success: true } wanted)
                {
                    sentence = "I would like to request " + wanted.Groups["x"].Value + ".";
                    subject = "Request: " + wanted.Groups["x"].Value;
                }
                else if (Regex.IsMatch(content, @"^(?:(?:him|her|them|you)\s+)?to\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    string doing = Regex.Replace(content, @"^(?:(?:him|her|them|you)\s+)?to\s+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    sentence = "Could you please " + doing + "?";
                    subject = Capital(doing);
                }
                else if (Regex.IsMatch(content, @"^(?:whether|if|when|where|what|why|how|who|which|about)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    sentence = "Could you please let me know " + content + "?";
                    subject = Capital(Regex.Replace(content, @"^about\s+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                }
                else if (ask.Kind == "request")
                {
                    sentence = "I would like to request " + content + ".";
                    subject = "Request: " + content;
                }
                else
                {
                    sentence = "Could you please let me know " + content + "?";
                    subject = Capital(content);
                }
                break;
            case "remind":
                string rest = Regex.Replace(content, @"^(?:(?:him|her|them|you)\s+)?", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                sentence = "This is a gentle reminder " + (Regex.IsMatch(rest, @"^(?:to|about|of|that)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? rest : "about " + rest) + ".";
                subject = "Reminder: " + Regex.Replace(rest, @"^(?:to|about|of|that)\s+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                break;
            case "thank":
                string thanks = Regex.Replace(content, @"^(?:you|him|her|them)\s+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                sentence = "Thank you" + (thanks.Length > 0 ? " " + thanks : string.Empty) + ".";
                subject = "Thank you";
                break;
            case "about":
                sentence = "I am writing about " + content + ".";
                subject = Capital(ask.What.Trim().TrimEnd('.', '?', '!'));
                break;
            default:
                sentence = content.Length > 0 ? Capital(content) + "." : string.Empty;
                subject = Capital(content);
                break;
        }
        subject = subject.Length > 60 ? subject[..60].TrimEnd() + "…" : subject;
        var b = new StringBuilder();
        switch (style)
        {
            case "ta":
                b.Append("அன்புள்ள ").Append(first).Append(",\n\n").Append(sentence).Append("\n\nநன்றி,\n").Append(sign);
                break;
            case "formal":
                b.Append("Dear ").Append(first).Append(",\n\n").Append(sentence).Append("\n\nI would be grateful for your reply.\n\nYours sincerely,\n").Append(sign);
                break;
            case "short":
                b.Append("Hi ").Append(first).Append(",\n\n").Append(sentence).Append("\n\nThanks,\n").Append(sign);
                break;
            default:
                b.Append("Dear ").Append(first).Append(",\n\n").Append(sentence).Append("\n\nThank you,\n").Append(sign);
                break;
        }
        return (subject, b.ToString());
    }

    // "Dear Meera", "Dear HR team", "Dear Human resources team", "Dear Principal", "Dear Dr Rao",
    // "Dear Hospital Administrator", "Dear Meera and Rao".
    private static string Greeting(string to, string toName)
    {
        string name = toName.Trim();
        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && (Titles.Contains(name.ToLowerInvariant()) || (!name.Contains('@', StringComparison.Ordinal) && Titles.Contains(words[^1].ToLowerInvariant()))))
        {
            return string.Join(' ', words.Select(w => w.Length <= 3 && Departments.Contains(w.ToLowerInvariant()) ? w.ToUpperInvariant() : Capital(w.ToLowerInvariant())));
        }
        if (name.Contains('@', StringComparison.Ordinal) || name.Length <= 3)
        {
            string local = name.Split('@')[0];
            return Departments.Contains(local.ToLowerInvariant()) ? local.ToUpperInvariant() + " team" : Capital(local);
        }
        if (Regex.IsMatch(name, @"\s(?:and|&)\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return Capital(name);
        }
        if (words.Length > 1 && Honorifics.Contains(words[0].TrimEnd('.').ToLowerInvariant()))
        {
            return Capital(words[0].TrimEnd('.')) + " " + words[1];
        }
        return RoleGreeting(name) is string team ? Capital(team) : MailboxService.FirstName(name, to);
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // "The meeting is moved" -> "the meeting is moved"; "I", names and short capitals stay as typed.
    private static string Lower(string s) =>
        s.Length > 1 && char.IsUpper(s[0]) && char.IsLower(s[1]) && !s.StartsWith("I ", StringComparison.Ordinal) && Regex.IsMatch(s, @"^(?:The|A|An|My|Our|This|That|It|We|There|Your)\b", RegexOptions.CultureInvariant)
            ? char.ToLowerInvariant(s[0]) + s[1..]
            : s;

    // Screen pictures, 12 Oct 2026: a letter to "Human resources <hr@...>" began "Dear Human,". A department
    // or desk is greeted by its whole name: "Dear Human resources team,".
    private static readonly string[] RoleWords = { "resources", "team", "office", "desk", "department", "committee", "services", "support", "accounts", "reception", "administration", "helpdesk" };

    private static string? RoleGreeting(string name)
    {
        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!words.Any(w => RoleWords.Contains(w.ToLowerInvariant().Trim(',', '.'))))
        {
            return null;
        }
        string last = words[^1].ToLowerInvariant();
        return last is "team" or "office" or "desk" or "committee" or "department" or "helpdesk" ? name : name + " team";
    }
}
