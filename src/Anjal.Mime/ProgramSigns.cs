namespace Anjal.Mime;

/// <summary>
/// rc.26 (item 85, D-173), moved here in rc.29: how a file is known to be a program - by its name, by its first
/// bytes, or by what a ZIP holds. The mail server's attachment rules (<c>AttachmentPolicy</c>) and the separate
/// viewer process (AC-17) use the same rules from here, and the viewer needs nothing else of the mail libraries.
/// </summary>
public static class ProgramSigns
{
    private static readonly string[] Programs =
    {
        ".exe", ".com", ".bat", ".cmd", ".msi", ".msp", ".scr", ".pif", ".cpl", ".dll", ".sys", ".drv",
        ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh", ".ps1", ".psm1", ".hta", ".reg", ".jar", ".app",
        ".sh", ".bash", ".py", ".pl", ".rb", ".php", ".lnk", ".inf", ".vb", ".ws", ".msc", ".scf", ".apk", ".deb", ".rpm",
    };

    /// <summary>Whether a file name is a program or script, by its ending.</summary>
    /// <param name="fileName">The name.</param>
    /// <returns>True when it is one.</returns>
    public static bool IsProgramName(string fileName) => EndsWithOneOf(fileName, Programs);

    /// <summary>
    /// Whether a file's bytes are a program, whatever its name says: a Windows program (MZ), a Linux one (ELF), a
    /// Mac one (Mach-O), a Java class, a script that names its own interpreter (#!), or an archive holding a program
    /// (a ZIP, a JAR among them).
    /// </summary>
    /// <param name="body">The file's bytes, decoded.</param>
    /// <returns>True when it is a program.</returns>
    public static bool IsProgramContent(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length < 4)
        {
            return false;
        }
        uint head = (uint)(body[0] << 24 | body[1] << 16 | body[2] << 8 | body[3]);
        if (IsWindowsProgram(body) || head == 0x7F454C46 || head is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE)
        {
            return true;
        }
        if (body[0] == (byte)'#' && body[1] == (byte)'!')
        {
            return true;
        }
        IReadOnlyList<string> entries = ZipEntryNames(body);
        return !IsOfficeContainer(entries) && entries.Any(n => IsProgramName(n) || n.EndsWith(".class", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a ZIP's entry names are an Office document's (it holds [Content_Types].xml).</summary>
    /// <param name="entries">The names.</param>
    /// <returns>True for an Office document.</returns>
    public static bool IsOfficeContainer(IReadOnlyList<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Any(n => string.Equals(n, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The names of the entries of a ZIP, read from its local headers; empty for anything else.</summary>
    /// <param name="body">The file's bytes.</param>
    /// <returns>The names.</returns>
    public static IReadOnlyList<string> ZipEntryNames(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var names = new List<string>();
        if (body.Length < 4 || body[0] != 0x50 || body[1] != 0x4B)
        {
            return names;
        }
        try
        {
            for (int p = 0; p + 30 <= body.Length && names.Count < 10000; p++)
            {
                if (U32(body, p) == 0x04034b50)
                {
                    int nameLength = U16(body, p + 26);
                    if (p + 30 + nameLength <= body.Length)
                    {
                        names.Add(System.Text.Encoding.UTF8.GetString(body, p + 30, nameLength));
                    }
                    p += 29;
                }
            }
        }
#pragma warning disable CA1031 // A file that cannot be read gives no names.
        catch (Exception)
        {
            names.Clear();
        }
#pragma warning restore CA1031
        return names;
    }

    // MZ, and the PE header it points to ("PE\0\0"): a text that happens to begin "MZ" is not one.
    private static bool IsWindowsProgram(byte[] b)
    {
        if (b.Length < 0x40 || b[0] != 0x4D || b[1] != 0x5A)
        {
            return false;
        }
        int pe = b[0x3C] | (b[0x3D] << 8) | (b[0x3E] << 16) | (b[0x3F] << 24);
        return pe > 0 && pe + 4 <= b.Length && b[pe] == (byte)'P' && b[pe + 1] == (byte)'E' && b[pe + 2] == 0 && b[pe + 3] == 0;
    }

    private static bool EndsWithOneOf(string fileName, string[] endings)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }
        string lower = fileName.Trim().ToLowerInvariant();
        foreach (string ending in endings)
        {
            if (lower.EndsWith(ending, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static int U16(byte[] b, int p) => p + 2 <= b.Length && p >= 0 ? b[p] | (b[p + 1] << 8) : 0;

    private static long U32(byte[] b, int p) => p + 4 <= b.Length && p >= 0 ? (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)) : 0;
}
