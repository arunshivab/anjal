namespace Anjal.Mailbox;

/// <summary>What Anjal says about a file it delivers but cannot fully vouch for (rc.24, item 93, D-196).</summary>
public enum FileRisk
{
    /// <summary>Nothing to say.</summary>
    None = 0,

    /// <summary>An encrypted archive (or a password-protected Office file): no scanner can look inside.</summary>
    Encrypted = 1,

    /// <summary>An Office file with macros: small programs that run when the file is opened.</summary>
    Macros = 2,
}

/// <summary>
/// rc.24 (item 93; owner, 25 Oct 2026: "Deliver with a warning"): encrypted archives and files with
/// macros are delivered, and the reader is warned. The checks read only the file's own structure -
/// a ZIP's directory, an Office file's parts, the headers of RAR and 7-Zip archives - and never open
/// or run anything. A file that cannot be read is simply not warned about.
/// </summary>
public static class FileRisks
{
    private static readonly string[] MacroEndings = { ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm", ".potm", ".ppsm", ".ppam", ".sldm" };

    private static readonly byte[] OleMagic = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
    private static readonly byte[] RarMagic = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 };
    private static readonly byte[] SevenZipMagic = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

    // 7-Zip's AES-256 + SHA-256 coder id.
    private static readonly byte[] SevenZipAes = { 0x06, 0xF1, 0x07, 0x01 };

    // UTF-16LE stream names inside an Office (OLE) file.
    private static readonly byte[] VbaStream = System.Text.Encoding.Unicode.GetBytes("_VBA_PROJECT");
    private static readonly byte[] EncryptedPackage = System.Text.Encoding.Unicode.GetBytes("EncryptedPackage");

    /// <summary>What to warn about for one file.</summary>
    /// <param name="fileName">Its name.</param>
    /// <param name="body">Its bytes, decoded.</param>
    /// <returns>The risk; <see cref="FileRisk.None"/> when there is nothing to say.</returns>
    public static FileRisk Inspect(string fileName, byte[] body)
    {
        System.ArgumentNullException.ThrowIfNull(fileName);
        System.ArgumentNullException.ThrowIfNull(body);
        try
        {
            if (StartsWith(body, OleMagic))
            {
                // An Office 97-2003 file with a VBA project, or a password-protected Office 2007+ file.
                if (IndexOf(body, EncryptedPackage) >= 0)
                {
                    return FileRisk.Encrypted;
                }
                return IndexOf(body, VbaStream) >= 0 ? FileRisk.Macros : FileRisk.None;
            }
            if (body.Length >= 4 && body[0] == 0x50 && body[1] == 0x4B)
            {
                FileRisk zip = InspectZip(body);
                if (zip != FileRisk.None)
                {
                    return zip;
                }
                return EndsWithOneOf(fileName, MacroEndings) ? FileRisk.Macros : FileRisk.None;
            }
            if (StartsWith(body, RarMagic))
            {
                return RarEncrypted(body) ? FileRisk.Encrypted : FileRisk.None;
            }
            if (StartsWith(body, SevenZipMagic))
            {
                return IndexOf(body, SevenZipAes) >= 0 ? FileRisk.Encrypted : FileRisk.None;
            }
            return EndsWithOneOf(fileName, MacroEndings) ? FileRisk.Macros : FileRisk.None;
        }
#pragma warning disable CA1031 // A file that cannot be read is not warned about; delivery never fails on it.
        catch (System.Exception)
        {
            return FileRisk.None;
        }
#pragma warning restore CA1031
    }

    /// <summary>The names of the entries of a ZIP (rc.26, item 85); empty for anything else or a file that cannot be read.</summary>
    /// <param name="body">The file's bytes.</param>
    /// <returns>The names.</returns>
    public static System.Collections.Generic.IReadOnlyList<string> ZipEntryNames(byte[] body)
    {
        System.ArgumentNullException.ThrowIfNull(body);
        var names = new System.Collections.Generic.List<string>();
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
        catch (System.Exception)
        {
            names.Clear();
        }
#pragma warning restore CA1031
        return names;
    }

