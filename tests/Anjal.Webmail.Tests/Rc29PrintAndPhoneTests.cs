namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.29 (owner, 11 Nov): what the first look at rc.28 on a phone found - the recovery sheet printed with a
/// black frame of ink round it in the dark theme, the Lajna button covered Forward on an open mail, and
/// "Never for this sender" went off the left edge of the new-sender dialog.
/// </summary>
public sealed class Rc29PrintAndPhoneTests
{
    [Fact]
    public void Paper_IsNeverDark_InAnyTheme()
    {
        string css = Css();
        // Chrome paints the paper's margins from color-scheme, not from the page's background.
        Assert.Contains("@media print { :root, html[data-theme] { color-scheme: light !important; } }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void OnAnOpenMail_TheLajnaButton_RisesAboveTheReplyRow()
    {
        string css = Css();
        int phone = css.IndexOf("  .lajnabtn span { display: none; }", StringComparison.Ordinal);
        int lift = css.IndexOf("  body:has(.letterend) .lajnabtn { bottom: 136px; }", StringComparison.Ordinal);
        Assert.True(phone > 0, "the phone rule for the Lajna button is missing");
        Assert.True(lift > phone, "the lift must sit in the same phone rule, after it");
    }

    [Fact]
    public void TheNewSenderDialog_WrapsItsButtons()
    {
        Assert.Contains("dialog.nsdialog .modal-actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }", Css(), StringComparison.Ordinal);
    }

    private static string Css()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        string root = dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        return File.ReadAllText(Path.Combine(root, "src", "Anjal.Webmail", "wwwroot", "app.css"));
    }
}
