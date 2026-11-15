using System.Globalization;

namespace Anjal.Webmail.Services;

/// <summary>rc.29 (DES-22, AC-17): small readings the viewer's page needs - an entry's places and a cell's address.</summary>
public static class ViewerLinks
{
    /// <summary>"3" or "3/5" as places; empty for anything else (and for more than two levels).</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The places.</returns>
    public static int[] Places(string? entry)
    {
        if (string.IsNullOrEmpty(entry))
        {
            return Array.Empty<int>();
        }
        string[] parts = entry.Split('/');
        if (parts.Length > 2)
        {
            return Array.Empty<int>();
        }
        var places = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out places[i]) || places[i] >= 2000)
            {
                return Array.Empty<int>();
            }
        }
        return places;
    }

    /// <summary>A cell's address as its column and row ("B12" gives 2 and 12); (0, 0) when it is not one.</summary>
    /// <param name="address">The address.</param>
    /// <returns>The column and row (1-based).</returns>
    public static (int Column, int Row) CellOf(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return (0, 0);
        }
        int column = 0, i = 0;
        while (i < address.Length && char.IsAsciiLetter(address[i]))
        {
            column = (column * 26) + (char.ToUpperInvariant(address[i]) - 'A' + 1);
            i++;
            if (column > 16384)
            {
                return (0, 0);
            }
        }
        return i > 0 && int.TryParse(address.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out int row) && row > 0 ? (column, row) : (0, 0);
    }
}
