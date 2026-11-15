using System.Globalization;
using Anjal.Viewing;
using Chuvadi.Sheets.Model;
using SheetView = Anjal.Viewing.SheetView;

namespace Anjal.Viewer;

/// <summary>
/// rc.29 (DES-22 §3.3, D-361): one sheet of a workbook as a grid - the file's own number formats in the reader's
/// language, merged cells, column widths and hidden columns - with 2,000 rows at most and the sheet's real size
/// stated. Read with the streaming reader and the untrusted limits.
/// </summary>
public static class SheetJob
{
    /// <summary>The most rows drawn a sheet (the owner's number, D-361).</summary>
    public const int MaxRows = 2000;

    /// <summary>The most columns drawn (a sheet can have 16,384; the rest are named in a notice).</summary>
    public const int MaxColumns = 200;

    /// <summary>Reads one sheet.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="password">Its password, or null.</param>
    /// <param name="sheet">The sheet (0-based); a sheet past the last shows the first.</param>
    /// <param name="culture">The reader's language and region ("en-IN"); null for the invariant one.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer.</returns>
    public static JobAnswer Run(byte[] bytes, string? password, int sheet, string? culture, CancellationToken ct)
    {
        try
        {
            XlsxReaderOptions options = XlsxReaderOptions.ForUntrustedInput();
            options.MaxRowsPerSheet = MaxRows;
            options.TreatFirstRowAsHeaders = false;
            options.CancellationToken = ct;
            options.DisplayCulture = Culture(culture);
            using var stream = new MemoryStream(bytes, writable: false);
            using XlsxReader reader = string.IsNullOrEmpty(password) ? XlsxReader.Open(stream, options) : XlsxReader.Open(stream, password, options);
            var view = new SheetView();
            foreach (SheetInfo s in reader.Sheets)
            {
                view.Sheets.Add(new SheetTab { Name = s.Name, Hidden = s.Visibility != SheetVisibility.Visible });
            }
            if (view.Sheets.Count == 0)
            {
                return JobAnswer.Json(ViewJson.Write(view));
            }
            int index = sheet >= 0 && sheet < view.Sheets.Count ? sheet : 0;
            SheetReader sr = reader.Sheet(reader.Sheets[index].Name);
            SheetLayoutInfo layout = sr.ReadLayout();
            (int lastColumn, int lastRow) = UsedEnd(layout.UsedRange);
            int columns = Math.Min(Math.Max(lastColumn, 1), MaxColumns);
            var rows = new List<SheetRow>();
            int shown = 0;
            bool cut = false;
            bool wide = lastColumn > MaxColumns;
            using (IEnumerator<RowReader> e = sr.Rows.GetEnumerator())
            {
                while (true)
                {
                    try
                    {
                        if (!e.MoveNext())
                        {
                            break;
                        }
                    }
                    catch (XlsxLimitExceededException ex) when (ex.LimitName == nameof(XlsxReaderOptions.MaxRowsPerSheet))
                    {
                        // The 2,001st row: the rows read so far are shown, and the real size stated.
                        cut = true;
                        break;
                    }
                    RowReader row = e.Current;
                    shown = row.RowNumber;
                    var cells = new List<SheetCell>();
                    // A file with no <dimension> (Chuvadi's own writer, many others) gives no used range: each
                    // row's own cells say how wide the sheet is.
                    int rowColumns = row.Count;
                    if (rowColumns > MaxColumns)
                    {
                        wide = true;
                    }
                    columns = Math.Max(columns, Math.Min(rowColumns, MaxColumns));
                    for (int c = 0; c < columns; c++)
                    {
                        if (!row.HasCell(c))
                        {
                            continue;
                        }
                        string shownText = row.GetDisplayString(c) ?? string.Empty;
                        string? stored = Stored(row[c]);
                        cells.Add(new SheetCell { C = c + 1, D = shownText, V = stored == shownText ? null : stored });
                    }
                    if (cells.Count > 0)
                    {
                        rows.Add(new SheetRow { N = row.RowNumber, Cells = cells });
                    }
                }
            }
            var result = new SheetView
            {
                Sheets = view.Sheets,
                Index = index,
                Rows = rows,
                Columns = columns,
                Shown = shown,
                Total = Math.Max(lastRow, shown),
                Cut = cut || lastRow > shown,
                Merges = layout.MergeRanges.ToList(),
            };
            for (int c = 1; c <= columns; c++)
            {
                result.Widths.Add(Math.Round(layout.ColumnWidth(c), 2));
                if (layout.IsColumnHidden(c))
                {
                    result.HiddenColumns.Add(c);
                }
            }
            if (reader.HasMacros)
            {
                result.Notices.Add(ViewNotices.Macros);
            }
            if (wide || sr.OutOfGridCells > 0)
            {
                result.Notices.Add(ViewNotices.Partial);
            }
            return JobAnswer.Json(ViewJson.Write(result));
        }
        catch (XlsxPasswordRequiredException)
        {
            return JobAnswer.Status(string.IsNullOrEmpty(password) ? ViewStatus.NeedsPassword : ViewStatus.WrongPassword, null);
        }
        catch (XlsxLimitExceededException ex)
        {
            return JobAnswer.Status(ViewStatus.TooLarge, "xlsx " + ex.LimitName);
        }
        catch (XlsxFormatException ex)
        {
            return JobAnswer.Status(ViewStatus.Damaged, "xlsx " + ex.GetType().Name);
        }
        catch (InvalidDataException ex)
        {
            // The MaxPartBytes limit (DES-22 §5).
            return JobAnswer.Status(ViewStatus.TooLarge, "xlsx " + ex.GetType().Name);
        }
    }

    /// <summary>The last column and row of a used range ("A1:K250" gives 11 and 250); (0, 0) when there is none.</summary>
    /// <param name="usedRange">The range.</param>
    /// <returns>The last column and row.</returns>
    public static (int Column, int Row) UsedEnd(string? usedRange)
    {
        if (string.IsNullOrEmpty(usedRange))
        {
            return (0, 0);
        }
        string end = usedRange.Contains(':', StringComparison.Ordinal) ? usedRange[(usedRange.IndexOf(':', StringComparison.Ordinal) + 1)..] : usedRange;
        int column = 0, i = 0;
        while (i < end.Length && char.IsAsciiLetter(end[i]))
        {
            column = (column * 26) + (char.ToUpperInvariant(end[i]) - 'A' + 1);
            i++;
            if (column > 16384)
            {
                return (16384, 0);
            }
        }
        int row = int.TryParse(end.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out int r) ? r : 0;
        return (column, row);
    }

    private static readonly DateTime ExcelDayZero = new(1899, 12, 30);

    private static string? Stored(object? value) => value switch
    {
        null => null,
        string s => s,
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        // AC-22: a time with no date is Excel's day zero (30 Dec 1899) plus the time; it is stored as the time alone,
        // as Excel's own formula bar shows it.
        DateTime t when t.Date == ExcelDayZero => t.ToString("H:mm:ss", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static CultureInfo Culture(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return CultureInfo.InvariantCulture;
        }
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
