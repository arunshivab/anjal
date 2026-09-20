namespace Anjal.Webmail.Services;

/// <summary>
/// The theme for the page being rendered. Scoped per request and filled
/// from the signed-in mailbox's stored preference, so a user's theme
/// follows them to any browser rather than living in local storage on
/// one machine. Anonymous pages get the default.
/// </summary>
public sealed class ThemeState
{
    /// <summary>The theme name to put on the root element.</summary>
    public string Current { get; set; } = Anjal.Store.MailboxRow.DefaultTheme;
}
