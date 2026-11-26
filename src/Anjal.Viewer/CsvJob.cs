using System.Text;
using Anjal.Viewing;

namespace Anjal.Viewer;

/// <summary>
/// rc.31 (D4, the coordinator, 25 Nov 2026): a CSV or TSV file as a plain table, read in the viewer's child like every
/// other file. Nothing in it is ever evaluated or run: every cell is shown as the text it holds, so a cell starting
/// with =, +, - or @ is shown as written, never as a formula. Caps: 10 MB read, 5,000 rows, 200 columns and 1,000
/// characters shown a cell; past any cap the first part is shown with a plain note. The answer is a
/// <see cref="SheetView"/>, so the table is drawn like a workbook's sheet.
/// </summary>
public static class CsvJob
{
    /// <summary>The most bytes read.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>The most rows shown.</summary>
    public const int MaxRows = 5000;

    /// <summary>The most columns shown.</summary>
    public const int MaxColumns = 200;

    /// <summary>The most characters shown in one cell.</summary>
    public const int MaxCellChars = 1000;

    /// <summary>Reads a CSV or TSV file into a table.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="name">Its name (a .tsv is split at tabs).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer.</returns>
    public static JobAnswer Run(byte[] bytes, string? name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return JobAnswer.Json(ViewJson.Write(Read(bytes, name, ct)));
    }

    /// <summary>Reads a CSV or TSV file into a table (the work of <see cref="Run"/>, for tests).</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="name">Its name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The table.</returns>
    public static SheetView Read(byte[] bytes, string? name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        bool sizeCut = bytes.Length > MaxBytes;
        string text = Decode(sizeCut ? bytes.AsSpan(0, MaxBytes) : bytes);
        char separator = Separator(text, name);
        var rows = new List<SheetRow>();
        var cells = new List<SheetCell>();
        var cell = new StringBuilder();
        int columns = 0;
        int column = 0;
        int n = 0;
        bool quoted = false;
        bool wide = false;
        bool longCell = false;
        bool rowCut = false;
        int i = 0;
        while (i < text.Length)
        {
            if ((i & 0xFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    Add(cell, '"', ref longCell);
                    i += 2;
                }
                else if (c == '"')
                {
                    quoted = false;
                    i++;
                }
                else
                {
                    Add(cell, c, ref longCell);
                    i++;
                }
                continue;
            }
            if (c == '"' && cell.Length == 0)
            {
                quoted = true;
                i++;
                continue;
            }
            if (c == separator)
            {
                EndCell(cells, cell, ref column, ref wide);
                i++;
                continue;
            }
            if (c == '\r' || c == '\n')
            {
                EndCell(cells, cell, ref column, ref wide);
                i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                if (!EndRow(rows, cells, ref column, ref columns, ref n))
                {
                    rowCut = true;
                    break;
                }
                continue;
            }
            Add(cell, c, ref longCell);
            i++;
        }
        // The last line, when the file does not end with a line break.
        if (!rowCut && (cell.Length > 0 || column > 0 || quoted))
        {
            EndCell(cells, cell, ref column, ref wide);
            rowCut = !EndRow(rows, cells, ref column, ref columns, ref n);
        }
        var view = new SheetView
        {
            Rows = rows,
            Columns = Math.Max(columns, 1),
            Shown = n,
            Total = n,
            // How much more there is is not counted (the file is not read past its caps): the note says what was cut.
            Cut = false,
        };
        for (int c = 1; c <= view.Columns; c++)
        {
            view.Widths.Add(0);
        }
        if (wide || longCell || sizeCut || rowCut)
        {
            view.Notices.Add(ViewNotices.TableCut);
        }
        return view;
    }

    private static void EndCell(List<SheetCell> cells, StringBuilder cell, ref int column, ref bool wide)
    {
        if (column < MaxColumns)
        {
            if (cell.Length > 0)
            {
                cells.Add(new SheetCell { C = column + 1, D = cell.ToString() });
            }
        }
        else if (cell.Length > 0)
        {
            wide = true;
        }
        cell.Clear();
        column++;
    }

    // Ends a row; false when it would be one past the row cap (it is then not shown).
    private static bool EndRow(List<SheetRow> rows, List<SheetCell> cells, ref int column, ref int columns, ref int n)
    {
        if (n >= MaxRows)
        {
            return false;
        }
        n++;
        columns = Math.Max(columns, Math.Min(column, MaxColumns));
        if (cells.Count > 0)
        {
            rows.Add(new SheetRow { N = n, Cells = new List<SheetCell>(cells) });
        }
        cells.Clear();
        column = 0;
        return true;
    }

    private static void Add(StringBuilder cell, char c, ref bool longCell)
    {
        if (cell.Length < MaxCellChars)
        {
            cell.Append(c);
        }
        else
        {
            longCell = true;
        }
    }

    // A .tsv splits at tabs; otherwise the first line decides: a comma, or a semicolon or tab when it has more of them.
    private static char Separator(string text, string? name)
    {
        if (name is not null && name.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase))
        {
            return '\t';
        }
        int lineEnd = text.IndexOfAny(['\r', '\n']);
        ReadOnlySpan<char> first = lineEnd < 0 ? text.AsSpan() : text.AsSpan(0, lineEnd);
        int commas = first.Count(',');
        int semis = first.Count(';');
        int tabs = first.Count('\t');
        if (tabs > commas && tabs >= semis)
        {
            return '\t';
        }
        return semis > commas ? ';' : ',';
    }

    /// <summary>UTF-8 (with or without its mark) when the bytes are valid UTF-8; otherwise Windows-1252.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The text.</returns>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> body = bytes.StartsWith("﻿"u8) ? bytes[3..] : bytes;
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(body);
        }
        catch (DecoderFallbackException)
        {
            // Cut in the middle of a character by the size cap: drop up to three bytes at the end and try again.
            for (int drop = 1; drop <= 3 && drop < body.Length; drop++)
            {
                try
                {
                    return new UTF8Encoding(false, true).GetString(body[..^drop]);
                }
                catch (DecoderFallbackException)
                {
                }
            }
            return Windows1252(body);
        }
    }

    // Windows-1252: Latin-1, except 0x80 to 0x9F, which hold the curly quotes, dashes, the euro sign and so on.
    private static readonly char[] High =
    [
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    ];

    private static string Windows1252(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            chars[i] = b is >= 0x80 and <= 0x9F ? High[b - 0x80] : (char)b;
        }
        return new string(chars);
    }
}
