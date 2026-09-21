using System.Globalization;
using System.Text;

namespace Anjal.Server;

/// <summary>
/// Builds the delivery-failure notice a sender receives when an outbound
/// message could not be delivered: an RFC 3464 <c>multipart/report</c> with
/// a readable explanation, a machine-readable delivery-status part, and the
/// original message's headers (not its body, which the sender already has).
/// <para>
/// The notice is filed directly into the sender's own INBOX. It is never
/// sent outbound: a bounce addressed to an arbitrary envelope sender is how
/// servers become sources of backscatter, and only local mailboxes can
/// submit mail here anyway.
/// </para>
/// </summary>
public static class BounceNotice
{
    /// <summary>Headers of the original kept in the notice, at most this many bytes.</summary>
    public const int MaxOriginalHeaderBytes = 64 * 1024;

    /// <summary>Build the notice.</summary>
    /// <param name="hostName">This server's name, for the Reporting-MTA and From.</param>
    /// <param name="message">The message that failed.</param>
    /// <param name="reason">Why it failed, as the last attempt reported it.</param>
    /// <param name="permanent">True for a permanent refusal; false when retries ran out.</param>
    /// <param name="now">The time of the notice.</param>
    public static byte[] Build(string hostName, Anjal.Store.OutboundMessage message, string reason, bool permanent, System.DateTimeOffset now)
    {
        System.ArgumentNullException.ThrowIfNull(hostName);
        System.ArgumentNullException.ThrowIfNull(message);
        System.ArgumentNullException.ThrowIfNull(reason);

        string boundary = "anjal-dsn-" + System.Guid.NewGuid().ToString("N");
        string date = now.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture);
        string safeReason = OneLine(reason);
        string recipient = OneLine(message.EnvelopeTo);
        string status = permanent ? "5.0.0" : "4.4.7";
        string action = "failed";

        var sb = new StringBuilder();
        sb.Append("From: Mail Delivery System <mailer-daemon@").Append(hostName).Append(">\r\n");
        sb.Append("To: ").Append(OneLine(message.EnvelopeFrom)).Append("\r\n");
        sb.Append("Subject: Undelivered: your message to ").Append(recipient).Append("\r\n");
        sb.Append("Date: ").Append(date).Append("\r\n");
        sb.Append("Message-ID: <").Append(System.Guid.NewGuid().ToString("N")).Append('@').Append(hostName).Append(">\r\n");
        sb.Append("Auto-Submitted: auto-replied\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("Content-Type: multipart/report; report-type=delivery-status; boundary=\"").Append(boundary).Append("\"\r\n");
        sb.Append("\r\n");

        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Type: text/plain; charset=utf-8\r\n\r\n");
        sb.Append("Your message to ").Append(recipient).Append(" could not be delivered.\r\n\r\n");
        sb.Append(permanent
            ? "The receiving server refused it, so it will not be retried.\r\n\r\n"
            : "Delivery kept failing until the retry period ran out.\r\n\r\n");
        sb.Append("What the receiving side reported:\r\n\r\n    ").Append(safeReason).Append("\r\n\r\n");
        sb.Append("The message is still in your Sent folder. Check the address, or send it again later.\r\n\r\n");

        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Type: message/delivery-status\r\n\r\n");
        sb.Append("Reporting-MTA: dns; ").Append(hostName).Append("\r\n");
        sb.Append("Arrival-Date: ").Append(message.CreatedAt.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)).Append("\r\n\r\n");
        sb.Append("Final-Recipient: rfc822; ").Append(recipient).Append("\r\n");
        sb.Append("Action: ").Append(action).Append("\r\n");
        sb.Append("Status: ").Append(status).Append("\r\n");
        sb.Append("Diagnostic-Code: smtp; ").Append(safeReason).Append("\r\n\r\n");

        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Type: text/rfc822-headers\r\n\r\n");
        sb.Append(OriginalHeaders(message.RawBytes));
        sb.Append("\r\n--").Append(boundary).Append("--\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string OriginalHeaders(byte[] raw)
    {
        int end = System.Math.Min(raw.Length, MaxOriginalHeaderBytes);
        for (int i = 0; i + 3 < end; i++)
        {
            if (raw[i] == '\r' && raw[i + 1] == '\n' && raw[i + 2] == '\r' && raw[i + 3] == '\n')
            {
                end = i + 2;
                break;
            }
        }
        return Encoding.ASCII.GetString(raw, 0, end);
    }

    /// <summary>Reduce text to one line for a header or diagnostic field.</summary>
    private static string OneLine(string text)
    {
        var sb = new StringBuilder(System.Math.Min(text.Length, 900));
        foreach (char c in text)
        {
            if (sb.Length >= 900)
            {
                break;
            }
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString().Trim();
    }
}
