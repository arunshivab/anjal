using System.Globalization;
using System.Text.Json;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One day of the server's disk record (rc.15, item 61).</summary>
public sealed class DiskDay
{
    /// <summary>The day, yyyy-MM-dd, in the server's own time zone.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>The disk's size.</summary>
    public long Total { get; set; }

    /// <summary>Used on it.</summary>
    public long Used { get; set; }

    /// <summary>Mail.</summary>
    public long Mail { get; set; }

    /// <summary>Evidence.</summary>
    public long Evidence { get; set; }

    /// <summary>Backup copies waiting on the server.</summary>
    public long Backups { get; set; }

    /// <summary>The database (DES-11 D8); 0 when it was not measured.</summary>
    public long Database { get; set; }
}

/// <summary>A sudden rise (DES-11 D8).</summary>
/// <param name="Kind">"received", "sent", "bounced" or "refused".</param>
/// <param name="Tenant">The organisation; null for the whole service (refusals).</param>
/// <param name="Today">Today's count.</param>
/// <param name="Usual">The daily average of the previous 7 days.</param>
public sealed record SuddenRise(string Kind, Guid? Tenant, long Today, double Usual);

/// <summary>One day of the 30-day activity chart (DES-11 D8).</summary>
/// <param name="Day">The day, in the viewer's zone.</param>
/// <param name="Received">Received (Junk left out).</param>
/// <param name="Sent">Sent.</param>
/// <param name="Refused">Refused by the mail server.</param>
/// <param name="Bounced">Sent mail given up on.</param>
public sealed record ActivityDay(DateTime Day, long Received, long Sent, long Refused, long Bounced);

/// <summary>A restore drill, as the operator records it (rc.15, item 61).</summary>
public sealed class RestoreDrill
{
    /// <summary>The day it was done, yyyy-MM-dd.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Whether everything came back.</summary>
    public bool Passed { get; set; }

    /// <summary>A short note: what was restored, or what failed.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>Who recorded it.</summary>
    public string By { get; set; } = string.Empty;

    /// <summary>When it was recorded.</summary>
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>How much of the service's mail a big receiver took (rc.15, item 61).</summary>
/// <param name="Name">"Gmail", "Microsoft" or "Yahoo".</param>
/// <param name="Sent">Accepted.</param>
/// <param name="Failed">Given up on.</param>
/// <param name="Waiting">Still trying.</param>
public sealed record Acceptance(string Name, long Sent, long Failed, long Waiting)
{
    /// <summary>Accepted, as a percentage of those settled (sent or given up); null when none settled.</summary>
    public int? Percent => this.Sent + this.Failed == 0 ? null : Sizes.Percent(this.Sent, this.Sent + this.Failed);
}

/// <summary>
/// rc.15 (item 61): what the Anjal service overview keeps and works out -
/// the daily disk record and its forecast, restore drills, the mail server's
/// refusals, acceptance by the big receivers, sudden rises in mail, and the
/// processor. Totals per service or per organisation only: never people or mail.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The service record holding the daily disk record.</summary>
    public const string DiskHistoryKind = "disk-history";

    /// <summary>The service record holding the restore drills.</summary>
    public const string DrillsKind = "restore-drills";

    /// <summary>The service record the mail server adds its refusal counts to, per day.</summary>
    public const string RefusalsKind = "refusals";

    /// <summary>Months between restore drills (owner, 7 Oct 2026).</summary>
    public const int DrillMonths = 6;

    // The receivers the owner named, by their receiving domains.
    private static readonly (string Name, string[] Domains)[] BigReceivers =
    {
        ("Gmail", new[] { "gmail.com", "googlemail.com" }),
        ("Microsoft", new[] { "outlook.com", "hotmail.com", "live.com", "msn.com", "outlook.in", "hotmail.co.uk", "live.in" }),
        ("Yahoo", new[] { "yahoo.com", "yahoo.co.in", "yahoo.in", "ymail.com", "rocketmail.com", "yahoo.co.uk" }),
    };

    private static (DateTimeOffset At, TimeSpan Cpu)? lastCpu;