    // A ZIP: any entry with the "encrypted" flag (bit 0), or an Office part named vbaProject.bin.
    private static FileRisk InspectZip(byte[] b)
    {
        bool encrypted = false;
        bool macros = false;
        int eocd = -1;
        for (int i = b.Length - 22; i >= System.Math.Max(0, b.Length - 65557); i--)
        {
            if (U32(b, i) == 0x06054b50)
            {
                eocd = i;
                break;
            }
        }
        if (eocd >= 0)
        {
            int entries = U16(b, eocd + 10);
            long at = U32(b, eocd + 16);
            for (int n = 0; n < entries && at >= 0 && at + 46 <= b.Length; n++)
            {
                int p = (int)at;
                if (U32(b, p) != 0x02014b50)
                {
                    break;
                }
                int flags = U16(b, p + 8);
                int nameLength = U16(b, p + 28);
                int extra = U16(b, p + 30);
                int comment = U16(b, p + 32);
                if (p + 46 + nameLength > b.Length)
                {
                    break;
                }
                string name = System.Text.Encoding.UTF8.GetString(b, p + 46, nameLength);
                encrypted |= (flags & 1) != 0;
                macros |= name.EndsWith("vbaProject.bin", System.StringComparison.OrdinalIgnoreCase);
                at = p + 46L + nameLength + extra + comment;
            }
        }
        else
        {
            // No directory (a cut-off file): the local headers.
            for (int p = 0; p + 30 <= b.Length; p++)
            {
                if (U32(b, p) == 0x04034b50)
                {
                    int flags = U16(b, p + 6);
                    int nameLength = U16(b, p + 26);
                    encrypted |= (flags & 1) != 0;
                    if (p + 30 + nameLength <= b.Length)
                    {
                        macros |= System.Text.Encoding.UTF8.GetString(b, p + 30, nameLength).EndsWith("vbaProject.bin", System.StringComparison.OrdinalIgnoreCase);
                    }
                    p += 29;
                }
            }
        }
        return encrypted ? FileRisk.Encrypted : macros ? FileRisk.Macros : FileRisk.None;
    }

    // RAR 4: the main header's "password" flag or a file header's "encrypted" flag.
    // RAR 5: an archive encryption header (type 4) right after the signature.
    private static bool RarEncrypted(byte[] b)
    {
        if (b.Length > 7 && b[6] == 0x01 && b.Length > 8 && b[7] == 0x00)
        {
            int p = 8 + 4;  // signature, then the header's CRC32
            (long size, int used) = Vint(b, p);
            p += used;
            (long type, int _) = Vint(b, p);
            return size > 0 && type == 4;
        }
        int at = 7;
        while (at + 7 <= b.Length)
        {
            int type = b[at + 2];
            int flags = U16(b, at + 3);
            int size = U16(b, at + 5);
            if (type == 0x73 && (flags & 0x0080) != 0)
            {
                return true;
            }
            if (type == 0x74 && (flags & 0x0004) != 0)
            {
                return true;
            }
            long next = at + (long)size;
            if (type == 0x74 && at + 11 <= b.Length)
            {
                next += U32(b, at + 7);
            }
            if (size < 7 || next <= at || next > b.Length)
            {
                break;
            }
            at = (int)next;
        }
        return false;
    }

    private static (long Value, int Used) Vint(byte[] b, int p)
    {
        long value = 0;
        int shift = 0;
        int used = 0;
        while (p + used < b.Length && used < 10)
        {
            byte x = b[p + used];
            value |= (long)(x & 0x7F) << shift;
            used++;
            if ((x & 0x80) == 0)
            {
                break;
            }
            shift += 7;
        }
        return (value, used);
    }

    private static int U16(byte[] b, int p) => p + 2 <= b.Length && p >= 0 ? b[p] | (b[p + 1] << 8) : 0;

    private static long U32(byte[] b, int p) => p + 4 <= b.Length && p >= 0 ? (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)) : 0;

    private static bool StartsWith(byte[] b, byte[] prefix) => b.Length >= prefix.Length && b.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static int IndexOf(byte[] b, byte[] needle) => System.MemoryExtensions.IndexOf(b.AsSpan(), needle);

    private static bool EndsWithOneOf(string name, string[] endings)
    {
        string lower = name.Trim().ToLowerInvariant();
        foreach (string e in endings)
        {
            if (lower.EndsWith(e, System.StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
