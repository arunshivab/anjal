using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

public class Rc11ClockClaimTests
{
    private static readonly System.DateTimeOffset At = new(2026, 10, 2, 4, 12, 0, System.TimeSpan.Zero);

    [Fact]
    public void ACookieFromBeforeRc11_ShowsIndiaTime()
    {
        var old = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity("AnjalWebmail"));
        Assert.Equal("Fri 2 Oct 2026, 09:42 IST", WebmailAuthService.ClockOf(old).Full(At));
        Assert.Equal("Fri 2 Oct 2026, 09:42 IST", WebmailAuthService.ClockOf(null).Full(At));
    }

    [Fact]
    public async System.Threading.Tasks.Task SignIn_PutsThePersonsOwnZoneAndFormatInTheCookie()
    {
        var store = new InMemoryMessageStore();
        TenantRow tenant = await store.UpsertTenantAsync(new TenantRow { Slug = "example" });
        MailboxRow m = await store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "meera", Domain = "example.test", PasswordPbkdf2 = Anjal.Smtp.Pbkdf2Hasher.Hash("a-long-enough-phrase-here") });
        Assert.True(await store.SetMailboxPreferencesAsync(m.Id, new MailboxPreferences { TimeZone = "Europe/London", DateFormat = "yyyy-mm-dd" }));

        System.Security.Claims.ClaimsPrincipal? user = await new WebmailAuthService(store).AuthenticateAsync("meera@example.test", "a-long-enough-phrase-here");

        Assert.NotNull(user);
        Assert.Equal("Fri 2026-10-02, 05:12 BST", WebmailAuthService.ClockOf(user).Full(At));
    }

    [Fact]
    public void WithPreferences_ReplacesZoneAndFormat_AndKeepsEverythingElse()
    {
        var identity = new System.Security.Claims.ClaimsIdentity("AnjalWebmail");
        System.Guid id = System.Guid.NewGuid();
        identity.AddClaim(new System.Security.Claims.Claim(WebmailAuthService.MailboxIdClaim, id.ToString()));
        identity.AddClaim(new System.Security.Claims.Claim(WebmailAuthService.ThemeClaim, "plum-dark"));
        identity.AddClaim(new System.Security.Claims.Claim(WebmailAuthService.TimeZoneClaim, "Asia/Kolkata"));

        System.Security.Claims.ClaimsPrincipal changed = WebmailAuthService.WithPreferences(new System.Security.Claims.ClaimsPrincipal(identity), new MailboxPreferences { TimeZone = "Asia/Dubai", DateFormat = "dd/mm/yyyy" });

        Assert.Equal("Fri 02/10/2026, 08:12 GST", WebmailAuthService.ClockOf(changed).Full(At));
        Assert.Equal(id, WebmailAuthService.MailboxIdOf(changed));
        Assert.Equal("plum-dark", WebmailAuthService.ThemeOf(changed));
        Assert.Single(changed.Claims, c => c.Type == WebmailAuthService.TimeZoneClaim);
    }
}
