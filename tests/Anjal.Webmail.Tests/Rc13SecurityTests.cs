using System.Security.Cryptography;
using System.Text;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.13: the pieces of sign-in security, each on its own - authenticator
/// codes, the QR picture, passkeys, signed-in devices and idle limits, and
/// the account service (earlier passwords, reset codes, backup codes,
/// invitations, trusted devices).
/// </summary>
public sealed class Rc13SecurityTests : IDisposable
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");
    private static readonly string[] OneCode = { "abcd-2345" };
    private static readonly string[] WrongCodes = { "000000", "111111", "222222", "333333" };
    private static readonly string[] ArunOnly = { "arun@anjal.co.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc13-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private readonly MailboxService svc;
    private MailboxRow mailbox = new();
    private MailboxRow recovery = new();

    public Rc13SecurityTests()
    {
        this.maildir = new MaildirStore(this.root, "test");
        this.svc = new MailboxService(this.store, this.store, this.maildir, "anjal.localhost");
        TenantRow tenant = this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa" }).GetAwaiter().GetResult();
        this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" }).GetAwaiter().GetResult();
        this.mailbox = this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            DisplayName = "Arun Shiva",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        }).GetAwaiter().GetResult();
        this.recovery = this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "home", Domain = "anjal.co.in" }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void Totp_MatchesTheRfc6238TestVectors(long unixSeconds, string expected)
    {
        // RFC 6238 appendix B (SHA-1), last six of the eight digits.
        Assert.Equal(expected, Totp.Code(RfcSecret, Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))));
    }

    [Fact]
    public void Totp_AcceptsTheStepEitherSide_ButNeverTheSameCodeTwice()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        long step = Totp.StepOf(now);
        Assert.Equal(step, Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step), now, 0));
        Assert.Equal(step - 1, Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step - 1), now, 0));
        Assert.Equal(step + 1, Totp.Verify(RfcSecret, " " + Totp.Code(RfcSecret, step + 1)[..3] + " " + Totp.Code(RfcSecret, step + 1)[3..], now, 0));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step - 2), now, 0));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step), now, step));
        Assert.Null(Totp.Verify(RfcSecret, "12345", now, 0));
        Assert.Null(Totp.Verify(RfcSecret, null, now, 0));
    }

    [Fact]
    public void Base32_FollowsRfc4648_AndTheLinkCarriesTheSecret()
    {
        Assert.Equal("MZXW6YTBOI", Base32.Encode(Encoding.ASCII.GetBytes("foobar")));
        Assert.Equal("foobar", Encoding.ASCII.GetString(Base32.Decode("mzxw 6ytb-oi")!));
        Assert.Null(Base32.Decode("not base32!"));
        byte[] secret = Totp.NewSecret();
        Assert.Equal(secret, Base32.Decode(Base32.Grouped(secret)));
        string uri = Totp.Uri("Imagiqa mail", "arun@anjal.co.in", secret);
        Assert.StartsWith("otpauth://totp/Imagiqa%20mail:arun%40anjal.co.in?secret=" + Base32.Encode(secret), uri, StringComparison.Ordinal);
        Assert.Contains("&issuer=Imagiqa%20mail", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void QrCode_ChoosesTheSmallestVersion_AndDrawsItsFixedPatterns()
    {
        Assert.Equal(21, QrCode.Encode("hi").GetLength(0));
        Assert.Equal(57, QrCode.Encode(new string('a', QrCode.MaxBytes)).GetLength(0));
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('a', QrCode.MaxBytes + 1)));
        bool[,] m = QrCode.Encode(Totp.Uri("Imagiqa Healthcare mail", "arun.shiva@imagiqa.co.in", Totp.NewSecret()));
        int n = m.GetLength(0);
        foreach ((int r, int c) in new[] { (0, 0), (0, n - 7), (n - 7, 0) })
        {
            // A finder: dark ring, light ring, dark 3x3 centre.
            Assert.True(m[r, c] && m[r + 6, c + 6] && m[r + 3, c + 3]);
            Assert.False(m[r + 1, c + 1] || m[r + 5, c + 5]);
        }
        for (int i = 8; i < n - 8; i++)
        {
            Assert.Equal(i % 2 == 0, m[6, i]);
            Assert.Equal(i % 2 == 0, m[i, 6]);
        }
        Assert.True(m[n - 8, 8], "the dark module");
        string svg = QrCode.Svg("otpauth://totp/x", "Picture <for> the app");
        Assert.StartsWith("<svg class=\"qr\"", svg, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Picture &lt;for&gt; the app\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Passkey_IsReadFromTheBrowser_AndASignInVerifiesWithIt()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        byte[] credentialId = RandomNumberGenerator.GetBytes(16);
        const string Origin = "https://mail.anjal.co.in";
        const string RpId = "mail.anjal.co.in";

        (PasskeyRecord? passkey, string? error) = WebAuthn.Register(
            ClientData("webauthn.create", challenge, Origin), Attestation(key, credentialId, RpId), challenge, Origin, RpId, "Windows, Chrome", DateTimeOffset.UtcNow);
        Assert.Null(error);
        Assert.NotNull(passkey);
        Assert.Equal(WebAuthn.ToBase64Url(credentialId), passkey!.Id);
        Assert.Equal(WebAuthn.Es256, WebAuthn.KeyAlgorithm(WebAuthn.FromBase64Url(passkey.PublicKey)!));

        // A sign-in: signed by the key, for this server, this origin and this challenge.
        byte[] next = RandomNumberGenerator.GetBytes(32);
        string client = ClientData("webauthn.get", next, Origin);
        byte[] authData = AuthData(RpId, 0x05, 7);
        string sig = Sign(key, authData, client);
        (uint count, string? bad) = WebAuthn.Verify(passkey, client, WebAuthn.ToBase64Url(authData), sig, next, Origin, RpId);
        Assert.Null(bad);
        Assert.Equal(7u, count);

        // Anything else is refused.
        Assert.NotNull(WebAuthn.Verify(passkey, client, WebAuthn.ToBase64Url(authData), sig, RandomNumberGenerator.GetBytes(32), Origin, RpId).Error);
        Assert.NotNull(WebAuthn.Verify(passkey, client, WebAuthn.ToBase64Url(authData), sig, next, "https://evil.example", RpId).Error);
        Assert.NotNull(WebAuthn.Verify(passkey, client, WebAuthn.ToBase64Url(AuthData("evil.example", 0x05, 8)), Sign(key, AuthData("evil.example", 0x05, 8), client), next, Origin, RpId).Error);
        Assert.NotNull(WebAuthn.Verify(passkey with { SignCount = 9 }, client, WebAuthn.ToBase64Url(authData), sig, next, Origin, RpId).Error);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.NotNull(WebAuthn.Verify(passkey, client, WebAuthn.ToBase64Url(authData), Sign(other, authData, client), next, Origin, RpId).Error);
        Assert.NotNull(WebAuthn.Verify(passkey, ClientData("webauthn.create", next, Origin), WebAuthn.ToBase64Url(authData), sig, next, Origin, RpId).Error);
        Assert.NotNull(WebAuthn.Register(ClientData("webauthn.create", challenge, Origin), "@@@", challenge, Origin, RpId, "x", DateTimeOffset.UtcNow).Error);
    }

    [Fact]
    public void Cbor_RefusesWhatItCannotRead()
    {
        Assert.Throws<FormatException>(() => new CborReader(new byte[] { 0x5F }).Read());
        Assert.Throws<FormatException>(() => new CborReader(new byte[] { 0x58, 0x10, 0x01 }).Read());
        byte[] deep = Enumerable.Repeat((byte)0x81, 40).Append((byte)0x01).ToArray();
        Assert.Throws<FormatException>(() => new CborReader(deep).Read());
    }

    [Fact]
    public async Task Sessions_EndWhenIdle_SharedSoonerThanOwn_AndCanBeEndedFromElsewhere()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var sessions = new SessionRegistry(this.svc, () => now);
        string shared = await sessions.StartAsync(this.mailbox.Id, true, "Mozilla/5.0 (Windows NT 10.0) Chrome/130", "10.0.0.1");
        string own = await sessions.StartAsync(this.mailbox.Id, false, "Mozilla/5.0 (Linux; Android 14) Chrome/130 Mobile", "10.0.0.2");
        System.Security.Claims.ClaimsPrincipal Who(string sid, bool s) => WebmailAuthService.WithSession(
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[] { new System.Security.Claims.Claim(WebmailAuthService.MailboxIdClaim, this.mailbox.Id.ToString()) }, "t")), sid, s);

        IReadOnlyList<SessionRecord> list = await sessions.ListAsync(this.mailbox.Id, shared);
        Assert.Equal(shared, list[0].Id);
        Assert.Equal("Windows, Chrome", list[0].Device);
        Assert.True(list[1].Phone);

        now += TimeSpan.FromMinutes(14);
        Assert.Equal(SessionState.Valid, await sessions.CheckAsync(Who(shared, true), activity: false));
        Assert.Equal(60, sessions.SecondsLeft(shared));
        now += TimeSpan.FromMinutes(2);
        Assert.Equal(SessionState.Idle, await sessions.CheckAsync(Who(shared, true), activity: true));
        Assert.Equal(SessionState.Ended, await sessions.CheckAsync(Who(shared, true), activity: true));

        // Activity keeps the person's own device signed in; a page checking in does not.
        Assert.Equal(SessionState.Valid, await sessions.CheckAsync(Who(own, false), activity: true));
        now += TimeSpan.FromHours(7.9);
        Assert.Equal(SessionState.Valid, await sessions.CheckAsync(Who(own, false), activity: false));
        now += TimeSpan.FromHours(0.2);
        Assert.Equal(SessionState.Idle, await sessions.CheckAsync(Who(own, false), activity: true));

        string a = await sessions.StartAsync(this.mailbox.Id, false, null, "10.0.0.3");
        string b = await sessions.StartAsync(this.mailbox.Id, false, null, "10.0.0.4");
        Assert.Equal(1, await sessions.EndOthersAsync(this.mailbox.Id, a));
        Assert.Equal(SessionState.Ended, await sessions.CheckAsync(Who(b, false), activity: true));
        Assert.Equal(SessionState.Valid, await sessions.CheckAsync(Who(a, false), activity: true));

        // A session from before rc.13 carries no id, and must sign in again.
        Assert.Equal(SessionState.Ended, await sessions.CheckAsync(new System.Security.Claims.ClaimsPrincipal(), activity: true));
    }

    [Fact]
    public void Approvals_AreAnsweredOnlyByTheSameMailbox_AndChallengesWorkOnce()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var sessions = new SessionRegistry(this.svc, () => now);
        Approval asked = sessions.RequestApproval(this.mailbox.Id, "sign-in", "Mozilla/5.0 (Macintosh; Mac OS X) Version/17 Safari/605", "1.2.3.4");
        Assert.Equal("macOS, Safari", asked.Device);
        Assert.Single(sessions.Waiting(this.mailbox.Id));
        Assert.False(sessions.Answer(this.recovery.Id, asked.Id, true));
        Assert.True(sessions.Answer(this.mailbox.Id, asked.Id, true));
        Assert.False(sessions.Answer(this.mailbox.Id, asked.Id, false));
        Assert.Empty(sessions.Waiting(this.mailbox.Id));
        now += TimeSpan.FromMinutes(6);
        Assert.Null(sessions.ApprovalOf(asked.Id));

        (string id, byte[] challenge) = sessions.NewChallenge(this.mailbox.Id, "register");
        Assert.Null(sessions.TakeChallenge(id, this.mailbox.Id, "sign-in"));
        (id, challenge) = sessions.NewChallenge(this.mailbox.Id, "register");
        Assert.Equal(challenge, sessions.TakeChallenge(id, this.mailbox.Id, "register"));
        Assert.Null(sessions.TakeChallenge(id, this.mailbox.Id, "register"));

        string pending = sessions.AddPending(this.mailbox.Id, "arun@anjal.co.in", shared: true);
        Assert.True(sessions.Pending(pending)!.Shared);
        now += TimeSpan.FromMinutes(11);
        Assert.Null(sessions.Pending(pending));

        sessions.Reveal("s1", OneCode);
        Assert.Equal(OneCode, sessions.TakeReveal("s1"));
        Assert.Null(sessions.TakeReveal("s1"));
    }

    [Fact]
    public async Task Password_CannotBeOneOfTheLastFive_WhenTheOrganisationAsks()
    {
        // Owner, 10 Oct 2026 (P8): not checked by default; an organisation may refuse the last 3 or 5.
        Assert.Null(await this.svc.SaveSignInPolicyAsync(this.mailbox.TenantId, new SignInPolicy { PasswordHistory = 5 }));
        string[] passwords = { "Teal-Lantern-11", "Teal-Lantern-22", "Teal-Lantern-33", "Teal-Lantern-44", "Teal-Lantern-55" };
        string current = "correct horse battery";
        foreach (string next in passwords)
        {
            Assert.Null(await this.svc.ChangePasswordAsync(this.mailbox.Id, current, next, null));
            current = next;
        }
        Assert.Contains("used that password before", await this.svc.ChangePasswordAsync(this.mailbox.Id, current, "Teal-Lantern-22", null), StringComparison.Ordinal);
        // Five changes on, the first is far enough back.
        Assert.Null(await this.svc.ChangePasswordAsync(this.mailbox.Id, current, "correct horse battery", null));
        Assert.Equal(4, (await this.svc.GetSecurityAsync(this.mailbox.Id)).PasswordHistory.Count);
        Assert.Contains("at least 15", await this.svc.ChangePasswordAsync(this.mailbox.Id, "correct horse battery", "Krishna@123", null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EarlierPasswords_AreNeitherCheckedNorKept_ByDefault()
    {
        Assert.Null(await this.svc.ChangePasswordAsync(this.mailbox.Id, "correct horse battery", "Teal-Lantern-11", null));
        Assert.Null(await this.svc.ChangePasswordAsync(this.mailbox.Id, "Teal-Lantern-11", "correct horse battery", null));
        Assert.Empty((await this.svc.GetSecurityAsync(this.mailbox.Id)).PasswordHistory);
    }

    [Fact]
    public async Task ResetCode_GoesToTheRecoveryAddress_WorksOnce_AndFiveWrongSpendIt()
    {
        MailboxRow fresh = (await this.store.GetMailboxByIdAsync(this.mailbox.Id))!;
        Assert.Equal("This account has no recovery address.", await this.svc.SendResetCodeAsync(fresh));
        Assert.NotNull(await this.svc.SetRecoveryAddressAsync(this.mailbox.Id, "wrong password", "home@anjal.co.in"));
        Assert.NotNull(await this.svc.SetRecoveryAddressAsync(this.mailbox.Id, "correct horse battery", "arun@anjal.co.in"));
        Assert.Null(await this.svc.SetRecoveryAddressAsync(this.mailbox.Id, "correct horse battery", "Home@anjal.co.in"));
        Assert.Equal("h••••@anjal.co.in", MailboxService.MaskAddress((await this.svc.GetSecurityAsync(this.mailbox.Id)).RecoveryAddress));

        Assert.Null(await this.svc.SendResetCodeAsync(fresh));
        Assert.NotNull(await this.svc.SendResetCodeAsync(fresh));   // not again within the minute
        MessageRow mail = this.store.MailboxMessages.Last(m => m.MailboxId == this.recovery.Id);
        string code = System.Text.RegularExpressions.Regex.Match(mail.BodyText, @"\b\d{6}\b").Value;
        Assert.Equal(6, code.Length);

        string wrong = code == "000000" ? "111111" : "000000";
        Assert.Contains("not right", await this.svc.ResetPasswordAsync(fresh, wrong, "Brass-Lantern-77", false), StringComparison.Ordinal);
        Assert.NotNull(await this.svc.ResetPasswordAsync(fresh, code, "short", false));   // the rules still apply
        Assert.Null(await this.svc.ResetPasswordAsync(fresh, code, "Brass-Lantern-77", false));
        Assert.NotNull(await this.svc.ResetPasswordAsync(fresh, code, "Brass-Lantern-88", false));   // used
        Assert.True(Pbkdf2Hasher.Verify("Brass-Lantern-77", (await this.store.GetMailboxByIdAsync(this.mailbox.Id))!.PasswordPbkdf2));

        // Five wrong codes spend a code, even the right one after them.
        SecurityDocument doc = await this.svc.GetSecurityAsync(this.mailbox.Id);
        doc.Reset = null;
        await this.store.SetMailboxDocumentAsync(this.mailbox.Id, MailboxService.SecurityKind, System.Text.Json.JsonSerializer.Serialize(doc));
        fresh = (await this.store.GetMailboxByIdAsync(this.mailbox.Id))!;
        Assert.Null(await this.svc.SendResetCodeAsync(fresh));
        code = System.Text.RegularExpressions.Regex.Match(this.store.MailboxMessages.Last(m => m.MailboxId == this.recovery.Id).BodyText, @"\b\d{6}\b").Value;
        wrong = code == "000000" ? "111111" : "000000";
        for (int i = 0; i < 5; i++)
        {
            await this.svc.ResetPasswordAsync(fresh, wrong, "Copper-Kettle-99", false);
        }
        Assert.Contains("expired", await this.svc.ResetPasswordAsync(fresh, code, "Copper-Kettle-99", false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticator_BackupCodes_AndTrustedDevices()
    {
        (string uri, string secretText) = (await this.svc.BeginAuthenticatorAsync(this.mailbox.Id))!.Value;
        Assert.Contains("issuer=Imagiqa%20mail", uri, StringComparison.Ordinal);
        Assert.Equal(secretText, (await this.svc.BeginAuthenticatorAsync(this.mailbox.Id))!.Value.Secret);   // the same until it is confirmed
        byte[] secret = Base32.Decode(secretText)!;
        SecurityDocument stored = await this.svc.GetSecurityAsync(this.mailbox.Id);
        Assert.DoesNotContain(Base32.Encode(secret), System.Text.Json.JsonSerializer.Serialize(stored), StringComparison.Ordinal);   // kept encrypted

        long step = Totp.StepOf(DateTimeOffset.UtcNow);
        string wrong = WrongCodes.First(w => w != Totp.Code(secret, step - 1) && w != Totp.Code(secret, step) && w != Totp.Code(secret, step + 1));
        Assert.NotNull((await this.svc.ConfirmAuthenticatorAsync(this.mailbox.Id, wrong)).Error);
        (string? error, IReadOnlyList<string> codes) = await this.svc.ConfirmAuthenticatorAsync(this.mailbox.Id, Totp.Code(secret, Totp.StepOf(DateTimeOffset.UtcNow)));
        Assert.Null(error);
        Assert.Equal(MailboxService.BackupCodeCount, codes.Count);
        Assert.All(codes, c => Assert.Matches("^[a-z2-9]{4}-[a-z2-9]{4}$", c));
        Assert.True((await this.svc.GetSecurityAsync(this.mailbox.Id)).TwoStepOn);

        // The code that turned it on cannot be used again; the next one can.
        Assert.False(await this.svc.CheckAuthenticatorCodeAsync(this.mailbox.Id, Totp.Code(secret, Totp.StepOf(DateTimeOffset.UtcNow))));
        Assert.True(await this.svc.CheckAuthenticatorCodeAsync(this.mailbox.Id, Totp.Code(secret, Totp.StepOf(DateTimeOffset.UtcNow) + 1)));

        Assert.True(await this.svc.UseBackupCodeAsync(this.mailbox.Id, codes[3].ToUpperInvariant().Replace("-", " ", StringComparison.Ordinal)));
        Assert.False(await this.svc.UseBackupCodeAsync(this.mailbox.Id, codes[3]));
        Assert.Equal(MailboxService.BackupCodeCount - 1, (await this.svc.GetSecurityAsync(this.mailbox.Id)).BackupCodes.Count);

        string token = await this.svc.TrustDeviceAsync(this.mailbox.Id, "Windows, Chrome");
        Assert.True(await this.svc.IsTrustedDeviceAsync(this.mailbox.Id, token));
        Assert.False(await this.svc.IsTrustedDeviceAsync(this.mailbox.Id, token + "x"));
        Assert.False(await this.svc.IsTrustedDeviceAsync(this.recovery.Id, token));
        await this.svc.ForgetTrustedDevicesAsync(this.mailbox.Id);
        Assert.False(await this.svc.IsTrustedDeviceAsync(this.mailbox.Id, token));

        Assert.NotNull(await this.svc.RemoveAuthenticatorAsync(this.mailbox.Id, "wrong"));
        Assert.Null(await this.svc.RemoveAuthenticatorAsync(this.mailbox.Id, "correct horse battery"));
        SecurityDocument off = await this.svc.GetSecurityAsync(this.mailbox.Id);
        Assert.False(off.TwoStepOn);
        Assert.Empty(off.BackupCodes);
    }

    [Fact]
    public async Task Invitation_WorksOnceForSevenDays_AndSetsTheFirstPassword()
    {
        MailboxRow invited = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.mailbox.TenantId, LocalPart = "meera.iyer", Domain = "anjal.co.in", DisplayName = "Meera Iyer" });
        (string token, DateTimeOffset expires) = await this.svc.CreateInvitationAsync(invited.Id, "Arun Shiva B");
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromDays(6.9), TimeSpan.FromDays(7));
        (MailboxRow Mailbox, InvitationState Invitation, string Organisation)? found = await this.svc.FindInvitationAsync(token);
        Assert.Equal(invited.Id, found!.Value.Mailbox.Id);
        Assert.Equal("Imagiqa", found.Value.Organisation);
        Assert.Null(await this.svc.FindInvitationAsync(token[..^2] + "AA"));
        Assert.Null(await this.svc.FindInvitationAsync("not-a-token"));

        Assert.NotNull(await this.svc.AcceptInvitationAsync(token, "meera", string.Empty));   // her own name
        Assert.NotNull(await this.svc.AcceptInvitationAsync(token, "Quiet harbour mornings", "meera.iyer@anjal.co.in"));
        Assert.Null(await this.svc.AcceptInvitationAsync(token, "Quiet harbour mornings", "meera.personal@example.com"));
        Assert.Null(await this.svc.FindInvitationAsync(token));
        Assert.Equal("meera.personal@example.com", (await this.svc.GetSecurityAsync(invited.Id)).RecoveryAddress);
        Assert.True(Pbkdf2Hasher.Verify("Quiet harbour mornings", (await this.store.GetMailboxByIdAsync(invited.Id))!.PasswordPbkdf2));
    }

    [Fact]
    public async Task SignInPage_KnowsTheOrganisation_FromTheHost()
    {
        Assert.Equal("anjal.co.in", (await this.svc.OrganisationForHostAsync("mail.anjal.co.in"))!.Domain);
        Assert.Equal("Imagiqa", (await this.svc.OrganisationForHostAsync("localhost"))!.Name);   // one organisation: that one
        Assert.Equal("arun@anjal.co.in", MailboxService.FullAddress(" Arun ", "anjal.co.in"));
        Assert.Equal("arun@other.in", MailboxService.FullAddress("arun@other.in", "anjal.co.in"));
        Assert.Equal(this.mailbox.Id, (await this.svc.FindMailboxAsync("arun", "anjal.co.in"))!.Id);
    }

    [Fact]
    public async Task MailSettings_UndoAndTrash_AndTrashEmptiesOnlyAfterItsTime()
    {
        Assert.Equal(10, (await this.svc.GetMailSettingsAsync(this.mailbox.Id)).UndoSeconds);
        Assert.NotNull(await this.svc.SetMailSettingsAsync(this.mailbox.Id, 7, null));
        Assert.NotNull(await this.svc.SetMailSettingsAsync(this.mailbox.Id, null, 3));
        Assert.Null(await this.svc.SetMailSettingsAsync(this.mailbox.Id, 0, 7));
        Assert.Equal(0, (await this.svc.GetMailSettingsAsync(this.mailbox.Id)).UndoSeconds);

        // An old message put in Trash today waits its seven days; it is not judged by its date.
        await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "a@x.test",
            EnvelopeTo = ArunOnly,
            RawBytes = Encoding.ASCII.GetBytes("From: a@x.test\r\nDate: Mon, 1 Jan 2024 10:00:00 +0000\r\nSubject: Old\r\n\r\nx\r\n"),
        });
        MessageRow old = this.store.MailboxMessages.Last(m => m.MailboxId == this.mailbox.Id);
        Assert.NotNull(await this.svc.MoveAsync(this.mailbox.Id, old.Id, "Trash"));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Equal(0, await this.svc.EmptyOldAsync(this.mailbox.Id, now));
        Assert.Equal(0, await this.svc.EmptyOldAsync(this.mailbox.Id, now + TimeSpan.FromDays(6)));
        Assert.Equal(1, await this.svc.EmptyOldAsync(this.mailbox.Id, now + TimeSpan.FromDays(7.1)));
        Assert.DoesNotContain(this.store.MailboxMessages, m => m.Id == old.Id);
    }

    private static string ClientData(string type, byte[] challenge, string origin) =>
        WebAuthn.ToBase64Url(Encoding.UTF8.GetBytes($"{{\"type\":\"{type}\",\"challenge\":\"{WebAuthn.ToBase64Url(challenge)}\",\"origin\":\"{origin}\",\"crossOrigin\":false}}"));

    private static byte[] AuthData(string rpId, byte flags, uint count)
    {
        byte[] data = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(rpId)).CopyTo(data, 0);
        data[32] = flags;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(33), count);
        return data;
    }

    private static string Sign(ECDsa key, byte[] authData, string clientDataJson)
    {
        byte[] signed = authData.Concat(SHA256.HashData(WebAuthn.FromBase64Url(clientDataJson)!)).ToArray();
        return WebAuthn.ToBase64Url(key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    private static string Attestation(ECDsa key, byte[] credentialId, string rpId)
    {
        ECParameters p = key.ExportParameters(false);
        var cose = new List<byte> { 0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20 };
        cose.AddRange(p.Q.X!);
        cose.AddRange(new byte[] { 0x22, 0x58, 0x20 });
        cose.AddRange(p.Q.Y!);
        var auth = new List<byte>(AuthData(rpId, 0x45, 0));
        auth.AddRange(new byte[16]);
        auth.Add((byte)(credentialId.Length >> 8));
        auth.Add((byte)credentialId.Length);
        auth.AddRange(credentialId);
        auth.AddRange(cose);
        var att = new List<byte> { 0xA3 };
        att.AddRange(Text("fmt"));
        att.AddRange(Text("none"));
        att.AddRange(Text("attStmt"));
        att.Add(0xA0);
        att.AddRange(Text("authData"));
        att.AddRange(new byte[] { 0x59, (byte)(auth.Count >> 8), (byte)auth.Count });
        att.AddRange(auth);
        return WebAuthn.ToBase64Url(att.ToArray());
    }

    private static byte[] Text(string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        return new[] { (byte)(0x60 | b.Length) }.Concat(b).ToArray();
    }
}
