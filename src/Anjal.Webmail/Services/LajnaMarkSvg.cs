namespace Anjal.Webmail.Services;

/// <summary>
/// Lajna's mark (design review [8], final, owner 11 Oct 2026): Anjal's tile with the flap turned up -
/// the envelope opened, a Λ - and three fading dots.
/// </summary>
public static class LajnaMarkSvg
{
    /// <summary>The mark at 20px, for buttons.</summary>
    public const string Small = Body20;

    /// <summary>The mark at 32px, for the panel's head.</summary>
    public const string Large = "<svg class=\"lajnamark\" viewBox=\"0 0 148 148\" width=\"32\" height=\"32\" aria-hidden=\"true\">" + Shapes + "</svg>";

    private const string Body20 = "<svg class=\"lajnamark\" viewBox=\"0 0 148 148\" width=\"20\" height=\"20\" aria-hidden=\"true\">" + Shapes + "</svg>";

    private const string Shapes =
        "<rect width=\"148\" height=\"148\" rx=\"34\" class=\"mt-tile\"></rect>"
        + "<path d=\"M18 116 L74 68 L130 116\" class=\"mt-flap\"></path>"
        + "<circle cx=\"50\" cy=\"38\" r=\"8\" class=\"lj-dot\"></circle>"
        + "<circle cx=\"74\" cy=\"38\" r=\"8\" class=\"lj-dot lj-d2\"></circle>"
        + "<circle cx=\"98\" cy=\"38\" r=\"8\" class=\"lj-dot lj-d3\"></circle>";
}
