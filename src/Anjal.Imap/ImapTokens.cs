using System.Globalization;
using System.Text;

namespace Anjal.Imap;

/// <summary>One piece of an IMAP command: an atom, a string (quoted or literal), or a parenthesised list.</summary>
internal sealed class ImapToken
{
    private ImapToken(char kind, string text, byte[]? bytes, List<ImapToken>? items)
    {
        this.Kind = kind;
        this.Text = text;
        this.Bytes = bytes;
        this.Items = items ?? new List<ImapToken>();
    }

    /// <summary>'a' atom, 's' string, 'l' list.</summary>
    public char Kind { get; }

    /// <summary>The text (a string's bytes as UTF-8).</summary>
    public string Text { get; }

    /// <summary>A string's bytes.</summary>
    public byte[]? Bytes { get; }

    /// <summary>A list's items.</summary>
    public List<ImapToken> Items { get; }

    /// <summary>True for an atom equal to <paramref name="word"/>, ignoring case.</summary>
    /// <param name="word">The word.</param>
    /// <returns>True when it is.</returns>
    public bool Is(string word) => this.Kind == 'a' && string.Equals(this.Text, word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Read the tokens of one command (its tag and name included).</summary>
    /// <param name="data">The command, literals inline after their "{n}" and line break.</param>
    /// <returns>The tokens.</returns>
    /// <exception cref="FormatException">The lists are nested deeper than <see cref="MaxDepth"/>.</exception>
    public static List<ImapToken> Read(byte[] data)
    {
        int i = 0;
        return ReadList(data, ref i, ')', top: true, level: 0);
    }

    /// <summary>The deepest list a command may hold; no command of a real mail app comes near it.</summary>
    public const int MaxDepth = 32;

    private static List<ImapToken> ReadList(byte[] d, ref int i, char close, bool top, int level)
    {
        if (level > MaxDepth)
        {
            throw new FormatException("Lists nested too deeply.");
        }
        var list = new List<ImapToken>();
        while (i < d.Length)
        {
            byte c = d[i];
            if (c is (byte)' ' or 13 or 10)
            {
                i++;
                continue;
            }
            if (c == (byte)close && !top)
            {
                i++;
                return list;
            }
            if (c == (byte)'(')
            {
                i++;
                list.Add(new ImapToken('l', string.Empty, null, ReadList(d, ref i, ')', top: false, level + 1)));
                continue;
            }
            if (c == (byte)'"')
            {
                i++;
                var sb = new List<byte>();
                while (i < d.Length && d[i] != (byte)'"')
                {
                    if (d[i] == (byte)'\\' && i + 1 < d.Length)
                    {
                        i++;
                    }
                    sb.Add(d[i++]);
                }
                i++;
                byte[] bytes = sb.ToArray();
                list.Add(new ImapToken('s', Encoding.UTF8.GetString(bytes), bytes, null));
                continue;
            }
            if (c == (byte)'{')
            {
                int end = Array.IndexOf(d, (byte)'}', i);
                string inside = end > i ? Encoding.ASCII.GetString(d, i + 1, end - i - 1).TrimEnd('+') : string.Empty;
                if (end > i && int.TryParse(inside, NumberStyles.None, CultureInfo.InvariantCulture, out int size))
                {
                    int start = end + 1;
                    if (start < d.Length && d[start] == 13)
                    {
                        start++;
                    }
                    if (start < d.Length && d[start] == 10)
                    {
                        start++;
                    }
                    size = Math.Min(size, d.Length - start);
                    byte[] bytes = d.AsSpan(start, size).ToArray();
                    list.Add(new ImapToken('s', Encoding.UTF8.GetString(bytes), bytes, null));
                    i = start + size;
                    continue;
                }
            }
            // An atom; a "[...]" inside it is taken whole (BODY[HEADER.FIELDS (DATE FROM)]<0.100>).
            var atom = new StringBuilder();
            int depth = 0;
            while (i < d.Length)
            {
                char ch = (char)d[i];
                if (depth == 0 && (ch is ' ' or '(' or '\r' or '\n' || (ch == ')' && !top)))
                {
                    break;
                }
                if (depth == 0 && ch == ')' && top)
                {
                    break;
                }
                if (ch == '[')
                {
                    depth++;
                }
                else if (ch == ']')
                {
                    depth = Math.Max(0, depth - 1);
                }
                atom.Append(ch);
                i++;
            }
            if (atom.Length == 0)
            {
                i++;
                continue;
            }
            list.Add(new ImapToken('a', atom.ToString(), null, null));
        }
        return list;
    }
}