    /// <summary>Add today's line to the disk record, once a day (kept for 400 days).</summary>
    /// <param name="capacity">The disk now.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when a line was added.</returns>
    public async Task<bool> RecordDiskDayAsync(ServiceCapacity capacity, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        if (capacity.DiskTotal <= 0)
        {
            return false;
        }
        List<DiskDay> days = await this.DiskHistoryAsync(ct).ConfigureAwait(false);
        string today = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (days.Count > 0 && days[^1].Date == today)
        {
            return false;
        }
        days.Add(new DiskDay { Date = today, Total = capacity.DiskTotal, Used = capacity.DiskUsed, Mail = capacity.Mail, Evidence = capacity.Evidence, Backups = capacity.Backups, Database = capacity.Database });
        await this.messageStore.SetServiceRecordAsync(DiskHistoryKind, JsonSerializer.Serialize(days.TakeLast(400).ToList()), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Add today's line to the disk record from the disk as it is now (the hourly pass; once a day).</summary>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when a line was added.</returns>
    public async Task<bool> RecordDiskTodayAsync(DateTimeOffset now, CancellationToken ct = default) =>
        await this.RecordDiskDayAsync(
            await this.WithDatabaseAsync(Capacity((this.maildir as Anjal.Mailbox.MaildirStore)?.Root ?? Anjal.Mailbox.MaildirStore.DefaultRoot, Environment.GetEnvironmentVariable("ANJAL_EVIDENCE_ROOT") ?? Anjal.Mailbox.EvidenceVault.DefaultRoot, now), ct).ConfigureAwait(false),
            now,
            ct).ConfigureAwait(false);

    /// <summary>The capacity with the database's size taken in (DES-11 D8); unchanged when it cannot be measured.</summary>
    /// <param name="capacity">The capacity.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The capacity.</returns>
    public async Task<ServiceCapacity> WithDatabaseAsync(ServiceCapacity capacity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        try
        {
            return await this.messageStore.DatabaseBytesAsync(ct).ConfigureAwait(false) is long db ? capacity with { Database = db } : capacity;
        }
        catch (System.Data.Common.DbException)
        {
            return capacity;
        }
    }

    /// <summary>The daily disk record, oldest first.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The days.</returns>
    public async Task<List<DiskDay>> DiskHistoryAsync(CancellationToken ct = default) =>
        await this.ServiceRecordAsync<List<DiskDay>>(DiskHistoryKind, ct).ConfigureAwait(false) ?? new List<DiskDay>();

    /// <summary>
    /// When the disk will be full, from the growth of the last 90 days of the
    /// record (a straight line through them): in months, or null when it is
    /// not growing or the record is shorter than a week.
    /// </summary>
    /// <param name="days">The record.</param>
    /// <param name="capacity">The disk now.</param>
    /// <returns>Months, at least 1.</returns>
    public static int? MonthsUntilFull(IReadOnlyList<DiskDay> days, ServiceCapacity capacity) => MonthsUntilFull(days, capacity, d => d.Used);

    /// <summary>
    /// When the disk will be full if only one part of it keeps growing as it has over the last 90
    /// days of the record (DES-11 D8: the database's own forecast): in months, or null when that
    /// part is not growing or has fewer than a week of records.
    /// </summary>
    /// <param name="days">The record.</param>
    /// <param name="capacity">The disk now.</param>
    /// <param name="of">The part measured.</param>
    /// <returns>Months, at least 1.</returns>
    public static int? MonthsUntilFull(IReadOnlyList<DiskDay> days, ServiceCapacity capacity, Func<DiskDay, long> of)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(of);
        List<DiskDay> recent = days.TakeLast(90).Where(d => of(d) > 0).ToList();
        if (recent.Count < 7 || capacity.DiskTotal <= 0)
        {
            return null;
        }
        // Least squares: bytes used against the day's number.
        double n = recent.Count;
        double sx = 0;
        double sy = 0;
        double sxx = 0;
        double sxy = 0;
        DateTime first = DateTime.ParseExact(recent[0].Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (DiskDay d in recent)
        {
            double x = (DateTime.ParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture) - first).TotalDays;
            sx += x;
            sy += of(d);
            sxx += x * x;
            sxy += x * of(d);
        }
        double denominator = (n * sxx) - (sx * sx);
        if (denominator <= 0)
        {
            return null;
        }
        double perDay = ((n * sxy) - (sx * sy)) / denominator;
        if (perDay <= 0)
        {
            return null;
        }
        double daysLeft = (capacity.DiskTotal - capacity.DiskUsed) / perDay;
        return (int)Math.Max(1, Math.Round(daysLeft / 30.44));
    }

    /// <summary>How much something grew over the last days of the disk record, or null when the record is too short.</summary>
    /// <param name="days">The record.</param>
    /// <param name="span">How many days back.</param>
    /// <param name="of">What is measured.</param>
    /// <returns>The growth in bytes (negative when it shrank).</returns>
    public static long? GrowthOver(IReadOnlyList<DiskDay> days, int span, Func<DiskDay, long> of)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(of);
        if (days.Count < 2)
        {
            return null;
        }
        DiskDay last = days[^1];
        DateTime lastDate = DateTime.ParseExact(last.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        DiskDay start = days.FirstOrDefault(d => (lastDate - DateTime.ParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)).TotalDays <= span) ?? days[0];
        return start == last ? null : of(last) - of(start);
    }

