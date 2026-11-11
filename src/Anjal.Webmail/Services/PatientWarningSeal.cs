using Microsoft.AspNetCore.DataProtection;

namespace Anjal.Webmail.Services;

/// <summary>
/// rc.28 (D-331, option C): what a patient warning was about, sealed into the compose form so that
/// "Send anyway" sends only the mail the person was warned about. If the mail has other signs or goes
/// to other outside addresses when it is sent again, the seal no longer matches and the warning shows
/// again. The seal is the webmail's own data protection: the browser cannot read or forge it, and it
/// never holds the mail's words (only the signs and addresses the warning itself shows).
/// </summary>
public static class PatientWarningSeal
{
    private const string Purpose = "Anjal.Webmail.PatientWarning.v1";

    /// <summary>The seal for a warning's signs.</summary>
    /// <param name="protection">The webmail's data protection.</param>
    /// <param name="signs">What <see cref="MailboxService.PatientWarningAsync"/> found, as shown.</param>
    /// <returns>The sealed text for the form's hidden field.</returns>
    public static string Seal(IDataProtectionProvider protection, IReadOnlyList<string> signs)
    {
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(signs);
        return protection.CreateProtector(Purpose).Protect(Canonical(signs));
    }

    /// <summary>True when the seal was made for exactly these signs.</summary>
    /// <param name="protection">The webmail's data protection.</param>
    /// <param name="seal">The hidden field's value, as posted.</param>
    /// <param name="signs">What the check finds in the mail now.</param>
    /// <returns>True when nothing new would be warned about.</returns>
    public static bool Matches(IDataProtectionProvider protection, string? seal, IReadOnlyList<string> signs)
    {
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(signs);
        if (string.IsNullOrEmpty(seal) || signs.Count == 0)
        {
            return false;
        }
        try
        {
            return string.Equals(protection.CreateProtector(Purpose).Unprotect(seal), Canonical(signs), StringComparison.Ordinal);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The signs the seal was made for, or none when it does not open.</summary>
    /// <param name="protection">The webmail's data protection.</param>
    /// <param name="seal">The hidden field's value, as posted.</param>
    /// <returns>The signs, in the order they were shown.</returns>
    public static IReadOnlyList<string> Open(IDataProtectionProvider protection, string? seal)
    {
        ArgumentNullException.ThrowIfNull(protection);
        if (string.IsNullOrEmpty(seal))
        {
            return Array.Empty<string>();
        }
        try
        {
            return protection.CreateProtector(Purpose).Unprotect(seal).Split("; ", StringSplitOptions.None);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return Array.Empty<string>();
        }
    }

    // The same joining the warning's address uses (Program.cs, "patient=").
    private static string Canonical(IReadOnlyList<string> signs) => string.Join("; ", signs);
}
