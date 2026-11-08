namespace Anjal.Mailbox;

/// <summary>
/// rc.27 (item 72.8, D-314): how long Anjal keeps trying to deliver an application's mail - chosen per
/// application, 5 days (the queue's own) by default. A patient reminder can stop after 4 hours; a report
/// may keep trying.
/// </summary>
public static class AppGiveUp
{
    /// <summary>The hours an application may choose.</summary>
    public static readonly IReadOnlyList<int> Choices = new[] { 1, 4, 24, 48, 120 };

    /// <summary>The default: the queue's own 5 days.</summary>
    public const int DefaultHours = 120;

    /// <summary>The time for a chosen number of hours; the default for anything not on the list.</summary>
    /// <param name="hours">The hours.</param>
    /// <returns>The time.</returns>
    public static System.TimeSpan Of(int hours) => System.TimeSpan.FromHours(Choices.Contains(hours) ? hours : DefaultHours);

    /// <summary>
    /// The give-up time of the application an SMTP key belongs to, from the organisation's
    /// applications document ("apps"); the default when the key is not an application's.
    /// </summary>
    /// <param name="appsJson">The organisation's "apps" document, or null.</param>
    /// <param name="smtpUser">The key's user name the mail was sent with.</param>
    /// <returns>The time.</returns>
    public static System.TimeSpan ForKey(string? appsJson, string? smtpUser)
    {
        if (string.IsNullOrWhiteSpace(appsJson) || string.IsNullOrEmpty(smtpUser))
        {
            return Of(DefaultHours);
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(appsJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return Of(DefaultHours);
            }
            foreach (System.Text.Json.JsonElement app in doc.RootElement.EnumerateArray())
            {
                if (app.TryGetProperty("Keys", out System.Text.Json.JsonElement keys) && keys.ValueKind == System.Text.Json.JsonValueKind.Array
                    && keys.EnumerateArray().Any(k => k.TryGetProperty("Username", out System.Text.Json.JsonElement u) && string.Equals(u.GetString(), smtpUser, System.StringComparison.OrdinalIgnoreCase)))
                {
                    return Of(app.TryGetProperty("GiveUpHours", out System.Text.Json.JsonElement h) && h.TryGetInt32(out int hours) ? hours : DefaultHours);
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // An unreadable document gives the default.
        }
        return Of(DefaultHours);
    }
}
