using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Anjal.Webmail.Services;

/// <summary>What a typed search asks for, once its operators are read.</summary>
/// <param name="Words">The words left to search for.</param>
/// <param name="Sender">from: - a sender's name or address, or null.</param>
/// <param name="After">The first day (yyyy-MM-dd), or null.</param>
/// <param name="Before">The last day (yyyy-MM-dd), or null.</param>
/// <param name="Attachment">has: - true for mail with attachments.</param>
/// <param name="AllFolders">in:all - true to search every folder.</param>
public sealed record SearchAsk(string Words, string? Sender, string? After, string? Before, bool Attachment, bool AllFolders)
{
    /// <summary>True when the search held at least one operator or date phrase.</summary>
    public bool HasOperators => this.Sender is not null || this.After is not null || this.Before is not null || this.Attachment || this.AllFolders;
}

/// <summary>
/// rc.16, design review [6g] and 5b.2 (owner, 11 Oct 2026): a search that understands. Operators
/// typed in the box - from:meera, has:pdf (or has:attachment), after:2026-10-01, before:..., in:all -
/// and the phrases today, yesterday, this week, last week and last month become filters, shown as
/// chips under the box; what is left is searched as words.
/// </summary>
public static partial class SearchOperators
{
    /// <summary>Read a typed search.</summary>
    /// <param name="text">What was typed.</param>
    /// <param name="today">Today in the person's zone.</param>
    /// <param name="weekStart">The first day of the person's week ("monday" or "sunday").</param>
    /// <returns>The words and the filters.</returns>
    public static SearchAsk Parse(string? text, DateOnly today, string weekStart)
    {
        ArgumentNullException.ThrowIfNull(weekStart);
        string rest = text ?? string.Empty;
        string? sender = null, after = null, before = null;
        bool attachment = false, all = false;
        foreach (Match m in Operator().Matches(rest))
        {
            string key = m.Groups[1].Value.ToLowerInvariant();
            string value = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            switch (key)
            {
                case "from":
                    sender = value;
                    break;
                case "has":
                    attachment = true;
                    break;
                case "in":
                    all = value.Equals("all", StringComparison.OrdinalIgnoreCase) || all;
                    break;
                case "after":
                case "since":
                    after = Iso(value) ?? after;
                    break;
                case "before":
                case "until":
                    before = Iso(value) ?? before;
                    break;
                default:
                    break;
            }
        }
        rest = Operator().Replace(rest, " ");
        DateOnly monday = today.AddDays(-(((int)today.DayOfWeek - (weekStart == "sunday" ? 0 : 1) + 7) % 7));
        (string Phrase, DateOnly From, DateOnly To)[] phrases =
        {
            ("last week", monday.AddDays(-7), monday.AddDays(-1)),
            ("this week", monday, today),
            ("last month", new DateOnly(today.Year, today.Month, 1).AddMonths(-1), new DateOnly(today.Year, today.Month, 1).AddDays(-1)),
            ("this month", new DateOnly(today.Year, today.Month, 1), today),
            ("yesterday", today.AddDays(-1), today.AddDays(-1)),
            ("today", today, today),
        };
        foreach ((string phrase, DateOnly from, DateOnly to) in phrases)
        {
            Match p = Regex.Match(rest, @"(?<![\w-])" + Regex.Escape(phrase) + @"(?![\w'’-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (p.Success)
            {
                after ??= from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                before ??= to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                rest = rest.Remove(p.Index, p.Length);
                break;
            }
        }
        return new SearchAsk(Spaces().Replace(rest, " ").Trim(), sender, after, before, attachment, all);
    }

    private static string? Iso(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    /// <summary>The address of a search with these filters, as the search page reads it.</summary>
    /// <param name="ask">What was asked.</param>
    /// <param name="scope">The folder searched ("all" for every folder).</param>
    /// <param name="from">Where the search started.</param>
    /// <returns>A /search address.</returns>
    public static string Link(SearchAsk ask, string scope, string from)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(from);
        var sb = new StringBuilder("/search?q=").Append(Uri.EscapeDataString(ask.Words))
            .Append("&scope=").Append(Uri.EscapeDataString(ask.AllFolders ? "all" : scope))
            .Append("&from=").Append(Uri.EscapeDataString(from));
        if (ask.Sender is not null)
        {
            sb.Append("&sender=").Append(Uri.EscapeDataString(ask.Sender));
        }
        if (ask.After is not null)
        {
            sb.Append("&after=").Append(ask.After);
        }
        if (ask.Before is not null)
        {
            sb.Append("&before=").Append(ask.Before);
        }
        if (ask.Attachment)
        {
            sb.Append("&attach=1");
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"(?<![\w-])(from|has|in|after|since|before|until):(""([^""]*)""|(\S+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Operator();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
