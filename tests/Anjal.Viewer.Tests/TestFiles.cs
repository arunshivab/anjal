using System.IO.Compression;
using System.Text;
using Chuvadi.Pdf.Authoring;
using Chuvadi.Sheets.Model;

namespace Anjal.Viewer.Tests;

/// <summary>Files made for the tests: PDFs with the library's own builder, a Word file and archives by hand.</summary>
internal static class TestFiles
{
    /// <summary>A PDF with the given lines of text, one page each.</summary>
    public static byte[] Pdf(params string[] pages) => LockedPdf(null, pages);

    /// <summary>A PDF, locked with a password when one is given.</summary>
    public static byte[] LockedPdf(string? password, params string[] pages)
    {
        PdfDocumentBuilder builder = PdfDocumentBuilder.Create().SetTitle("Test");
        foreach (string text in pages)
        {
            builder.AddPage(PageSize.A4).DrawText(text, 72, 72, "Helvetica", 12, Colors.Black);
        }
        return password is null ? builder.ToByteArray() : builder.ToByteArray(Chuvadi.Pdf.IO.EncryptionOptions.Aes256(password));
    }

    /// <summary>A workbook with a few cells, a merged range, a hidden sheet and, when asked, many rows.</summary>
    public static byte[] Workbook(int rows = 3)
    {
        var wb = new Chuvadi.Sheets.Model.Workbook();
        Sheet s = wb.AddSheet("Results");
        s.Cell(1, 1).Value = "Name";
        s.Cell(1, 2).Value = "Value";
        for (int r = 2; r <= rows; r++)
        {
            s.Cell(r, 1).Value = "Row " + r.ToString(System.Globalization.CultureInfo.InvariantCulture);
            s.Cell(r, 2).Value = r * 1.5;
        }
        s.MergeCells("D1:E1");
        s.Cell(1, 4).Value = "Merged";
        Sheet hidden = wb.AddSheet("Hidden");
        hidden.Cell(1, 1).Value = "x";
        hidden.Visibility = SheetVisibility.Hidden;
        using var ms = new MemoryStream();
        wb.SaveTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// AC-22: a workbook written by hand with Excel's built-in formats - a date in format 14 (the reader's short date),
    /// a date and time in format 22, and a time with no date in format 21 (h:mm:ss) - the cells Sheets 2.0.5 showed as
    /// their raw code and as ########.
    /// </summary>
    /// <returns>The file.</returns>
    public static byte[] DatesAndTimes()
    {
        const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string Pr = "http://schemas.openxmlformats.org/package/2006/relationships";
        string styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"" + S + "\">" +
            "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"4\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "<xf numFmtId=\"22\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "<xf numFmtId=\"21\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>" +
            "</styleSheet>";
        // 46327 is 1 Nov 2026 in the 1900 date system; .75 is 18:00; 0.3958333333333333 is 09:30:00.
        string sheet = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"" + S + "\"><sheetData><row r=\"1\">" +
            "<c r=\"A1\" s=\"1\"><v>46327</v></c><c r=\"B1\" s=\"2\"><v>46327.75</v></c><c r=\"C1\" s=\"3\"><v>0.3958333333333333</v></c>" +
            "</row></sheetData></worksheet>";
        return Zip(
            ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>"),
            ("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"" + Pr + "\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"" + S + "\" xmlns:r=\"" + R + "\"><sheets><sheet name=\"Clinic\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"" + Pr + "\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>"),
            ("xl/styles.xml", styles),
            ("xl/worksheets/sheet1.xml", sheet));
    }

    /// <summary>
    /// A Word file: a heading, a paragraph, two list items, a table and a paragraph after it - written by hand, so
    /// the style ids are exactly the ones Word writes.
    /// </summary>
    public static byte[] Word()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        static string P(string style, string text) =>
            "<w:p>" + (style.Length > 0 ? "<w:pPr><w:pStyle w:val=\"" + style + "\"/></w:pPr>" : string.Empty) + "<w:r><w:t xml:space=\"preserve\">" + text + "</w:t></w:r></w:p>";
        static string Cell(string text) => "<w:tc><w:p><w:r><w:t>" + text + "</w:t></w:r></w:p></w:tc>";
        string body =
            P("Heading1", "Discharge summary") +
            P(string.Empty, "Plain paragraph &lt;script&gt;") +
            P("ListBullet", "First item") +
            P("ListBullet2", "Second item") +
            "<w:tbl><w:tr>" + Cell("Test") + Cell("Result") + "</w:tr><w:tr>" + Cell("Hb") + Cell("13.2") + "</w:tr></w:tbl>" +
            P(string.Empty, "After the table");
        string document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"" + W + "\"><w:body>" + body + "</w:body></w:document>";
        return Zip(
            ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"),
            ("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/document.xml", document));
    }

    /// <summary>
    /// AC-24: a Word file with formatting from every place the library resolves it - a paragraph style that is italic,
    /// a character style based on another that is bold, the run's own underline, strikethrough, raised text and
    /// capitals - plus hidden text and a tracked deletion (neither is shown), and a table with a bold first row and a
    /// cell of two paragraphs. Written by hand, with a styles part, as Word writes it.
    /// </summary>
    /// <returns>The file.</returns>
    public static byte[] FormattedWord()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        static string R(string props, string text) => "<w:r>" + (props.Length > 0 ? "<w:rPr>" + props + "</w:rPr>" : string.Empty) + "<w:t xml:space=\"preserve\">" + text + "</w:t></w:r>";
        static string Cell(string runs) => "<w:tc><w:p>" + runs + "</w:p></w:tc>";
        string styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"" + W + "\">" +
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Quote\"><w:name w:val=\"Quote\"/><w:basedOn w:val=\"Normal\"/><w:rPr><w:i/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/></w:style>" +
            "<w:style w:type=\"character\" w:styleId=\"Emph\"><w:name w:val=\"Emph\"/><w:rPr><w:b/></w:rPr></w:style>" +
            "<w:style w:type=\"character\" w:styleId=\"Drug\"><w:name w:val=\"Drug\"/><w:basedOn w:val=\"Emph\"/></w:style>" +
            "</w:styles>";
        string body =
            "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr>" + R(string.Empty, "Discharge summary") + "</w:p>" +
            "<w:p>" + R(string.Empty, "Give ") + R("<w:rStyle w:val=\"Drug\"/>", "Paracetamol") + R(string.Empty, " twice; ") +
                R("<w:u w:val=\"single\"/>", "not") + R(string.Empty, " with food. ") + R("<w:strike/>", "Old dose") + R(string.Empty, " CO") +
                R("<w:vertAlign w:val=\"subscript\"/>", "2") + R(string.Empty, " and m") + R("<w:vertAlign w:val=\"superscript\"/>", "2") + R(string.Empty, " ") +
                R("<w:caps/>", "urgent") + R("<w:vanish/>", "secret") +
                "<w:del w:id=\"1\" w:author=\"A\" w:date=\"2026-11-01T00:00:00Z\"><w:r><w:delText>removed</w:delText></w:r></w:del>" + "</w:p>" +
            "<w:p><w:pPr><w:pStyle w:val=\"Quote\"/></w:pPr>" + R(string.Empty, "Seen by the duty doctor") + "</w:p>" +
            "<w:p>" + R(string.Empty, "Plain line") + "</w:p>" +
            "<w:tbl><w:tblGrid><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"2000\"/></w:tblGrid>" +
                "<w:tr>" + Cell(R("<w:b/>", "Test")) + Cell(R("<w:b/>", "Result")) + "</w:tr>" +
                "<w:tr>" + Cell(R(string.Empty, "Hb")) + "<w:tc><w:p>" + R(string.Empty, "13.2") + "</w:p><w:p>" + R("<w:i/>", "rechecked") + "</w:p></w:tc></w:tr>" +
            "</w:tbl>" +
            "<w:p>" + R(string.Empty, "After the table") + "</w:p>";
        string document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"" + W + "\"><w:body>" + body + "</w:body></w:document>";
        return Zip(
            ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>"),
            ("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/_rels/document.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>"),
            ("word/styles.xml", styles),
            ("word/document.xml", document));
    }

