namespace Anjal.Webmail.Services;

/// <summary>
/// The operators of this Anjal service (rc.14): the people who may open the
/// Anjal console. Named by address in ANJAL_OPERATORS (comma-separated);
/// operators manage the service, never anyone's mail.
/// </summary>
public static class Operators
{
    /// <summary>Every operator's address, in the order ANJAL_OPERATORS gives them.</summary>
    /// <returns>The addresses; empty when none are set.</returns>
    public static IReadOnlyList<string> List() =>
        (Environment.GetEnvironmentVariable("ANJAL_OPERATORS") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when an address is one of the operators.</summary>
    /// <param name="address">The signed-in person's address.</param>
    /// <returns>Whether they may open the Anjal console.</returns>
    public static bool Is(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }
        string list = Environment.GetEnvironmentVariable("ANJAL_OPERATORS") ?? string.Empty;
        foreach (string entry in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(entry, address.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
