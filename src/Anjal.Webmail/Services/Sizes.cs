using System.Globalization;

namespace Anjal.Webmail.Services;

/// <summary>
/// Owner, 9 Oct 2026 ("make them same everywhere with two decimals"): every
/// size Anjal shows - a mailbox, an organisation's storage, the server's
/// disk, a folder, an attachment - is written the same way: KB, MB and GB
/// of 1,024 each (as mailbox sizes always were), with two decimals.
/// </summary>
internal static class Sizes
{
    /// <summary>One KB.</summary>
    internal const long KB = 1024L;

    /// <summary>One MB.</summary>
    internal const long MB = 1024L * 1024;

    /// <summary>One GB: an organisation's storage of 50 GB is 50 of these.</summary>
    internal const long GB = 1024L * 1024 * 1024;

    /// <summary>A size in bytes as it is shown everywhere: "512 B", "57.39 KB", "1.40 MB", "223.83 GB".</summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The size in words.</returns>
    internal static string Text(long bytes) =>
        bytes < KB ? Math.Max(0, bytes).ToString(CultureInfo.InvariantCulture) + " B"
        : bytes < MB ? (bytes / (double)KB).ToString("0.00", CultureInfo.InvariantCulture) + " KB"
        : bytes < GB ? (bytes / (double)MB).ToString("0.00", CultureInfo.InvariantCulture) + " MB"
        : (bytes / (double)GB).ToString("0.00", CultureInfo.InvariantCulture) + " GB";

    /// <summary>A size in MB with two decimals, however small or large (the person's space by folder).</summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The size in MB, "0.01 MB" at the least for a folder that holds anything.</returns>
    internal static string Megabytes(long bytes)
    {
        double mb = bytes / (double)MB;
        return (bytes > 0 && mb < 0.01 ? 0.01 : mb).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>
    /// How much of a whole a part is, in whole percent, rounded to the nearest (half away from
    /// zero) - the same figure on every screen (owner, 9 Oct 2026; DES-11 F10: mailbox fullness and
    /// four other figures still rounded down, so 79.6% showed 79% and was not counted at 80%).
    /// It never says 100% while something is missing, nor 0% when there is something: 99.7% of a
    /// mailbox is shown 99%, not "full"; one passed message in a thousand is 1%, not none.
    /// </summary>
    /// <param name="part">The part.</param>
    /// <param name="whole">The whole.</param>
    /// <returns>0 to 100.</returns>
    internal static int Percent(long part, long whole)
    {
        if (whole <= 0 || part <= 0)
        {
            return 0;
        }
        if (part >= whole)
        {
            return 100;
        }
        return (int)Math.Clamp(Math.Round(part * 100d / whole, MidpointRounding.AwayFromZero), 1, 99);
    }
}
