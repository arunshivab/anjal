namespace Anjal.Store;

/// <summary>
/// rc.26 (DES-14 §17, D-229): the per-send return path of an application send,
/// <c>bounce+&lt;send id&gt;@&lt;return domain&gt;</c>, the send id in lowercase base32 (26 characters,
/// so that a server that changes the case of an address cannot break it).
/// </summary>
public static class AppBounceAddress
{
    /// <summary>The local part's start.</summary>
    public const string Prefix = "bounce+";

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>The return path of a send.</summary>
    /// <param name="sendId">The send.</param>
    /// <param name="returnDomain">The return domain.</param>
    /// <returns>The address.</returns>
    public static string For(System.Guid sendId, string returnDomain)
    {
        System.ArgumentNullException.ThrowIfNull(returnDomain);
        return Prefix + Encode(sendId) + "@" + returnDomain.Trim().ToLowerInvariant();
    }

    /// <summary>The send a return path names, or null when it is not one.</summary>
    /// <param name="address">The address.</param>
    /// <returns>The send id, or null.</returns>
    public static System.Guid? SendOf(string address)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        int at = address.LastIndexOf('@');
        string local = (at < 0 ? address : address[..at]).Trim().ToLowerInvariant();
        return local.StartsWith(Prefix, System.StringComparison.Ordinal) ? Decode(local[Prefix.Length..]) : null;
    }

    /// <summary>Whether an address's local part is a return path's.</summary>
    /// <param name="address">The address.</param>
    /// <returns>True when it starts with bounce+.</returns>
    public static bool IsBounceAddress(string address)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        return address.Trim().StartsWith(Prefix, System.StringComparison.OrdinalIgnoreCase) && address.Contains('@', System.StringComparison.Ordinal);
    }

    /// <summary>A send id as 26 lowercase base32 characters.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The text.</returns>
    public static string Encode(System.Guid id)
    {
        byte[] bytes = id.ToByteArray(bigEndian: true);
        var text = new System.Text.StringBuilder(26);
        int buffer = 0;
        int bits = 0;
        foreach (byte b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
        {
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }
        return text.ToString();
    }

    /// <summary>The send id of 26 base32 characters, or null.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The id, or null.</returns>
    public static System.Guid? Decode(string text)
    {
        System.ArgumentNullException.ThrowIfNull(text);
        if (text.Length != 26)
        {
            return null;
        }
        byte[] bytes = new byte[16];
        int buffer = 0;
        int bits = 0;
        int n = 0;
        foreach (char c in text.ToLowerInvariant())
        {
            int v = Alphabet.IndexOf(c, System.StringComparison.Ordinal);
            if (v < 0)
            {
                return null;
            }
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                if (n == 16)
                {
                    break;
                }
                bytes[n++] = (byte)((buffer >> (bits - 8)) & 0xFF);
                bits -= 8;
            }
        }
        return n == 16 ? new System.Guid(bytes, bigEndian: true) : null;
    }
}
