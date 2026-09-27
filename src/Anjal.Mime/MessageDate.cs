using System.Globalization;

namespace Anjal.Mime;

/// <summary>
/// Dates Anjal writes into messages (v1.0.0-rc.8): an RFC 5322 date-time in
/// the configured time zone with its true offset - India by default, so a
/// message sent at 23:24 IST reads "23:24:33 +0530", not "17:54:33 +0000".
/// The zone is <c>ANJAL_TIMEZONE</c> (an IANA name, e.g. <c>Asia/Kolkata</c>);
/// an unknown zone falls back to UTC. The offset is always computed, never
/// written as fixed text, so the time and its label cannot disagree.
/// </summary>
public static class MessageDate
{
    /// <summary>The default zone when none is configured.</summary>
    public const string DefaultZoneId = "Asia/Kolkata";

    /// <summary>The zone dates are written in.</summary>
    public static TimeZoneInfo Zone { get; set; } = Resolve(Environment.GetEnvironmentVariable("ANJAL_TIMEZONE"));

    /// <summary>A zone from its IANA id; UTC when the id is empty or unknown.</summary>
    /// <param name="id">The IANA id, or null for the default.</param>
    /// <returns>The zone.</returns>
    public static TimeZoneInfo Resolve(string? id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(id) ? DefaultZoneId : id.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Format an instant for a Date header in <see cref="Zone"/>.</summary>
    /// <param name="instant">The instant (any offset).</param>
    /// <returns>For example <c>Sun, 27 Sep 2026 23:24:33 +0530</c>.</returns>
    public static string Format(DateTimeOffset instant) => Format(instant, Zone);

    /// <summary>Format an instant for a Date header in a given zone.</summary>
    /// <param name="instant">The instant (any offset).</param>
    /// <param name="zone">The zone.</param>
    /// <returns>The RFC 5322 date-time.</returns>
    public static string Format(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        DateTimeOffset local = TimeZoneInfo.ConvertTime(instant, zone);
        TimeSpan offset = local.Offset;
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        TimeSpan abs = offset.Duration();
        return local.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture)
            + sign + abs.Hours.ToString("00", CultureInfo.InvariantCulture) + abs.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }
}
