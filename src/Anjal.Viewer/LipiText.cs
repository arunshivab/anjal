using System.Collections.Concurrent;
using Chuvadi.Fonts;
using Chuvadi.Pdf.Text.Shaping;

namespace Anjal.Viewer;

/// <summary>
/// rc.29 part 2 (DES-22 §7.4, §7.7): how wide words are in LiPi Sans, the face the builder draws "Lipi" text with,
/// measured by shaping each script's run with that script's LiPi face - so a text box wraps, and a watermark or a
/// page number is centred, by the words' real width in every script (the Standard 14 metrics know only Latin).
/// </summary>
public static class LipiText
{
    private static readonly ConcurrentDictionary<LipiScript, byte[]?> Faces = new();

    /// <summary>The width of one line of text, in points.</summary>
    /// <param name="text">The line.</param>
    /// <param name="size">The size of its letters, in points.</param>
    /// <returns>Its width.</returns>
    public static double Width(string text, double size)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return 0;
        }
        double units = 0;
        foreach (ScriptRun run in ScriptClassifier.Split(text))
        {
            if (run.Text.Length == 0)
            {
                continue;
            }
            byte[]? face = Faces.GetOrAdd(run.Script, Face);
            if (face is null)
            {
                // A script with no LiPi face: about half an em a character, as the builder's own fallback is.
                units += run.Text.Length * 500;
                continue;
            }
            foreach (ShapedGlyph g in TextShaper.Shape(face, run.Text, run.Script))
            {
                units += g.XAdvance;
            }
        }
        return units * size / 1000;
    }

    /// <summary>The lines a text fills in a box of a width: the person's own line breaks kept, long lines wrapped at spaces.</summary>
    /// <param name="text">The text.</param>
    /// <param name="width">The box's width, in points.</param>
    /// <param name="size">The size of its letters, in points.</param>
    /// <returns>The lines.</returns>
    public static List<string> Wrap(string text, double width, double size)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = string.Empty;
            foreach (string word in paragraph.Split(' '))
            {
                string tried = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Width(tried, size) > width)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = tried;
                }
            }
            lines.Add(line);
        }
        return lines;
    }

    // The regular face of a script, from the font pack the builder embeds from.
    private static byte[]? Face(LipiScript script)
    {
        if (!Enum.TryParse(script.ToString(), out FontScript wanted))
        {
            return null;
        }
        FontPackFile? file = FontPack.Files
            .Where(f => f.Script == wanted && !f.Italic)
            .OrderBy(f => Math.Abs(f.Weight - 400))
            .FirstOrDefault();
        return file is null ? null : FontPack.ReadAllBytes(file.FileName);
    }
}
