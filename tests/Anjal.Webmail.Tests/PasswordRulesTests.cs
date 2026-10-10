using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// The password rules (owner, 10 Oct 2026, P1-P8): 15 characters alone, 12 with two-step, or the
/// organisation's own length; no forced periodic change; earlier passwords refused only when the
/// organisation asks; ten wrong passwords in a row pause signing in with the password for 30
/// minutes; a password shorter than the rules now ask is replaced at the next sign-in.
/// </summary>
public sealed class PasswordRulesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-pw-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MailboxService svc;
    private readonly TenantRow tenant;
    private readonly MailboxRow ravi;

    public PasswordRulesTests()
    {
        this.svc = new MailboxService(this.store, this.store, new MaildirStore(this.root, "test"), "anjal.localhost");
        this.tenant = this.store.UpsertTenantAsync(new TenantRow { Slug = "clinic", DisplayName = "Clinic" }).GetAwaiter().GetResult();
        this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "clinic.example" }).GetAwaiter().GetResult();
        this.ravi = this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.tenant.Id, LocalPart = "ravi", Domain = "clinic.example", DisplayName = "Ravi", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public async Task TheLength_Is15Alone_12WhenTwoStepIsRequired_OrTheOrganisations()
    {
        Assert.Equal(15, await this.svc.PasswordLengthForAsync(this.ravi, null));
        Assert.Contains("at least 15", await this.svc.ChangePasswordAsync(this.ravi.Id, "correct horse battery", "teal lantern", null), StringComparison.Ordinal);

        Assert.Null(await this.svc.SaveSignInPolicyAsync(this.tenant.Id, new SignInPolicy { TwoStep = "everyone" }));
        Assert.Equal(12, await this.svc.PasswordLengthForAsync(this.ravi, null));
        Assert.Null(await this.svc.ChangePasswordAsync(this.ravi.Id, "correct horse battery", "teal lantern", null));

        Assert.Null(await this.svc.SaveSignInPolicyAsync(this.tenant.Id, new SignInPolicy { MinLength = 20, TwoStep = "everyone" }));
        Assert.Equal(20, await this.svc.PasswordLengthForAsync(this.ravi, null));
        Assert.NotNull(await this.svc.SaveSignInPolicyAsync(this.tenant.Id, new SignInPolicy { MinLength = 8 }));
    }

    [Fact]
    public async Task TheResetPage_AsksTheSameOfEveryone()
    {
        Assert.Equal(15, MailboxService.ResetLength(new SignInPolicy()));
        Assert.Equal(15, MailboxService.ResetLength(new SignInPolicy { TwoStep = "admins" }));
        Assert.Equal(12, MailboxService.ResetLength(new SignInPolicy { TwoStep = "everyone" }));
        Assert.Equal(16, MailboxService.ResetLength(new SignInPolicy { MinLength = 16, TwoStep = "everyone" }));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task APasswordShorterThanTheRules_IsReplacedAtTheNextSignIn()
    {
        Assert.False(await this.svc.PasswordAcceptedAsync(this.ravi, "correct horse battery"));
        Assert.Null(await this.svc.SignInDemandAsync(this.ravi.Id));

        Assert.True(await this.svc.PasswordAcceptedAsync(this.ravi, "Ward#Round7"));
        Assert.Equal("password", await this.svc.SignInDemandAsync(this.ravi.Id));
        Assert.Null(await this.svc.SetPasswordAsync(this.ravi, "quiet harbour morning tea"));
        Assert.Null(await this.svc.SignInDemandAsync(this.ravi.Id));
    }

    [Fact]
    public async Task PasswordsNoLongerExpire()
    {
        Assert.Null(await this.svc.SaveSignInPolicyAsync(this.tenant.Id, new SignInPolicy { ExpiryDays = 30 }));
        this.ravi.CreatedAt = DateTimeOffset.UtcNow.AddYears(-1);
        await this.store.UpsertMailboxAsync(this.ravi);
        Assert.Null(await this.svc.SignInDemandAsync(this.ravi.Id));
    }

    [Fact]
    public async Task TenWrongPasswordsInARow_PauseSigningIn_For30Minutes()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 1; i < MailboxService.WrongPasswordLimit; i++)
        {
            Assert.Null(await this.svc.PasswordRefusedAsync(this.ravi.Id, now));
        }
        Assert.False(await this.svc.PasswordPausedAsync(this.ravi.Id, now));
        Assert.Equal(now + TimeSpan.FromMinutes(30), await this.svc.PasswordRefusedAsync(this.ravi.Id, now));
        Assert.True(await this.svc.PasswordPausedAsync(this.ravi.Id, now.AddMinutes(29)));
        Assert.False(await this.svc.PasswordPausedAsync(this.ravi.Id, now.AddMinutes(31)));
    }

    [Fact]
    public async Task ARightPassword_StartsTheCountAgain_AndAResetEndsThePause()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 1; i < MailboxService.WrongPasswordLimit; i++)
        {
            await this.svc.PasswordRefusedAsync(this.ravi.Id, now);
        }
        await this.svc.PasswordAcceptedAsync(this.ravi, "correct horse battery");
        Assert.Null(await this.svc.PasswordRefusedAsync(this.ravi.Id, now));
        Assert.Equal(1, (await this.svc.GetSecurityAsync(this.ravi.Id)).WrongPasswords);

        for (int i = 1; i < MailboxService.WrongPasswordLimit; i++)
        {
            await this.svc.PasswordRefusedAsync(this.ravi.Id, now);
        }
        Assert.True(await this.svc.PasswordPausedAsync(this.ravi.Id, now));
        Assert.Null(await this.svc.SetPasswordAsync(this.ravi, "quiet harbour morning tea"));
        Assert.False(await this.svc.PasswordPausedAsync(this.ravi.Id, now));
    }

    [Fact]
    public void TheThrottle_StopsAnAccountAfterTen_FromAnywhere_For30Minutes()
    {
        DateTimeOffset now = new(2026, 10, 10, 6, 0, 0, TimeSpan.Zero);
        var t = new LoginThrottle(5, 10, TimeSpan.FromMinutes(15), () => now);
        for (int i = 0; i < 10; i++)
        {
            t.RecordFailure("10.0.0." + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "x@clinic.example");
        }
        Assert.True(t.IsPairAllowed("10.0.0.99", "x@clinic.example"));
        Assert.False(t.IsAccountAllowed("x@clinic.example"));
        now = now.AddMinutes(31);
        Assert.True(t.IsAccountAllowed("x@clinic.example"));
    }
}
