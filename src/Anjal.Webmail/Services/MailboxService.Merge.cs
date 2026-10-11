using System.Globalization;
using Anjal.Mime;

namespace Anjal.Webmail.Services;

/// <summary>One person in a "Send one each": their address and the values of their blanks.</summary>
/// <param name="Address">The address.</param>
/// <param name="Values">Blank name to value, for example "First name" to "Ravi".</param>
public sealed record MergePerson(string Address, IReadOnlyDictionary<string, string> Values);

/// <summary>rc.15 (item 64): the organisation's daily limit for Send one each, as stored.</summary>
public sealed class MergeLimitSetting
{
    /// <summary>Messages per person per day.</summary>
    public int Limit { get; set; } = MailboxService.DefaultDailyMergeLimit;
}

/// <summary>
/// Who may use Send one each (owner, 8 Oct 2026): no one, until the organisation's administrator
/// gives it to a person. It is never available to people on their own.
/// </summary>
public sealed class MergeAccess
{
    /// <summary>The people allowed to send one each.</summary>
    public List<Guid> People { get; set; } = new();
}

/// <summary>rc.15 (item 64): how one blank is filled - the field it reads, and what to write when that is empty.</summary>
/// <param name="Field">The list column or contact field; empty for the field of the blank's own name.</param>
/// <param name="Fallback">Written when the person has no value.</param>
public sealed record MergeBlank(string Field, string Fallback);

