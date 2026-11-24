using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Anjal.Examples.WebmailDemo;

/// <summary>
/// rc.29 (DES-22): small real files for the demo's letters, so the viewers have something to show - a PDF with
/// text, a Word file, a workbook, a ZIP whose folder has no entry of its own, and a ZIP locked with a password. All
/// written here by hand; the demo uses no file library (only the viewer process does, DES-22 §11).
/// </summary>
internal static class DemoFiles
{
    private static readonly uint[] CrcTable = MakeCrcTable();

    /// <summary>A one-page A4 PDF with one line of text for each entry of <paramref name="lines"/>.</summary>
    public static byte[] Pdf(params string[] lines)
    {
        var text = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"BT /F1 14 Tf 72 {760 - (22 * i)} Td ({lines[i]}) Tj ET\n");
        }
        byte[] content = Encoding.ASCII.GetBytes(text.ToString());
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            string.Create(CultureInfo.InvariantCulture, $"<< /Length {content.Length} >>\nstream\n") + text + "endstream",
        ];
        var output = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int n = 0; n < objects.Length; n++)
        {
            offsets.Add(output.Length);
            output.Append(CultureInfo.InvariantCulture, $"{n + 1} 0 obj\n{objects[n]}\nendobj\n");
        }
        int xref = output.Length;
        output.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            output.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        output.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(output.ToString());
    }

    /// <summary>
    /// A Word file with a heading, paragraphs and a table whose first row is bold. In a paragraph, text between * is
    /// bold and text between _ is italic (AC-24, so the run formatting shows in the demo).
    /// </summary>
    public static byte[] Docx(string heading, string[] paragraphs, string[][] table)
    {
        var body = new StringBuilder();
        body.Append("<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>").Append(SecurityElement.Escape(heading)).Append("</w:t></w:r></w:p>");
        foreach (string p in paragraphs)
        {
            body.Append("<w:p>");
            bool bold = false, italic = false;
            var run = new StringBuilder();
            void Flush()
            {
                if (run.Length > 0)
                {
                    string props = (bold ? "<w:b/>" : string.Empty) + (italic ? "<w:i/>" : string.Empty);
                    body.Append("<w:r>").Append(props.Length > 0 ? "<w:rPr>" + props + "</w:rPr>" : string.Empty).Append("<w:t xml:space=\"preserve\">").Append(SecurityElement.Escape(run.ToString())).Append("</w:t></w:r>");
                    run.Clear();
                }
            }
            foreach (char c in p)
            {
                if (c is '*' or '_')
                {
                    Flush();
                    bold ^= c == '*';
                    italic ^= c == '_';
                }
                else
                {
                    run.Append(c);
                }
            }
            Flush();
            body.Append("</w:p>");
        }
        body.Append("<w:tbl>");
        for (int r = 0; r < table.Length; r++)
        {
            body.Append("<w:tr>");
            foreach (string cell in table[r])
            {
                body.Append("<w:tc><w:p><w:r>").Append(r == 0 ? "<w:rPr><w:b/></w:rPr>" : string.Empty).Append("<w:t>").Append(SecurityElement.Escape(cell)).Append("</w:t></w:r></w:p></w:tc>");
            }
            body.Append("</w:tr>");
        }
        body.Append("</w:tbl>");
        return Zip(
            ("[Content_Types].xml", Text("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>")),
            ("_rels/.rels", Text("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>")),
            ("word/document.xml", Text("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + body + "</w:body></w:document>")));
    }

    /// <summary>A workbook with one sheet of text and numbers (a number is any cell that parses as one).</summary>
    public static byte[] Xlsx(string sheet, string[][] rows)
    {
        var data = new StringBuilder();
        for (int r = 0; r < rows.Length; r++)
        {
            data.Append(CultureInfo.InvariantCulture, $"<row r=\"{r + 1}\">");
            for (int c = 0; c < rows[r].Length; c++)
            {
                string reference = string.Create(CultureInfo.InvariantCulture, $"{(char)('A' + c)}{r + 1}");
                string value = rows[r][c];
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    data.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"><v>{value}</v></c>");
                }
                else
                {
                    data.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(value)}</t></is></c>");
                }
            }
            data.Append("</row>");
        }
        return Zip(
            ("[Content_Types].xml", Text("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>")),
            ("_rels/.rels", Text("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>")),
            ("xl/workbook.xml", Text("<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"" + SecurityElement.Escape(sheet) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>")),
            ("xl/_rels/workbook.xml.rels", Text("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>")),
            ("xl/worksheets/sheet1.xml", Text("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" + data + "</sheetData></worksheet>")));
    }

    /// <summary>A ZIP of the given entries; a name with a folder in it gets no entry for the folder, as many tools write it.</summary>
    public static byte[] Zip(params (string Name, byte[] Body)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] body) in entries)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(5.5));
                using Stream s = entry.Open();
                s.Write(body);
            }
        }
        return ms.ToArray();
    }

    /// <summary>
    /// A ZIP locked with the old ZipCrypto password scheme, stored (not packed): the kind many tools still write, so
    /// the viewer's password screens have a real one to show.
    /// </summary>
    public static byte[] LockedZip(string password, params (string Name, byte[] Body)[] entries)
    {
        using var output = new MemoryStream();
        using var central = new MemoryStream();
        using var w = new BinaryWriter(output);
        using var c = new BinaryWriter(central);
        const ushort DosTime = 9 << 11;
        const ushort DosDate = ((2026 - 1980) << 9) | (10 << 5) | 1;
        foreach ((string name, byte[] body) in entries)
        {
            uint[] keys = [0x12345678, 0x23456789, 0x34567890];
            foreach (byte b in Encoding.ASCII.GetBytes(password))
            {
                UpdateKeys(keys, b);
            }
            uint crc = Crc32(body);
            byte[] header = new byte[12];
            for (int i = 0; i < 11; i++)
            {
                header[i] = (byte)i;
            }
            header[11] = (byte)(crc >> 24);
            byte[] data = [.. Encrypt(keys, header), .. Encrypt(keys, body)];
            byte[] raw = Encoding.ASCII.GetBytes(name);
            uint offset = (uint)output.Position;
            w.Write(0x04034B50u);
            w.Write((ushort)20);
            w.Write((ushort)1);
            w.Write((ushort)0);
            w.Write(DosTime);
            w.Write(DosDate);
            w.Write(crc);
            w.Write((uint)data.Length);
            w.Write((uint)body.Length);
            w.Write((ushort)raw.Length);
            w.Write((ushort)0);
            w.Write(raw);
            w.Write(data);
            c.Write(0x02014B50u);
            c.Write((ushort)20);
            c.Write((ushort)20);
            c.Write((ushort)1);
            c.Write((ushort)0);
            c.Write(DosTime);
            c.Write(DosDate);
            c.Write(crc);
            c.Write((uint)data.Length);
            c.Write((uint)body.Length);
            c.Write((ushort)raw.Length);
            c.Write((ushort)0);
            c.Write((ushort)0);
            c.Write((ushort)0);
            c.Write((ushort)0);
            c.Write(0u);
            c.Write(offset);
            c.Write(raw);
        }
        c.Flush();
        uint start = (uint)output.Position;
        w.Write(central.ToArray());
        w.Write(0x06054B50u);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)entries.Length);
        w.Write((ushort)entries.Length);
        w.Write((uint)central.Length);
        w.Write(start);
        w.Write((ushort)0);
        w.Flush();
        return output.ToArray();
    }

    /// <summary>
    /// rc.30 (DES-22 §21): a made-up DICOM slice - Explicit VR Little Endian, 16-bit MONOCHROME2, a gradient with a ring
    /// that grows with the slice number - for the patient "SAMPLE^PATIENT" (no real person). Shown only with
    /// ANJAL_VIEWER_DICOM=on.
    /// </summary>
    /// <param name="number">The slice's number in its series.</param>
    /// <param name="z">Its position along the body, in mm.</param>
    /// <param name="series">The series' number.</param>
    /// <param name="description">The series' description.</param>
    /// <param name="size">Rows and columns.</param>
    /// <returns>The file.</returns>
    public static byte[] Dicom(int number, double z, string series, string description, int size = 128)
    {
        string sop = "1.2.826.0.1.3680043.10.999.3." + series + "." + number.ToString(CultureInfo.InvariantCulture);
        byte[] pixels = new byte[size * size * 2];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double r = Math.Sqrt(((x - (size / 2.0)) * (x - (size / 2.0))) + ((y - (size / 2.0)) * (y - (size / 2.0))));
                int v = Math.Abs(r - (size * (0.15 + (0.03 * number)))) < 1.5 ? 4095 : (x + y) * 2047 / size;
                pixels[2 * ((y * size) + x)] = (byte)v;
                pixels[(2 * ((y * size) + x)) + 1] = (byte)(v >> 8);
            }
        }
        using var meta = new MemoryStream();
        DicomElement(meta, 0x0002, 0x0001, "OB", new byte[] { 0, 1 });
        DicomElement(meta, 0x0002, 0x0002, "UI", Uid("1.2.840.10008.5.1.4.1.1.2"));
        DicomElement(meta, 0x0002, 0x0003, "UI", Uid(sop));
        DicomElement(meta, 0x0002, 0x0010, "UI", Uid("1.2.840.10008.1.2.1"));
        using var file = new MemoryStream();
        file.Write(new byte[128]);
        file.Write("DICM"u8);
        DicomElement(file, 0x0002, 0x0000, "UL", BitConverter.GetBytes((uint)meta.Length));
        meta.WriteTo(file);
        DicomElement(file, 0x0008, 0x0016, "UI", Uid("1.2.840.10008.5.1.4.1.1.2"));
        DicomElement(file, 0x0008, 0x0018, "UI", Uid(sop));
        DicomElement(file, 0x0008, 0x0020, "DA", Even("20260919"));
        DicomElement(file, 0x0008, 0x0060, "CS", Even("CT"));
        DicomElement(file, 0x0008, 0x103E, "LO", Even(description));
        DicomElement(file, 0x0010, 0x0010, "PN", Even("SAMPLE^PATIENT"));
        DicomElement(file, 0x0010, 0x0020, "LO", Even("SAMPLE-0001"));
        DicomElement(file, 0x0020, 0x000D, "UI", Uid("1.2.826.0.1.3680043.10.999.1"));
        DicomElement(file, 0x0020, 0x000E, "UI", Uid("1.2.826.0.1.3680043.10.999.2." + series));
        DicomElement(file, 0x0020, 0x0011, "IS", Even(series));
        DicomElement(file, 0x0020, 0x0013, "IS", Even(number.ToString(CultureInfo.InvariantCulture)));
        DicomElement(file, 0x0020, 0x0032, "DS", Even("0\\0\\" + z.ToString("0.0", CultureInfo.InvariantCulture)));
        DicomElement(file, 0x0020, 0x0037, "DS", Even("1\\0\\0\\0\\1\\0"));
        DicomElement(file, 0x0028, 0x0002, "US", BitConverter.GetBytes((ushort)1));
        DicomElement(file, 0x0028, 0x0004, "CS", Even("MONOCHROME2"));
        DicomElement(file, 0x0028, 0x0010, "US", BitConverter.GetBytes((ushort)size));
        DicomElement(file, 0x0028, 0x0011, "US", BitConverter.GetBytes((ushort)size));
        DicomElement(file, 0x0028, 0x0100, "US", BitConverter.GetBytes((ushort)16));
        DicomElement(file, 0x0028, 0x0101, "US", BitConverter.GetBytes((ushort)12));
        DicomElement(file, 0x0028, 0x0102, "US", BitConverter.GetBytes((ushort)11));
        DicomElement(file, 0x0028, 0x0103, "US", BitConverter.GetBytes((ushort)0));
        DicomElement(file, 0x7FE0, 0x0010, "OW", pixels);
        return file.ToArray();
    }

    // One element: its tag, its value representation, its length (four bytes for the long ones) and its value.
    private static void DicomElement(Stream s, ushort group, ushort element, string vr, byte[] value)
    {
        s.Write(BitConverter.GetBytes(group));
        s.Write(BitConverter.GetBytes(element));
        s.Write(Encoding.ASCII.GetBytes(vr));
        if (vr is "OB" or "OW")
        {
            s.Write(new byte[2]);
            s.Write(BitConverter.GetBytes((uint)value.Length));
        }
        else
        {
            s.Write(BitConverter.GetBytes((ushort)value.Length));
        }
        s.Write(value);
    }

    // A UID is padded with a NUL to an even length; text with a space.
    private static byte[] Uid(string uid) => Encoding.ASCII.GetBytes(uid.Length % 2 == 0 ? uid : uid + "\0");

    private static byte[] Even(string text) => Encoding.ASCII.GetBytes(text.Length % 2 == 0 ? text : text + " ");

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] Encrypt(uint[] keys, byte[] plain)
    {
        byte[] result = new byte[plain.Length];
        for (int i = 0; i < plain.Length; i++)
        {
            uint t = (keys[2] | 2) & 0xFFFF;
            result[i] = (byte)(plain[i] ^ (byte)((t * (t ^ 1)) >> 8));
            UpdateKeys(keys, plain[i]);
        }
        return result;
    }

    private static void UpdateKeys(uint[] keys, byte b)
    {
        keys[0] = CrcByte(keys[0], b);
        keys[1] = unchecked(((keys[1] + (keys[0] & 0xFF)) * 134775813) + 1);
        keys[2] = CrcByte(keys[2], (byte)(keys[1] >> 24));
    }

    private static uint CrcByte(uint crc, byte b) => CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = CrcByte(crc, b);
        }
        return ~crc;
    }

    private static uint[] MakeCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}
