using Anjal.Viewing;
using Chuvadi.Docs.Model;
using Chuvadi.Pdf.Images;

namespace Anjal.Viewer;

/// <summary>
/// rc.29 (DES-22 §3.2, AC-06, AC-24): a Word document as readable text - headings and list levels from the style
/// ids Word writes, tables, its pictures re-drawn as PNG, and each run's bold, italic, underline, strikethrough,
/// raised or lowered text and capitals as Chuvadi.Docs 2.2.0 resolves them (<c>DocxReader.FormattedParagraphs</c>:
/// defaults, styles, then the run's own - Anjal resolves no style itself). Read with the untrusted limits;
/// <c>Document.Load</c> is never used. No fonts, sizes, colours or links.
/// </summary>
public static class WordJob
{
    /// <summary>At most this many pictures are re-drawn and shown.</summary>
    public const int MaxPictures = 200;

    /// <summary>At most this many bytes of pictures are given back.</summary>
    public const long MaxPictureBytes = 32L * 1024 * 1024;

    /// <summary>Reads a Word document into blocks in the document's order, with its pictures.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="password">Its password, or null.</param>
    /// <param name="ct">Cancellation (held for the reader's whole life).</param>
    /// <returns>The answer, its pictures as files.</returns>
    public static JobAnswer Run(byte[] bytes, string? password, CancellationToken ct)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using DocxReader reader = DocxReader.Open(stream, string.IsNullOrEmpty(password) ? null : password, DocxReaderOptions.ForUntrustedInput(), ct);
            var view = new WordView();
            List<WordBlock> blocks = Blocks(reader.FormattedParagraphs(ct), ct);
            var files = new List<byte[]>();
            Place(reader.Images(ct), blocks, files, ct);
            view.Blocks.AddRange(blocks);
            DocxActiveContent active = reader.GetActiveContent(ct);
            if (reader.HasMacros || active.HasMacros)
            {
                view.Notices.Add(ViewNotices.Macros);
            }
            if (active.HasActiveXControls || active.HasEmbeddedObjects || active.HasAltChunks || active.HasDdeFields || active.HasRemoteTemplate || active.HasExternalResources)
            {
                view.Notices.Add(ViewNotices.Active);
            }
            foreach (DocxSkippedPart part in reader.SkippedParts)
            {
                view.Skipped.Add(part.PartName);
            }
            if (view.Skipped.Count > 0)
            {
                view.Notices.Add(ViewNotices.Partial);
            }
            return JobAnswer.Json(ViewJson.Write(view), files);
        }
        catch (DocxPasswordRequiredException)
        {
            return JobAnswer.Status(string.IsNullOrEmpty(password) ? ViewStatus.NeedsPassword : ViewStatus.WrongPassword, null);
        }
        catch (DocxSecurityLimitException ex)
        {
            return JobAnswer.Status(ViewStatus.TooLarge, "docx " + ex.GetType().Name);
        }
        catch (DocxLimitExceededException ex)
        {
            return JobAnswer.Status(ViewStatus.TooLarge, "docx " + ex.GetType().Name);
        }
        catch (DocxFormatException ex)
        {
            return JobAnswer.Status(ViewStatus.Damaged, "docx " + ex.GetType().Name);
        }
        catch (InvalidDataException ex)
        {
            return JobAnswer.Status(ViewStatus.Damaged, "docx " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// AC-24: the blocks in the document's order. The library gives the body in order with the paragraphs of table
    /// cells in place (each with its cell), then the headers and footers; a table becomes one block where its first
    /// paragraph is, and a table inside a cell becomes its own block after the cell it is in.
    /// </summary>
    /// <param name="paragraphs">The paragraphs with their formatting.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The blocks.</returns>
    public static List<WordBlock> Blocks(IEnumerable<DocxFormattedParagraph> paragraphs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        var blocks = new List<WordBlock>();
        // The tables open now; dropped at the next paragraph outside every table, so only one top-level table is held.
        var tables = new Dictionary<object, TableBuilder>(ReferenceEqualityComparer.Instance);
        foreach (DocxFormattedParagraph fp in paragraphs)
        {
            ct.ThrowIfCancellationRequested();
            List<WordRun> runs = Runs(fp.Paragraph.Runs, fp.RunFormats);
            string text = fp.Text ?? string.Empty;
            if (fp.Cell is TableCellRef cell)
            {
                if (!tables.TryGetValue(cell.Table, out TableBuilder? table))
                {
                    table = new TableBuilder();
                    tables[cell.Table] = table;
                    blocks.Add(table.Block);
                }
                table.Add(cell.Row, cell.Column, text, runs);
            }
            else
            {
                tables.Clear();
                blocks.Add(Block(fp.Paragraph.StyleId, text, runs));
            }
        }
        return blocks;
    }

    /// <summary>
    /// A paragraph's block: a heading or a list item by its style id's name only (AC-06), else a paragraph; with its
    /// runs when any of them is formatted (AC-24).
    /// </summary>
    /// <param name="styleId">The paragraph's style id, or null.</param>
    /// <param name="text">Its text.</param>
    /// <param name="runs">Its runs; null or plain for plain text.</param>
    /// <returns>The block.</returns>
    public static WordBlock Block(string? styleId, string text, List<WordRun>? runs = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<WordRun>? r = Formatted(runs);
        string id = styleId ?? string.Empty;
        if (id.Equals("Title", StringComparison.OrdinalIgnoreCase))
        {
            return new WordBlock { K = "h1", T = text, R = r };
        }
        if (id.Equals("Subtitle", StringComparison.OrdinalIgnoreCase))
        {
            return new WordBlock { K = "h2", T = text, R = r };
        }
        if (id.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && id.Length == 8 && id[7] is >= '1' and <= '6')
        {
            return new WordBlock { K = "h" + id[7], T = text, R = r };
        }
        if (id.Equals("ListParagraph", StringComparison.OrdinalIgnoreCase))
        {
            return new WordBlock { K = "li", L = 1, T = text, R = r };
        }
        foreach (string list in ListStyles)
        {
            if (id.StartsWith(list, StringComparison.OrdinalIgnoreCase))
            {
                string rest = id[list.Length..];
                int level = rest.Length == 0 ? 1 : rest.Length == 1 && rest[0] is >= '2' and <= '5' ? rest[0] - '0' : 0;
                if (level > 0)
                {
                    return new WordBlock { K = "li", L = level, T = text, R = r };
                }
            }
        }
        return new WordBlock { K = "p", T = text, R = r };
    }

    /// <summary>
    /// AC-24: a paragraph's runs with the formatting the library resolved for each (<paramref name="formats"/>[i] is
    /// <paramref name="runs"/>[i]'s). Deleted (tracked) and hidden text, markers, pictures, text boxes and symbol
    /// characters are left out; neighbouring runs that look the same are joined.
    /// </summary>
    /// <param name="runs">The runs.</param>
    /// <param name="formats">Their effective formatting.</param>
    /// <returns>The runs to show.</returns>
    public static List<WordRun> Runs(IReadOnlyList<Run> runs, IReadOnlyList<TextFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(formats);
        var shown = new List<WordRun>(runs.Count);
        for (int i = 0; i < runs.Count; i++)
        {
            Run run = runs[i];
            if (run.IsDeleted || run.IsMarker || run.Image is not null || run.TextBox is not null || run.Symbol is not null)
            {
                continue;
            }
            string text = run.TextContent ?? string.Empty;
            TextFormat f = i < formats.Count ? formats[i] : run.Format;
            if (text.Length == 0 || f.Hidden == true)
            {
                continue;
            }
            var w = new WordRun
            {
                T = text,
                B = f.Bold,
                I = f.Italic,
                U = f.EffectiveUnderline is UnderlineStyle u && u != UnderlineStyle.None,
                S = f.Strikethrough || f.DoubleStrikethrough == true,
                P = f.VerticalPosition switch
                {
                    VerticalTextPosition.Superscript => "sup",
                    VerticalTextPosition.Subscript => "sub",
                    _ => null,
                },
                C = f.Caps == true ? "caps" : f.SmallCaps == true ? "small" : null,
            };
            if (shown.Count > 0 && shown[^1].SameLook(w) && w.T != "\n" && shown[^1].T != "\n")
            {
                shown[^1] = new WordRun { T = shown[^1].T + w.T, B = w.B, I = w.I, U = w.U, S = w.S, P = w.P, C = w.C };
            }
            else
            {
                shown.Add(w);
            }
        }
        return shown;
    }

    private static readonly string[] ListStyles = { "ListBullet", "ListNumber" };

    // The runs to send: null when every run is plain, so a plain paragraph costs no more than before.
    private static List<WordRun>? Formatted(List<WordRun>? runs) => runs is null || runs.TrueForAll(r => r.IsPlain() && r.T != "\n") ? null : runs;

    /// <summary>One table being built from its cells' paragraphs.</summary>
    private sealed class TableBuilder
    {
        private readonly List<List<string>> rows = new();
        private readonly List<List<List<WordRun>?>> runRows = new();
        private readonly List<Dictionary<int, int>> columns = new();

        public TableBuilder() => this.Block = new WordBlock { K = "table", Rows = this.rows, RunRows = this.runRows };

        public WordBlock Block { get; }

        // A cell's second paragraph goes on a new line in the same cell.
        public void Add(int row, int column, string text, List<WordRun> runs)
        {
            while (this.rows.Count <= row)
            {
                this.rows.Add(new List<string>());
                this.runRows.Add(new List<List<WordRun>?>());
                this.columns.Add(new Dictionary<int, int>());
            }
            if (!this.columns[row].TryGetValue(column, out int at))
            {
                at = this.rows[row].Count;
                this.columns[row][column] = at;
                this.rows[row].Add(text);
                this.runRows[row].Add(Formatted(runs));
                return;
            }
            this.rows[row][at] += "\n" + text;
            List<WordRun> joined = this.runRows[row][at] ?? new List<WordRun> { new() { T = this.rows[row][at][..^(text.Length + 1)] } };
            joined.Add(new WordRun { T = "\n" });
            joined.AddRange(runs.Count > 0 || text.Length == 0 ? runs : new List<WordRun> { new() { T = text } });
            this.runRows[row][at] = joined;
        }
    }

    // Each picture after the paragraph whose text it is anchored to (the first such paragraph not yet given one),
    // or after the text; only PNG, JPEG and BMP are re-drawn (as PNG), others are named.
    private static void Place(IReadOnlyList<ImageInfo> images, List<WordBlock> blocks, List<byte[]> files, CancellationToken ct)
    {
        long total = 0;
        int shown = 0;
        var after = new Dictionary<int, List<WordBlock>>();
        var tail = new List<WordBlock>();
        foreach (ImageInfo image in images)
        {
            ct.ThrowIfCancellationRequested();
            int index = -1;
            if (shown < MaxPictures && Redraw(image.Bytes) is byte[] png && total + png.Length <= MaxPictureBytes)
            {
                index = files.Count;
                files.Add(png);
                total += png.Length;
                shown++;
            }
            var block = new WordBlock { K = "img", Img = index, Alt = image.AltText };
            int at = string.IsNullOrEmpty(image.AnchorParagraphText) ? -1 : blocks.FindIndex(b => b.K != "table" && b.K != "img" && b.T == image.AnchorParagraphText);
            if (at >= 0)
            {
                if (!after.TryGetValue(at, out List<WordBlock>? list))
                {
                    after[at] = list = new List<WordBlock>();
                }
                list.Add(block);
            }
            else
            {
                tail.Add(block);
            }
        }
        foreach (int at in after.Keys.OrderByDescending(k => k))
        {
            blocks.InsertRange(at + 1, after[at]);
        }
        blocks.AddRange(tail);
    }

    // A picture decoded and written again as PNG, so nothing of the file's own encoding reaches the browser.
    private static byte[]? Redraw(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4)
        {
            return null;
        }
        try
        {
            ImageFrame? frame = FileKinds.PictureType(bytes) switch
            {
                "image/png" => PngDecoder.Decode(bytes),
                "image/jpeg" => JpegDecoder.Decode(bytes),
                "image/bmp" => BmpDecoder.Decode(bytes),
                _ => null,
            };
            if (frame is null || frame.Width <= 0 || frame.Height <= 0 || (long)frame.Width * frame.Height > 16_000_000)
            {
                return null;
            }
            using var output = new MemoryStream();
            PngEncoder.Encode(frame, output, includeAlpha: true);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is Chuvadi.Pdf.Primitives.PdfException or InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }
}
