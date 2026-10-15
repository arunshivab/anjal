using System.Text;
using System.Text.RegularExpressions;

namespace Anjal.Webmail.Services;

/// <summary>
/// rc.18 (design review [8f]): the letters Lajna drafts - a leave request, or a short note - in the
/// language of the letter (English, or Tamil for "In தமிழ்"), with the person's own details as they
/// typed them. These are letters, not the screen's words, so they are not in the word list.
/// </summary>
public static partial class LajnaLetters
{
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
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(toName);
        ArgumentNullException.ThrowIfNull(what);
        ArgumentNullException.ThrowIfNull(myName);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(style);
        string first = toName.Contains('@', StringComparison.Ordinal) || toName.Length <= 3 ? toName.Split('@')[0].ToUpperInvariant() is { Length: <= 3 } team ? team + " team" : toName.Split('@')[0] : RoleGreeting(toName) ?? MailboxService.FirstName(toName, to);
        string dates = Dates(what);
        string reason = Reason(what, dates);
        string sign = myName + (signature.Trim().Length > 0 ? "\n" + signature.Trim() : string.Empty);
        string subject;
        var b = new StringBuilder();
        if (leave)
        {
            subject = dates.Length > 0 ? "Leave request: " + dates : "Leave request";
            switch (style)
            {
                case "ta":
                    subject = dates.Length > 0 ? "விடுப்பு கோரிக்கை: " + dates : "விடுப்பு கோரிக்கை";
                    b.Append("அன்புள்ள ").Append(first).Append(",\n\n");
                    b.Append(reason.Length > 0 ? reason + " காரணமாக " : string.Empty).Append(dates.Length > 0 ? dates + " " : string.Empty).Append("விடுப்பு வழங்குமாறு கேட்டுக்கொள்கிறேன்.\n");
                    if (cover is not null)
                    {
                        b.Append("கடந்த முறை போல், என் பணிகளைக் கவனிக்க ").Append(cover.Name).Append(" அவர்களிடம் ஏற்பாடு செய்கிறேன்.\n");
                    }
                    b.Append("\nநன்றி,\n").Append(sign);
                    break;
                case "formal":
                    b.Append("Dear ").Append(first).Append(",\n\n");
                    b.Append("I am writing to request leave of absence").Append(dates.Length > 0 ? (dates.Contains('–', StringComparison.Ordinal) ? " from " + dates : " " + Spoken(dates)) : string.Empty).Append(reason.Length > 0 ? ", on account of " + reason : string.Empty).Append(".\n\n");
                    b.Append(cover is null ? "I will ensure that my responsibilities are covered during this period" : "I will ensure that my responsibilities are covered during this period, as " + cover.Name + " kindly did on the last occasion").Append(".\n\n");
                    b.Append("I would be grateful for your approval.\n\nYours sincerely,\n").Append(sign);
                    break;
                case "short":
                    b.Append("Hi ").Append(first).Append(",\n\n");
                    b.Append("Could I take leave").Append(dates.Length > 0 ? " " + Spoken(dates) : string.Empty).Append(reason.Length > 0 ? " for " + reason : string.Empty).Append("? ");
                    b.Append(cover is null ? "I'll arrange cover." : "I'll ask " + cover.Name + " to cover, as last time.").Append("\n\nThanks,\n").Append(sign);
                    break;
                default:
                    b.Append("Dear ").Append(first).Append(",\n\n");
                    b.Append("I would like to request leave").Append(dates.Length > 0 ? " " + Spoken(dates) : string.Empty).Append(reason.Length > 0 ? " for " + reason : string.Empty).Append(".\n\n");
                    b.Append(cover is null ? "I will arrange for my work to be covered while I am away." : "I will arrange for my work to be covered while I am away, as " + cover.Name + " kindly did last time.").Append("\n\n");
                    b.Append("Thank you,\n").Append(sign);
                    break;
            }
        }
        else
        {
            string topic = what.Trim().TrimEnd('.', '?', '!');
            subject = Capital(topic.Length > 60 ? topic[..60].TrimEnd() + "…" : topic);
            string sentence = Capital(topic) + ".";
            switch (style)
            {
                case "ta":
                    b.Append("அன்புள்ள ").Append(first).Append(",\n\n").Append(sentence).Append("\n\nநன்றி,\n").Append(sign);
                    break;
                case "formal":
                    b.Append("Dear ").Append(first).Append(",\n\nI am writing regarding the following: ").Append(topic).Append(".\n\nI would be grateful for your reply.\n\nYours sincerely,\n").Append(sign);
                    break;
                case "short":
                    b.Append("Hi ").Append(first).Append(",\n\n").Append(sentence).Append("\n\nThanks,\n").Append(sign);
                    break;
                default:
                    b.Append("Dear ").Append(first).Append(",\n\n").Append(sentence).Append("\n\nThank you,\n").Append(sign);
                    break;
            }
        }
        return new LajnaDraft(to, toName, subject, b.ToString(), cover, style);
    }

    // "6-10 Nov", "6 to 10 November", "from 6 Nov to 10 Nov", "on 14 Oct" - as written, with an en dash.
    private static string Dates(string what)
    {
        Match m = DateRegex().Match(what);
        if (!m.Success)
        {
            return string.Empty;
        }
        string d = Regex.Replace(m.Value.Trim(), @"^(?:from|on)\s+", string.Empty, RegexOptions.IgnoreCase);
        d = Regex.Replace(d, @"\b(?:this|next|coming|monday|tuesday|wednesday|thursday|friday|saturday|sunday|today|tomorrow|week)\b", w => w.Value.ToLowerInvariant() is string x && x.EndsWith("day", StringComparison.Ordinal) && x is not ("today" or "tomorrow") ? Capital(x) : w.Value.ToLowerInvariant(), RegexOptions.IgnoreCase);
        return Regex.Replace(d, @"\s*(?:-|–|\bto\b)\s*", "–");
    }

    // rc.20 (owner, 14 Oct 2026: "asking for leave on Friday" lost the Friday): a single day reads
    // "on Friday" or "on 14 Oct" in the letter; a span ("6–10 Nov") and "tomorrow" read as they are.
    private static string Spoken(string d) =>
        !d.Contains('–', StringComparison.Ordinal) && Regex.IsMatch(d, @"^(?:(?:this|next|coming)\s+)?(?:mon|tues|wednes|thurs|fri|satur|sun)day$|^\d{1,2}\s+\p{L}+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? "on " + d
            : d;

    private static string Reason(string what, string dates)
    {
        string w = DateRegex().Replace(what, " ");
        Match m = Regex.Match(w, @"\bfor\s+(?<r>(?!leave\b)[^,.;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string r = m.Success ? m.Groups["r"].Value.Trim() : string.Empty;
        r = Regex.Replace(r, @"\s+(?:from|on|between)\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        return dates.Length > 0 && r.EndsWith(dates, StringComparison.Ordinal) ? r[..^dates.Length].Trim() : r;
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    [GeneratedRegex(@"(?:from\s+)?\b\d{1,2}(?:\s+(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*)?\s*(?:-|–|to)\s*\d{1,2}\s+(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\b|\b(?:on\s+)?\d{1,2}\s+(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\b|\b(?:on\s+|from\s+)?(?:(?:this|next|coming)\s+)?(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday)(?:\s*(?:-|–|to)\s*(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday))?\b|\b(?:today|tomorrow|next week)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();

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
