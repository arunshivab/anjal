using Anjal.Store;

namespace Anjal.Server.Tests;

/// <summary>
/// rc.15 (items 59 and 61): a refused submission by an application's key goes to the activity
/// log (a guessed user name does not), and the refusal counts add up per day.
/// </summary>
public sealed class ServiceRecordsTests
{
    [Fact]
    public async Task ARefusalForAnApplicationsKey_IsLogged_AndAGuessIsNot()
    {
        var store = new InMemoryMessageStore();
        await store.UpsertSmtpUserAsync(new SmtpUserRow { Username = "app-abc@clinic.example", PasswordPbkdf2 = "x", Enabled = true });
        System.Action<Anjal.Smtp.SubmissionRefusal> tell = ServiceRecords.RefusalRecorder(store, _ => { });
        tell(new Anjal.Smtp.SubmissionRefusal("app-abc@clinic.example", "wrong password", "203.0.113.9"));
        tell(new Anjal.Smtp.SubmissionRefusal("admin", "wrong password", "203.0.113.9"));
        IReadOnlyList<AuditEvent> log = Array.Empty<AuditEvent>();
        for (int i = 0; i < 50 && log.Count == 0; i++)
        {
            await Task.Delay(20);
            log = await store.ListAuditAsync(10);
        }
        AuditEvent e = Assert.Single(log);
        Assert.Equal("smtp.submission.refused", e.Action);
        Assert.Equal("app-abc@clinic.example", e.Subject);
        Assert.Equal("wrong password", e.Detail);
        Assert.Equal("203.0.113.9", e.RemoteAddress);
    }

    [Fact]
    public async Task RefusalCounts_AddUpPerDay()
    {
        var store = new InMemoryMessageStore();
        // Days are the server's own (Anjal's server runs in Asia/Kolkata).
        DateTimeOffset day = new(new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Local));
        await ServiceRecords.AddAsync(store, day, new Dictionary<string, long> { ["anjal_smtp_auth_failures_total"] = 4 });
        await ServiceRecords.AddAsync(store, day.AddHours(2), new Dictionary<string, long> { ["anjal_smtp_auth_failures_total"] = 3, ["anjal_spam_rejected_total"] = 1 });
        await ServiceRecords.AddAsync(store, day.AddDays(1), new Dictionary<string, long> { ["anjal_spam_rejected_total"] = 2 });
        string json = (await store.GetServiceRecordAsync(ServiceRecords.RefusalsKind))!;
        Dictionary<string, Dictionary<string, long>> days = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, long>>>(json)!;
        Assert.Equal(7, days["2026-10-08"]["anjal_smtp_auth_failures_total"]);
        Assert.Equal(1, days["2026-10-08"]["anjal_spam_rejected_total"]);
        Assert.Equal(2, days["2026-10-09"]["anjal_spam_rejected_total"]);
    }
}
