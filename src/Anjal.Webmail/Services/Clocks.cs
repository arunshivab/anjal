using System.Collections.Concurrent;

namespace Anjal.Webmail.Services;

/// <summary>
/// The 24-hour or 12-hour clock (owner, 8 Oct 2026): 24-hour unless the organisation chooses
/// otherwise; each person may choose their own, and someone who never chose follows their
/// organisation, including later changes. The organisations' choices are kept here, in the
/// webmail process, so every page can use them without asking the database.
/// </summary>
public static class Clocks
{
    /// <summary>The 24-hour clock: 14:30.</summary>
    public const string TwentyFour = "24";

    /// <summary>The 12-hour clock: 2:30 pm.</summary>
    public const string Twelve = "12";

    private static readonly ConcurrentDictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A stored choice, checked: "24", "12", or empty for none.</summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The choice.</returns>
    public static string Choice(string? value) => value switch
    {
        TwentyFour => TwentyFour,
        Twelve => Twelve,
        _ => string.Empty,
    };

    /// <summary>Note an organisation's clock.</summary>
    /// <param name="tenantSlug">The organisation.</param>
    /// <param name="clock">Its choice; anything but "12" is the 24-hour clock.</param>
    public static void SetDefault(string tenantSlug, string? clock)
    {
        ArgumentNullException.ThrowIfNull(tenantSlug);
        Defaults[tenantSlug] = Choice(clock) == Twelve ? Twelve : TwentyFour;
    }

    /// <summary>An organisation's clock: 24-hour unless it chose otherwise.</summary>
    /// <param name="tenantSlug">The organisation.</param>
    /// <returns>"24" or "12".</returns>
    public static string DefaultOf(string? tenantSlug) =>
        tenantSlug is not null && Defaults.TryGetValue(tenantSlug, out string? clock) ? clock : TwentyFour;

    /// <summary>The clock a person sees: their own choice, else their organisation's.</summary>
    /// <param name="own">The person's choice, or empty.</param>
    /// <param name="tenantSlug">Their organisation.</param>
    /// <returns>"24" or "12".</returns>
    public static string For(string? own, string? tenantSlug) => Choice(own) is { Length: > 0 } c ? c : DefaultOf(tenantSlug);
}