/// <summary>
/// Send one each (rc.12, item 64, the board ComposeMerge): every recipient
/// gets their own copy with the blanks filled for them, and no one sees
/// another's address. Copies wait in Scheduled and go out gradually, so
/// receiving servers do not take them for spam; the person's daily merge
/// limit applies.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The most merged messages one person may send in a day (until the organisation sets its own).</summary>
    public const int DefaultDailyMergeLimit = 200;

    /// <summary>rc.15 (item 64): Anjal's ceiling; an organisation may set anything from 1 to this.</summary>
    public const int MergeCeiling = 500;

    /// <summary>The kind of the organisation's merge-limit document.</summary>
    public const string MergeLimitKind = "merge-limit";

    /// <summary>The most people in one merge.</summary>
    public const int MaxMergePeople = 500;

    /// <summary>What a name blank becomes when a person has none (the board: "Dear colleague").</summary>
    public const string NameFallback = "colleague";

    /// <summary>The kind of the organisation's document naming who may send one each (owner, 8 Oct 2026).</summary>
    public const string MergeAccessKind = "merge-access";

    /// <summary>What a person is told who has not been given Send one each.</summary>
    public const string MergeNotAllowed = "Send one each is not turned on for you. Your organisation's administrator can turn it on.";

    /// <summary>The kind of the document counting today's merged messages.</summary>
    public const string MergeLogKind = "merge-log";

    private static readonly string[] NameBlanks = { "First name", "Name", "Last name" };

    /// <summary>
    /// The people of a merge: everyone in To, with their blanks from
    /// contacts; or, when a list (CSV) is given, its rows - its header names
    /// the blanks, and the column holding addresses is found by itself.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="to">The To line.</param>
    /// <param name="csv">An uploaded list, or null.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<MergePerson>> MergePeopleAsync(Guid mailboxId, string to, string? csv, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(to);
        var people = new List<MergePerson>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(csv))
        {
            IReadOnlyList<IReadOnlyList<string>> rows = ReadCsvRows(csv.TrimStart('﻿'));
            if (rows.Count > 1)
            {
                IReadOnlyList<string> head = rows[0];
                int addressColumn = -1;
                for (int i = 0; i < head.Count && addressColumn < 0; i++)
                {
                    string h = head[i].Trim().ToLowerInvariant();
                    if (h.Contains("mail", StringComparison.Ordinal) || h == "address")
                    {
                        addressColumn = i;
                    }
                }
                foreach (IReadOnlyList<string> row in rows.Skip(1))
                {
                    string raw = addressColumn >= 0 && addressColumn < row.Count ? row[addressColumn] : row.FirstOrDefault(f => f.Contains('@', StringComparison.Ordinal)) ?? string.Empty;
                    IReadOnlyList<MailAddress> parsed = AddressParser.Parse(raw.Trim());
                    if (parsed.Count != 1 || !seen.Add(parsed[0].Address))
                    {
                        continue;
                    }
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < head.Count && i < row.Count; i++)
                    {
                        if (i != addressColumn && head[i].Trim().Length > 0)
                        {
                            values[head[i].Trim()] = row[i].Trim();
                        }
                    }
                    people.Add(new MergePerson(parsed[0].Address, values));
                }
                return people.Take(MaxMergePeople).ToList();
            }
        }
        foreach (MailAddress a in AddressParser.Parse(to))
        {
            if (!seen.Add(a.Address))
            {
                continue;
            }
            Contact? c = await this.FindContactAsync(mailboxId, a.Address, ct).ConfigureAwait(false);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string first = c?.FirstName.Length > 0 ? c.FirstName : a.DisplayName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (first.Length > 0)
            {
                values["First name"] = first;
                values["Name"] = first;
            }
            if (c?.LastName.Length > 0)
            {
                values["Last name"] = c.LastName;
            }
            if (c?.Organisation.Length > 0)
            {
                values["Organisation"] = c.Organisation;
            }
            people.Add(new MergePerson(a.Address, values));
        }
        return people.Take(MaxMergePeople).ToList();
    }

    /// <summary>
    /// One person's copy: each blank filled from the field it was matched to (by default the field
    /// of the same name), else its chosen fallback, else "colleague" for a name blank.
    /// </summary>
    /// <param name="text">The text with blanks.</param>
    /// <param name="person">The person.</param>
    /// <param name="map">rc.15 (item 64): blank to the field it reads and its fallback; null for the defaults.</param>
    public static string MergeFill(string text, MergePerson person, IReadOnlyDictionary<string, MergeBlank>? map = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(person);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string blank in TemplateCatalogue.BlanksIn(text))
        {
            MergeBlank? rule = map is not null && map.TryGetValue(blank, out MergeBlank? r) ? r : null;
            string field = rule is not null && rule.Field.Length > 0 ? rule.Field : blank;
            string? v = person.Values.FirstOrDefault(kv => string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase)).Value;
            values[blank] = !string.IsNullOrWhiteSpace(v) ? v
                : rule is not null ? rule.Fallback
                : NameBlanks.Contains(blank, StringComparer.OrdinalIgnoreCase) ? NameFallback : string.Empty;
        }
        string result = text;
        foreach (KeyValuePair<string, string> kv in values)
        {
            result = result.Replace("{" + kv.Key + "}", kv.Value, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>
    /// Send one each: checks the daily limit, then keeps one copy per person
    /// in Scheduled, a few seconds apart (gradually for larger merges).
    /// Returns the error, or how many copies were made.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="request">The compose form, attachments already read; its To holds the people.</param>
    /// <param name="csv">An uploaded list, or null.</param>
    /// <param name="start">When the first copy goes.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<(string? Error, int Count)> SendOneEachAsync(Guid mailboxId, ComposeRequest request, string? csv, DateTimeOffset start, CancellationToken ct = default) =>
        this.SendOneEachAsync(mailboxId, request, csv, start, map: null, ct: ct);

    /// <summary>Send one each, with each blank filled as the person matched it (rc.15, item 64).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="request">The compose form.</param>
    /// <param name="csv">An uploaded list, or null.</param>
    /// <param name="start">When the first copy goes.</param>
    /// <param name="map">How each blank is filled; null for the defaults.</param>
    /// <param name="clock">The person's clock: the daily limit counts their own day (DES-11 F5); null for Anjal's default zone.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// DES-11 F5 (owner, 10 Oct 2026, "b"): Cc and Bcc are not put on the copies - each person gets
    /// only their own. One summary copy, listing who it went to and showing the message with its
    /// blanks, goes to <see cref="ComposeRequest.MergeSummaryTo"/> (the sender, unless changed).
    /// </remarks>
    public async Task<(string? Error, int Count)> SendOneEachAsync(Guid mailboxId, ComposeRequest request, string? csv, DateTimeOffset start, IReadOnlyDictionary<string, MergeBlank>? map, ZonedClock? clock = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        clock ??= ZonedClock.Default;
        int limit = await this.MergeLimitForAsync(mailboxId, ct).ConfigureAwait(false);
        IReadOnlyList<MergePerson> people = await this.MergePeopleAsync(mailboxId, request.To, csv, ct).ConfigureAwait(false);
        if (people.Count == 0)
        {
            return ("Send one each needs at least one valid address in To, or a list with an address column.", 0);
        }
        string today = clock.Local(DateTimeOffset.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Dictionary<string, int> log = await this.ReadDocumentAsync<Dictionary<string, int>>(mailboxId, MergeLogKind, ct).ConfigureAwait(false) ?? new Dictionary<string, int>();
        int usedToday = log.TryGetValue(today, out int n) ? n : 0;
        if (usedToday + people.Count > limit)
        {
            return ($"Send one each allows {limit} messages a day; {usedToday} have gone today, and this would add {people.Count}.", 0);
        }
        // About one every few seconds; larger merges more slowly.
        TimeSpan gap = people.Count <= 25 ? TimeSpan.FromSeconds(5) : people.Count <= 100 ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(30);
        int made = 0;
        foreach (MergePerson person in people)
        {
            var copy = new ComposeRequest
            {
                To = person.Address,
                Subject = MergeFill(request.Subject, person, map),
                Body = MergeFill(request.Body, person, map),
                BodyHtml = request.BodyHtml.Length == 0 ? string.Empty : MergeFill(request.BodyHtml, EscapedValues(person), EscapedMap(map)),
                InReplyTo = string.Empty,
            };
            foreach ((string FileName, string ContentType, byte[] Bytes) file in request.Attachments)
            {
                copy.Attachments.Add(file);
            }
            (string? error, Guid? _) = await this.HoldAsync(mailboxId, copy, start + (gap * made), ct).ConfigureAwait(false);
            if (error is not null)
            {
                return made == 0 ? (error, 0) : ($"{made} of {people.Count} were kept for sending; then: {error}", made);
            }
            made++;
        }
        if (request.MergeSummaryTo.Trim().Length > 0)
        {
            var summary = new ComposeRequest
            {
                To = request.MergeSummaryTo.Trim(),
                Subject = "Sent one each: " + request.Subject,
                Body = MergeSummaryText(request, people, made, start, clock),
            };
            (string? summaryError, Guid? _) = await this.HoldAsync(mailboxId, summary, start, ct).ConfigureAwait(false);
            if (summaryError is not null)
            {
                return ($"All {made} copies were kept for sending, but the summary copy could not be: {summaryError}", made);
            }
        }
        log = log.Where(kv => string.CompareOrdinal(kv.Key, today) >= 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        log[today] = usedToday + made;
        await this.WriteDocumentAsync(mailboxId, MergeLogKind, log, ct).ConfigureAwait(false);
        if (request.DraftId is Guid draft)
        {
            await this.DiscardDraftAsync(mailboxId, draft, ct).ConfigureAwait(false);
        }
        return (null, made);
    }

    /// <summary>The summary copy's text: who each copy went to, and the message with its blanks (DES-11 F5).</summary>
    /// <param name="request">The compose form.</param>
    /// <param name="people">The people, in order.</param>
    /// <param name="made">How many copies were kept for sending.</param>
    /// <param name="start">When the first copy goes.</param>
    /// <param name="clock">The person's clock.</param>
    /// <returns>Plain text.</returns>
    public static string MergeSummaryText(ComposeRequest request, IReadOnlyList<MergePerson> people, int made, DateTimeOffset start, ZonedClock clock)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(clock);
        var sb = new System.Text.StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"This message was sent with Send one each to {made} people, each with their own copy, from {clock.Time(start)} {clock.Date(start)}. No one saw anyone else's address, and no copy carried Cc or Bcc.\r\n\r\n");
        sb.Append("Sent to:\r\n");
        foreach (MergePerson p in people.Take(made))
        {
            sb.Append("- ").Append(p.Address).Append("\r\n");
        }
        sb.Append("\r\nThe message, with its blanks:\r\n\r\nSubject: ").Append(request.Subject).Append("\r\n\r\n").Append(request.Body).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>The merge values for people's addresses, for the compose preview ("3 of 24").</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="to">The To line.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<IReadOnlyList<MergePerson>> MergePreviewAsync(Guid mailboxId, string to, CancellationToken ct = default) =>
        this.MergePeopleAsync(mailboxId, to, null, ct);

    /// <summary>
    /// True when a person may send one each (owner, 8 Oct 2026): only someone their organisation's
    /// administrator has given it to.
    /// </summary>
    /// <param name="mailboxId">The person's mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether Send one each is theirs to use.</returns>
    public async Task<bool> MayMergeAsync(Guid mailboxId, CancellationToken ct = default)
    {
        if (await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false) is not { } context)
        {
            return false;
        }
        MergeAccess? access = await this.ReadTenantDocumentAsync<MergeAccess>(context.Tenant.Id, MergeAccessKind, ct).ConfigureAwait(false);
        return access is not null && access.People.Contains(mailboxId);
    }

    /// <summary>The people of an organisation who may send one each.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Their mailbox ids.</returns>
    public async Task<IReadOnlyList<Guid>> MergePeopleOfAsync(Guid tenantId, CancellationToken ct = default) =>
        (await this.ReadTenantDocumentAsync<MergeAccess>(tenantId, MergeAccessKind, ct).ConfigureAwait(false))?.People ?? new List<Guid>();

    /// <summary>Give Send one each to a person of the organisation, or take it away.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="personId">The person, who must belong to it.</param>
    /// <param name="allowed">True to give it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>An error, or null.</returns>
    public async Task<string?> SetMergeAccessAsync(Anjal.Store.TenantRow tenant, Guid personId, bool allowed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (await this.GetContextAsync(personId, ct).ConfigureAwait(false) is not { } context || context.Tenant.Id != tenant.Id)
        {
            return "That person is not in this organisation.";
        }
        MergeAccess access = await this.ReadTenantDocumentAsync<MergeAccess>(tenant.Id, MergeAccessKind, ct).ConfigureAwait(false) ?? new MergeAccess();
        access.People.Remove(personId);
        if (allowed)
        {
            access.People.Add(personId);
        }
        await this.WriteTenantDocumentAsync(tenant.Id, MergeAccessKind, access, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>rc.15 (item 64): the organisation's daily limit for Send one each, per person.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<int> MergeLimitOfAsync(Guid tenantId, CancellationToken ct = default)
    {
        MergeLimitSetting? set = await this.ReadTenantDocumentAsync<MergeLimitSetting>(tenantId, MergeLimitKind, ct).ConfigureAwait(false);
        return Math.Clamp(set?.Limit ?? DefaultDailyMergeLimit, 1, MergeCeiling);
    }

    /// <summary>rc.15 (item 64): set the organisation's daily limit, within Anjal's ceiling.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="limit">Messages per person per day.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<int> SaveMergeLimitAsync(Guid tenantId, int limit, CancellationToken ct = default)
    {
        int kept = Math.Clamp(limit, 1, MergeCeiling);
        await this.WriteTenantDocumentAsync(tenantId, MergeLimitKind, new MergeLimitSetting { Limit = kept }, ct).ConfigureAwait(false);
        return kept;
    }

    /// <summary>The daily merge limit that applies to a mailbox: its organisation's.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<int> MergeLimitForAsync(Guid mailboxId, CancellationToken ct = default) =>
        await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false) is { } context
            ? await this.MergeLimitOfAsync(context.Tenant.Id, ct).ConfigureAwait(false)
            : DefaultDailyMergeLimit;

    // The fallbacks go into HTML escaped, like the values.
    private static Dictionary<string, MergeBlank>? EscapedMap(IReadOnlyDictionary<string, MergeBlank>? map) =>
        map?.ToDictionary(kv => kv.Key, kv => new MergeBlank(kv.Value.Field, HtmlSanitizer.Escape(kv.Value.Fallback)), StringComparer.Ordinal);

    // In HTML the values are escaped, so a name can never become markup.
    private static MergePerson EscapedValues(MergePerson person) =>
        new(person.Address, person.Values.ToDictionary(kv => kv.Key, kv => HtmlSanitizer.Escape(kv.Value), StringComparer.OrdinalIgnoreCase));
}
