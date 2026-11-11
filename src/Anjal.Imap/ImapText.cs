using System.Globalization;
using System.Text;

namespace Anjal.Imap;

/// <summary>rc.28 (DES-20): how IMAP writes strings, dates, folder names and sets of numbers.</summary>
public static class ImapText
{
    /// <summary>A quoted string, or a literal when the text needs one (line breaks, 8-bit).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The IMAP form.</returns>
    public static string Quote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Any(c => c is '\r' or '\n' || c > 126))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            return "{" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "}\r\n" + text;
        }
        return "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>NIL for empty, otherwise <see cref="Quote"/>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The IMAP form.</returns>
    public static string NString(string? text) => string.IsNullOrEmpty(text) ? "NIL" : Quote(text);

    /// <summary>An INTERNALDATE ("09-Nov-2026 14:02:00 +0000").</summary>
    /// <param name="when">The time.</param>
    /// <returns>The quoted date.</returns>
    public static string Date(DateTimeOffset when)
    {
        string offset = (when.Offset < TimeSpan.Zero ? "-" : "+") + when.Offset.Duration().ToString("hhmm", CultureInfo.InvariantCulture);
        return "\"" + when.ToString("dd-MMM-yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " " + offset + "\"";
    }

    /// <summary>Read an IMAP date ("9-Nov-2026") or date-time ("09-Nov-2026 14:02:00 +0530").</summary>
    /// <param name="text">The text.</param>
    /// <returns>The time, or null.</returns>
    public static DateTimeOffset? ParseDate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string t = text.Trim().Trim('"');
        string[] formats = { "d-MMM-yyyy HH:mm:ss zzz", "dd-MMM-yyyy HH:mm:ss zzz", "d-MMM-yyyy", "dd-MMM-yyyy" };
        // .NET writes the offset as +05:30; IMAP as +0530.
        if (t.Length > 5 && (t[^5] == '+' || t[^5] == '-') && t[^4..].All(char.IsAsciiDigit))
        {
            t = t[..^2] + ":" + t[^2..];
        }
        return DateTimeOffset.TryParseExact(t, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset d) ? d : null;
    }

    /// <summary>A folder name as IMAP writes it (modified UTF-7, RFC 3501 §5.1.3).</summary>
    /// <param name="name">The name.</param>
    /// <returns>The encoded name.</returns>
    public static string EncodeFolder(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder();
        var pending = new StringBuilder();
        void Flush()
        {
            if (pending.Length == 0)
            {
                return;
            }
            byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(pending.ToString());
            sb.Append('&').Append(Convert.ToBase64String(utf16).TrimEnd('=').Replace('/', ',')).Append('-');
            pending.Clear();
        }
        foreach (char c in name)
        {
            if (c >= 0x20 && c <= 0x7E)
            {
                Flush();
                sb.Append(c == '&' ? "&-" : c.ToString());
            }
            else
            {
                pending.Append(c);
            }
        }
        Flush();
        return sb.ToString();
    }

    /// <summary>A folder name from IMAP's modified UTF-7.</summary>
    /// <param name="encoded">The encoded name.</param>
    /// <returns>The name.</returns>
    public static string DecodeFolder(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        var sb = new StringBuilder();
        int i = 0;
        while (i < encoded.Length)
        {
            char c = encoded[i];
            if (c != '&')
            {
                sb.Append(c);
                i++;
                continue;
            }
            int end = encoded.IndexOf('-', i + 1);
            if (end < 0)
            {
                sb.Append(encoded[i..]);
                break;
            }
            string b64 = encoded[(i + 1)..end].Replace(',', '/');
            if (b64.Length == 0)
            {
                sb.Append('&');
            }
            else
            {
                try
                {
                    byte[] bytes = Convert.FromBase64String(b64.PadRight((b64.Length + 3) / 4 * 4, '='));
                    sb.Append(Encoding.BigEndianUnicode.GetString(bytes));
                }
                catch (FormatException)
                {
                    sb.Append(encoded, i, end - i + 1);
                }
            }
            i = end + 1;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Read a set of numbers ("1:5,7,9:*"); "*" is the largest. Ranges either way round; nothing outside
    /// 1..<paramref name="largest"/> is returned unless it was named directly.
    /// </summary>
    /// <param name="set">The set.</param>
    /// <param name="largest">The value of "*".</param>
    /// <returns>A test for whether a number is in the set; null when the set cannot be read.</returns>
    public static Func<long, bool>? ParseSet(string set, long largest)
    {
        ArgumentNullException.ThrowIfNull(set);
        var ranges = new List<(long Low, long High)>();
        foreach (string piece in set.Split(','))
        {
            string[] ends = piece.Split(':');
            if (ends.Length is < 1 or > 2 || ends.Any(e => e.Length == 0))
            {
                return null;
            }
            long? a = Number(ends[0], largest);
            long? b = ends.Length == 2 ? Number(ends[1], largest) : a;
            if (a is null || b is null)
            {
                return null;
            }
            ranges.Add((Math.Min(a.Value, b.Value), Math.Max(a.Value, b.Value)));
        }
        return n => ranges.Any(r => n >= r.Low && n <= r.High);
    }

    /// <summary>Numbers written as a compact set ("3:5,9").</summary>
    /// <param name="numbers">The numbers.</param>
    /// <returns>The set.</returns>
    public static string WriteSet(IEnumerable<long> numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        var sorted = numbers.Distinct().Order().ToList();
        var parts = new List<string>();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
            {
                j++;
            }
            parts.Add(i == j ? sorted[i].ToString(CultureInfo.InvariantCulture) : sorted[i].ToString(CultureInfo.InvariantCulture) + ":" + sorted[j].ToString(CultureInfo.InvariantCulture));
            i = j + 1;
        }
        return string.Join(',', parts);
    }

    private static long? Number(string text, long largest) =>
        text == "*" ? Math.Max(largest, 1) : long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long n) && n > 0 ? n : null;
}
