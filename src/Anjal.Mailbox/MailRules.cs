using System.Text.Json;

namespace Anjal.Mailbox;

/// <summary>One condition of a <see cref="MailRule"/> (rc.12, items 24 and 31).</summary>
public sealed class RuleCondition
{
    /// <summary>What is tested: from, to, subject, body, attachment, unsubscribe, outside or group.</summary>
    public string Field { get; set; } = "from";

    /// <summary>How: contains, is, startswith, notcontains. Ignored by attachment, unsubscribe and outside.</summary>
    public string Op { get; set; } = "contains";

    /// <summary>The value compared, without regard to case; for group, the group's name.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// A person's rule: when every condition holds for an arriving message,
/// the actions are done. Rules run in order, top first; the first that
/// matches is the one applied. Mail Anjal has put in Junk is never moved
/// out of it by a rule.
/// </summary>
public sealed class MailRule
{
    /// <summary>Identifier.</summary>
    public System.Guid Id { get; set; } = System.Guid.NewGuid();

    /// <summary>The rule's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether it runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The conditions; all must hold.</summary>
    public IList<RuleCondition> Conditions { get; init; } = new List<RuleCondition>();

    /// <summary>The folder to file the message in; empty leaves it in INBOX.</summary>
    public string MoveTo { get; set; } = string.Empty;

    /// <summary>Mark it read on arrival.</summary>
    public bool MarkRead { get; set; }

    /// <summary>Flag it on arrival.</summary>
    public bool Flag { get; set; }
}

/// <summary>What a rule decided for one message.</summary>
/// <param name="Rule">The rule that matched.</param>
/// <param name="MoveTo">The folder, or empty.</param>
/// <param name="MarkRead">Mark it read.</param>
/// <param name="Flag">Flag it.</param>
public sealed record RuleOutcome(MailRule Rule, string MoveTo, bool MarkRead, bool Flag);

/// <summary>The facts about an arriving message that rules can test.</summary>
public sealed class RuleSubject
{
    /// <summary>The From header (name and address).</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>The To and Cc headers.</summary>
    public string To { get; init; } = string.Empty;

    /// <summary>The subject.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>The text of the body.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>It carries an attachment.</summary>
    public bool HasAttachment { get; init; }

    /// <summary>It carries a List-Unsubscribe header (a newsletter).</summary>
    public bool HasUnsubscribe { get; init; }

    /// <summary>The sender's address is outside the recipient's organisation.</summary>
    public bool FromOutside { get; init; }

    /// <summary>The sender's address, for group tests.</summary>
    public string FromAddress { get; init; } = string.Empty;
}

/// <summary>
/// Reading and judging a mailbox's rules. The rules are the mailbox's
/// "rules" document; contact groups (for "From is in the group") are its
/// "contact-groups" document, both written by the webmail.
/// </summary>
public static class MailRules
{
    /// <summary>The document kind holding the rules.</summary>
    public const string Kind = "rules";

    /// <summary>The document kind holding contact groups.</summary>
    public const string GroupsKind = "contact-groups";

    /// <summary>At most this many rules per mailbox.</summary>
    public const int MaxRules = 100;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Read the rules document; an empty or broken one gives no rules.</summary>
    /// <param name="json">The document.</param>
    public static IReadOnlyList<MailRule> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return System.Array.Empty<MailRule>();
        }
        try
        {
            return JsonSerializer.Deserialize<List<MailRule>>(json, Json) ?? new List<MailRule>();
        }
        catch (JsonException)
        {
            return System.Array.Empty<MailRule>();
        }
    }

    /// <summary>Write the rules document.</summary>
    /// <param name="rules">The rules.</param>
    public static string Write(IEnumerable<MailRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return JsonSerializer.Serialize(rules.Take(MaxRules).ToList());
    }

    /// <summary>Read group names and members from the contact-groups document.</summary>
    /// <param name="json">The document.</param>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseGroups(string? json)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(System.StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }
            foreach (JsonElement g in doc.RootElement.EnumerateArray())
            {
                if (g.TryGetProperty("Name", out JsonElement name) && name.ValueKind == JsonValueKind.String
                    && g.TryGetProperty("Addresses", out JsonElement members) && members.ValueKind == JsonValueKind.Array)
                {
                    result[name.GetString() ?? string.Empty] = members.EnumerateArray()
                        .Where(m => m.ValueKind == JsonValueKind.String)
                        .Select(m => m.GetString() ?? string.Empty)
                        .ToList();
                }
            }
        }
        catch (JsonException)
        {
            // A broken document means no groups.
        }
        return result;
    }

    /// <summary>The first enabled rule all of whose conditions hold, or null.</summary>
    /// <param name="rules">The rules, in order.</param>
    /// <param name="message">The message.</param>
    /// <param name="groups">Contact groups, by name.</param>
    public static RuleOutcome? Evaluate(IEnumerable<MailRule> rules, RuleSubject message, IReadOnlyDictionary<string, IReadOnlyList<string>> groups)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(groups);
        foreach (MailRule rule in rules)
        {
            if (!rule.Enabled || rule.Conditions.Count == 0)
            {
                continue;
            }
            if (rule.Conditions.All(c => Holds(c, message, groups)))
            {
                return new RuleOutcome(rule, rule.MoveTo.Trim(), rule.MarkRead, rule.Flag);
            }
        }
        return null;
    }

    /// <summary>Whether one condition holds for a message.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="message">The message.</param>
    /// <param name="groups">Contact groups, by name.</param>
    public static bool Holds(RuleCondition condition, RuleSubject message, IReadOnlyDictionary<string, IReadOnlyList<string>> groups)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(groups);
        string value = condition.Value.Trim();
        switch (condition.Field)
        {
            case "attachment":
                return message.HasAttachment;
            case "unsubscribe":
                return message.HasUnsubscribe;
            case "outside":
                return message.FromOutside;
            case "group":
                return groups.TryGetValue(value, out IReadOnlyList<string>? members)
                    && members.Contains(message.FromAddress, System.StringComparer.OrdinalIgnoreCase);
            default:
                break;
        }
        if (value.Length == 0)
        {
            return false;
        }
        string text = condition.Field switch
        {
            "from" => message.From,
            "to" => message.To,
            "subject" => message.Subject,
            "body" => message.Body,
            _ => string.Empty,
        };
        return condition.Op switch
        {
            "is" => string.Equals(text.Trim(), value, System.StringComparison.OrdinalIgnoreCase)
                || (condition.Field == "from" && string.Equals(message.FromAddress, value, System.StringComparison.OrdinalIgnoreCase)),
            "startswith" => text.TrimStart().StartsWith(value, System.StringComparison.OrdinalIgnoreCase),
            "notcontains" => !text.Contains(value, System.StringComparison.OrdinalIgnoreCase),
            _ => text.Contains(value, System.StringComparison.OrdinalIgnoreCase),
        };
    }
}