    /// <summary>The restore drills recorded, newest first.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The drills.</returns>
    public async Task<IReadOnlyList<RestoreDrill>> DrillsAsync(CancellationToken ct = default) =>
        (await this.ServiceRecordAsync<List<RestoreDrill>>(DrillsKind, ct).ConfigureAwait(false) ?? new List<RestoreDrill>())
            .OrderByDescending(d => d.Date, StringComparer.Ordinal).ThenByDescending(d => d.RecordedAt).ToList();

    /// <summary>Record a restore drill (the operator, from the Service health page).</summary>
    /// <param name="drill">The drill.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RecordDrillAsync(RestoreDrill drill, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(drill);
        List<RestoreDrill> all = await this.ServiceRecordAsync<List<RestoreDrill>>(DrillsKind, ct).ConfigureAwait(false) ?? new List<RestoreDrill>();
        all.Add(drill);
        await this.messageStore.SetServiceRecordAsync(DrillsKind, JsonSerializer.Serialize(all.TakeLast(100).ToList()), ct).ConfigureAwait(false);
    }

    /// <summary>When the next restore drill is due: six months after the last that passed; null when none is recorded.</summary>
    /// <param name="drills">The drills.</param>
    /// <returns>The day.</returns>
    public static DateTime? NextDrillDue(IReadOnlyList<RestoreDrill> drills)
    {
        ArgumentNullException.ThrowIfNull(drills);
        RestoreDrill? last = drills.Where(d => d.Passed).OrderByDescending(d => d.Date, StringComparer.Ordinal).FirstOrDefault();
        return last is not null && DateTime.TryParseExact(last.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day) ? day.AddMonths(DrillMonths) : null;
    }

    /// <summary>
    /// The mail server's refusals since a day, added up per counter. The server
    /// keeps them per day of its own time zone (Asia/Kolkata on Anjal's server),
    /// so a day here is that day in the same zone.
    /// </summary>
    /// <param name="since">From the day this moment falls on, in the server's zone.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The totals, by counter name.</returns>
    public async Task<IReadOnlyDictionary<string, long>> RefusalsSinceAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        Dictionary<string, Dictionary<string, long>> days = await this.ServiceRecordAsync<Dictionary<string, Dictionary<string, long>>>(RefusalsKind, ct).ConfigureAwait(false) ?? new Dictionary<string, Dictionary<string, long>>();
        string from = since.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach ((string day, Dictionary<string, long> counts) in days)
        {
            if (string.CompareOrdinal(day, from) < 0)
            {
                continue;
            }
            foreach ((string name, long n) in counts)
            {
                totals[name] = (totals.TryGetValue(name, out long t) ? t : 0) + n;
            }
        }
        return totals;
    }

    /// <summary>How much of the service's mail Gmail, Microsoft and Yahoo accepted in a period, from Anjal's own sending records.</summary>
    /// <param name="period">The period.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>One line per receiver.</returns>
    public async Task<IReadOnlyList<Acceptance>> AcceptanceAsync(DashPeriod period, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(period);
        IReadOnlyList<RecipientDomainTraffic> traffic = await this.messageStore.CountOutboundByRecipientDomainAsync(period.Start, period.End, ct).ConfigureAwait(false);
        return BigReceivers.Select(r =>
        {
            List<RecipientDomainTraffic> mine = traffic.Where(t => r.Domains.Contains(t.Domain, StringComparer.OrdinalIgnoreCase)).ToList();
            return new Acceptance(r.Name, mine.Sum(t => t.Sent), mine.Sum(t => t.Failed), mine.Sum(t => t.Waiting));
        }).ToList();
    }

    /// <summary>
    /// Sudden rises (DES-11 D8, owner 10 Oct 2026): today against the daily average of the previous
    /// 7 days - more than three times it, and at least 20. Mail received, sent out and bounced is
    /// counted per organisation; refusals for the whole service, as the mail server counts them
    /// before any organisation is known.
    /// </summary>
    /// <param name="clock">The viewer's zone, for "today".</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Each rise, the biggest first.</returns>
    public async Task<IReadOnlyList<SuddenRise>> SuddenRisesAsync(ZonedClock clock, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        DashPeriod today = DashPeriodOf("today", clock, now);
        var rises = new List<SuddenRise>();
        void Look(string kind, IReadOnlyDictionary<Guid, long> todays, IReadOnlyDictionary<Guid, long> week)
        {
            foreach ((Guid tenant, long n) in todays)
            {
                double usual = (week.TryGetValue(tenant, out long w) ? w : 0) / 7.0;
                if (IsRise(n, usual))
                {
                    rises.Add(new SuddenRise(kind, tenant, n, usual));
                }
            }
        }

        Look("received",
            await this.store.CountReceivedByTenantAsync(today.Start, today.End, ct).ConfigureAwait(false),
            await this.store.CountReceivedByTenantAsync(today.Start.AddDays(-7), today.Start, ct).ConfigureAwait(false));
        Dictionary<string, Guid> owner = (await this.store.ListTenantDomainsAsync(null, ct).ConfigureAwait(false))
            .GroupBy(d => d.Domain.ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().TenantId, StringComparer.Ordinal);
        IReadOnlyList<SenderDomainTraffic> outToday = await this.messageStore.CountOutboundBySenderDomainAsync(today.Start, today.End, ct).ConfigureAwait(false);
        IReadOnlyList<SenderDomainTraffic> outWeek = await this.messageStore.CountOutboundBySenderDomainAsync(today.Start.AddDays(-7), today.Start, ct).ConfigureAwait(false);
        Dictionary<Guid, long> ByTenant(IReadOnlyList<SenderDomainTraffic> rows, Func<SenderDomainTraffic, long> of) => rows
            .Where(r => owner.ContainsKey(r.Domain))
            .GroupBy(r => owner[r.Domain])
            .ToDictionary(g => g.Key, g => g.Sum(of));
        Look("sent", ByTenant(outToday, r => r.Queued), ByTenant(outWeek, r => r.Queued));
        Look("bounced", ByTenant(outToday, r => r.Failed), ByTenant(outWeek, r => r.Failed));

        // Refusals: the mail server's days are its own zone's (as RefusalsSinceAsync reads them).
        Dictionary<string, Dictionary<string, long>> days = await this.ServiceRecordAsync<Dictionary<string, Dictionary<string, long>>>(RefusalsKind, ct).ConfigureAwait(false) ?? new Dictionary<string, Dictionary<string, long>>();
        DateTime serverToday = now.ToLocalTime().Date;
        long RefusedOn(DateTime day) => days.TryGetValue(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out Dictionary<string, long>? c) ? c.Values.Sum() : 0;
        long refusedToday = RefusedOn(serverToday);
        double refusedUsual = Enumerable.Range(1, 7).Sum(i => RefusedOn(serverToday.AddDays(-i))) / 7.0;
        if (IsRise(refusedToday, refusedUsual))
        {
            rises.Add(new SuddenRise("refused", null, refusedToday, refusedUsual));
        }
        return rises.OrderByDescending(r => r.Today).ToList();
    }

    /// <summary>A sudden rise: more than three times the usual day, and at least 20.</summary>
    /// <param name="today">Today's count.</param>
    /// <param name="usual">The usual day.</param>
    /// <returns>True for a rise.</returns>
    public static bool IsRise(long today, double usual) => today >= 20 && today > usual * 3;

    /// <summary>
    /// The last 30 days of the whole service, day by day in the viewer's zone (DES-11 D8): mail
    /// received and sent (as the dashboards count them), refused by the mail server, and bounced.
    /// </summary>
    /// <param name="clock">The viewer's zone.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Thirty days, oldest first; today last.</returns>
    public async Task<IReadOnlyList<ActivityDay>> ActivityAsync(ZonedClock clock, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        DateTime today = clock.Local(now).Date;
        DateTime first = today.AddDays(-29);
        DateTimeOffset At(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(local, clock.Zone), TimeSpan.Zero);
        MailFigures figures = await this.store.GetMailFiguresAsync(FigureScope.Service, At(first), now, clock.ZoneId, TimeStep.Day, ct).ConfigureAwait(false);
        Dictionary<string, Dictionary<string, long>> refusals = await this.ServiceRecordAsync<Dictionary<string, Dictionary<string, long>>>(RefusalsKind, ct).ConfigureAwait(false) ?? new Dictionary<string, Dictionary<string, long>>();
        var result = new List<ActivityDay>();
        for (DateTime day = first; day <= today; day = day.AddDays(1))
        {
            TimeBucket? bucket = figures.Series.FirstOrDefault(b => b.Start.Date == day);
            long bounced = (await this.messageStore.CountOutboundBySenderDomainAsync(At(day), At(day.AddDays(1)), ct).ConfigureAwait(false)).Sum(t => t.Failed);
            long refused = refusals.TryGetValue(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out Dictionary<string, long>? c) ? c.Values.Sum() : 0;
            result.Add(new ActivityDay(day, bucket?.Received ?? 0, bucket?.Sent ?? 0, refused, bounced));
        }
        return result;
    }

    /// <summary>The database's size, or null when it cannot be measured.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Bytes.</returns>
    public Task<long?> DatabaseBytesAsync(CancellationToken ct = default) => this.messageStore.DatabaseBytesAsync(ct);

    /// <summary>
    /// The processor: on Linux the load over the last minute against the
    /// number of cores; elsewhere this service's own share since it was last
    /// asked. Null when it cannot be measured.
    /// </summary>
    /// <param name="now">Now.</param>
    /// <returns>A percentage of the whole processor, and the cores.</returns>
    public static (int Percent, int Cores)? Processor(DateTimeOffset now)
    {
        int cores = Environment.ProcessorCount;
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/loadavg"))
            {
                string first = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                double load = double.Parse(first, CultureInfo.InvariantCulture);
                return ((int)Math.Min(100, Math.Round(load * 100 / cores)), cores);
            }
            TimeSpan cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            (DateTimeOffset At, TimeSpan Cpu)? before = lastCpu;
            lastCpu = (now, cpu);
            if (before is { } b && now - b.At > TimeSpan.FromSeconds(1))
            {
                return ((int)Math.Min(100, Math.Round((cpu - b.Cpu).TotalMilliseconds * 100 / ((now - b.At).TotalMilliseconds * cores))), cores);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException or IndexOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The organisation this server's own domain belongs to (the parent of its host name), or null.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The organisation.</returns>
    public async Task<TenantRow?> ServiceOrganisationAsync(CancellationToken ct = default)
    {
        string host = this.hostName.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (string d in new[] { host, host.Contains('.', StringComparison.Ordinal) ? host[(host.IndexOf('.', StringComparison.Ordinal) + 1)..] : host })
        {
            if (await this.store.GetTenantDomainAsync(d, ct).ConfigureAwait(false) is TenantDomainRow row)
            {
                return await this.store.GetTenantByIdAsync(row.TenantId, ct).ConfigureAwait(false);
            }
        }
        return null;
    }

    private async Task<T?> ServiceRecordAsync<T>(string kind, CancellationToken ct)
        where T : class
    {
        string? json = await this.messageStore.GetServiceRecordAsync(kind, ct).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
