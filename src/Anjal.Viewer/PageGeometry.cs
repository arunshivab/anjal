using Chuvadi.Pdf.Documents;

namespace Anjal.Viewer;

/// <summary>
/// rc.29 (DES-22 §3.1, §7.4): between a page as the PDF holds it (user space: bottom-left origin, y up, unrotated)
/// and the page as the person sees it (its crop box, turned by its <c>/Rotate</c>, top-left origin, y down), in
/// points. The library draws a page turned and cropped (<c>ApplyPageRotation</c>); its text, search boxes and
/// redaction boxes are in user space - so every position shown over a page picture goes through here.
/// </summary>
public readonly struct PageGeometry
{
    private readonly double x1;
    private readonly double y1;
    private readonly double w;
    private readonly double h;

    /// <summary>
    /// Takes the box the renderers show (chuvadi-pdf 3.34.0 <c>GetDisplayBox</c>: the CropBox intersected with the
    /// MediaBox, normalised) and the page's turn, taken at its nearest lower multiple of 90 as the renderers take it -
    /// so the positions shown over a page match the library's own display space, which the editor writes through.
    /// </summary>
    /// <param name="page">The page.</param>
    public PageGeometry(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        PdfRectangle box = page.GetDisplayBox(PdfDisplayBox.CropBox);
        this.x1 = box.X1;
        this.y1 = box.Y1;
        this.w = box.Width;
        this.h = box.Height;
        this.Rotate = (((page.Rotate % 360) + 360) % 360) / 90 * 90;
    }

    /// <summary>Takes a crop box and a turn directly (for tests).</summary>
    /// <param name="x1">The box's left.</param>
    /// <param name="y1">The box's bottom.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="rotate">The turn: 0, 90, 180 or 270, clockwise.</param>
    public PageGeometry(double x1, double y1, double width, double height, int rotate)
    {
        this.x1 = x1;
        this.y1 = y1;
        this.w = width;
        this.h = height;
        this.Rotate = ((rotate % 360) + 360) % 360;
    }

    /// <summary>The page's turn, clockwise.</summary>
    public int Rotate { get; }

    /// <summary>The width as shown.</summary>
    public double ShownWidth => this.Rotate is 90 or 270 ? this.h : this.w;

    /// <summary>The height as shown.</summary>
    public double ShownHeight => this.Rotate is 90 or 270 ? this.w : this.h;

    /// <summary>A point in user space as shown (from the top-left).</summary>
    /// <param name="ux">User-space x.</param>
    /// <param name="uy">User-space y.</param>
    /// <returns>The point as shown.</returns>
    public (double X, double Y) ToShown(double ux, double uy)
    {
        double px = ux - this.x1;
        double py = this.y1 + this.h - uy;
        return this.Rotate switch
        {
            90 => (this.h - py, px),
            180 => (this.w - px, this.h - py),
            270 => (py, this.w - px),
            _ => (px, py),
        };
    }

    /// <summary>A point as shown (from the top-left) in user space - the way back (§7.4).</summary>
    /// <param name="sx">Shown x.</param>
    /// <param name="sy">Shown y.</param>
    /// <returns>The user-space point.</returns>
    public (double X, double Y) ToUser(double sx, double sy)
    {
        (double px, double py) = this.Rotate switch
        {
            90 => (sy, this.h - sx),
            180 => (this.w - sx, this.h - sy),
            270 => (this.w - sy, sx),
            _ => (sx, sy),
        };
        return (px + this.x1, this.y1 + this.h - py);
    }

    /// <summary>A user-space rectangle (left, bottom, width, height) as a box shown from the top-left.</summary>
    /// <param name="x">Its left.</param>
    /// <param name="y">Its bottom.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The shown box.</returns>
    public Anjal.Viewing.Box ToShownBox(double x, double y, double width, double height)
    {
        (double ax, double ay) = this.ToShown(x, y);
        (double bx, double by) = this.ToShown(x + width, y + height);
        return new Anjal.Viewing.Box
        {
            X = Math.Min(ax, bx),
            Y = Math.Min(ay, by),
            W = Math.Abs(bx - ax),
            H = Math.Abs(by - ay),
        };
    }

    /// <summary>A direction in user space (degrees, counter-clockwise) as a turn on the screen (degrees, clockwise).</summary>
    /// <param name="userAngle">The user-space angle.</param>
    /// <returns>The screen turn.</returns>
    public double ToShownAngle(double userAngle)
    {
        double a = (this.Rotate - userAngle) % 360;
        return a < 0 ? a + 360 : a;
    }
}
