namespace Anjal.Server;

/// <summary>
/// Header completion for submitted mail (v1.0.0-rc.9, DEF-073; RFC 6409 8.2 and
/// 8.3): a message a user's mail program submits without a Date or Message-ID
/// gets one, added at the end of its header so the trace lines stay first.
/// </summary>
public static class SubmissionHeaders
{
    /// <summary>The message with a Date and a Message-ID, adding whichever is missing.</summary>
    /// <param name="raw">The submitted message.</param>
    /// <param name="senderDomain">Domain for a new Message-ID (the sender's own).</param>
    /// <param name="now">The time for a new Date.</param>
    /// <returns>The completed message; the same bytes when nothing was missing.</returns>
    public static byte[] Complete(byte[] raw, string senderDomain, System.DateTimeOffset now)
    {
        System.ArgumentNullException.ThrowIfNull(raw);
        System.ArgumentNullException.ThrowIfNull(senderDomain);
        bool hasDate = false;
        bool hasMessageId = false;
        int headerEnd = raw.Length;   // where the blank line starts, or the end if there is none
        int pos = 0;
        while (pos < raw.Length)
        {
            if (raw[pos] == (byte)'\r' || raw[pos] == (byte)'\n')
            {
                headerEnd = pos;
                break;
            }
            int nl = System.Array.IndexOf(raw, (byte)'\n', pos);
            int end = nl < 0 ? raw.Length : nl + 1;
            if (raw[pos] != (byte)' ' && raw[pos] != (byte)'\t')
            {
                string start = System.Text.Encoding.ASCII.GetString(raw, pos, System.Math.Min(12, end - pos));
                hasDate |= start.StartsWith("Date:", System.StringComparison.OrdinalIgnoreCase);
                hasMessageId |= start.StartsWith("Message-ID:", System.StringComparison.OrdinalIgnoreCase);
            }
            pos = end;
        }
        if (hasDate && hasMessageId)
        {
            return raw;
        }
        var added = new System.Text.StringBuilder();
        if (headerEnd > 0 && raw[headerEnd - 1] != (byte)'\n')
        {
            added.Append("\r\n");   // a header with no final line break
        }
        if (!hasDate)
        {
            added.Append("Date: ").Append(Anjal.Mime.MessageDate.Format(now)).Append("\r\n");
        }
        if (!hasMessageId)
        {
            string domain = senderDomain.Length > 0 ? senderDomain : "localhost";
            added.Append("Message-ID: <").Append(System.Guid.NewGuid().ToString("N")).Append('@').Append(domain).Append(">\r\n");
        }
        byte[] insert = System.Text.Encoding.ASCII.GetBytes(added.ToString());
        var result = new byte[raw.Length + insert.Length];
        System.Array.Copy(raw, 0, result, 0, headerEnd);
        insert.CopyTo(result, headerEnd);
        System.Array.Copy(raw, headerEnd, result, headerEnd + insert.Length, raw.Length - headerEnd);
        return result;
    }
}