    /// <summary>An archive of text entries.</summary>
    public static byte[] Zip(params (string Name, string Text)[] entries) =>
        ZipBytes(entries.Select(e => (e.Name, (byte[]?)Encoding.UTF8.GetBytes(e.Text))).ToArray());

    /// <summary>An archive of entries (a null body is a folder).</summary>
    public static byte[] ZipBytes(params (string Name, byte[]? Body)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[]? body) in entries)
            {
                ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.Optimal);
                if (body is not null)
                {
                    using Stream s = e.Open();
                    s.Write(body);
                }
            }
        }
        return ms.ToArray();
    }

    /// <summary>AC-20: an archive locked with a password (WinZip AES-256), written with Chuvadi.Zip.</summary>
    public static byte[] LockedZip(string password, params (string Name, byte[] Body)[] entries) =>
        Chuvadi.Zip.ZipWriter.ToBytes(
            w =>
            {
                foreach ((string name, byte[] body) in entries)
                {
                    w.AddBytes(name, body);
                }
            },
            new Chuvadi.Zip.ZipWriterOptions { Password = password });

    /// <summary>A Windows program's first bytes ("MZ", and the "PE" header it points to).</summary>
    public static byte[] WindowsProgram()
    {
        byte[] b = new byte[256];
        b[0] = (byte)'M';
        b[1] = (byte)'Z';
        b[0x3C] = 0x80;
        b[0x80] = (byte)'P';
        b[0x81] = (byte)'E';
        return b;
    }
}
