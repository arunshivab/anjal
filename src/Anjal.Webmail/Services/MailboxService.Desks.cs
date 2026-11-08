using System.Collections.Concurrent;
using System.Globalization;
using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>A desk conversation's state as the list and the envelope show it (rc.27, DES-17 §2.2).</summary>
/// <param name="ItemId">Its item, or null when nobody has acted on it yet.</param>
/// <param name="State">"unassigned", "open", "waiting" or "done".</param>
/// <param name="AssigneeId">Who has it.</param>
/// <param name="Assignee">Their name.</param>
/// <param name="Due">When a reply is due, while one is owed and the desk has a target.</param>
/// <param name="Overdue">True when that time has passed.</param>
/// <param name="Replying">The name of a member writing a reply now, or null.</param>
public sealed record DeskState(Guid? ItemId, string State, Guid? AssigneeId, string Assignee, DateTimeOffset? Due, bool Overdue, string? Replying)
{
    /// <summary>The tabs: Not assigned, Mine, Open, Done (and All).</summary>
    public static readonly IReadOnlyList<string> Tabs = new[] { "all", "unassigned", "mine", "open", "done" };

    /// <summary>True when this state belongs under a tab for this person.</summary>
    /// <param name="tab">The tab.</param>
    /// <param name="me">The person.</param>
    /// <returns>True when it is listed there.</returns>
    public bool In(string tab, Guid me) => tab switch
    {
        "unassigned" => this.State != "done" && this.AssigneeId is null,
        "mine" => this.State != "done" && this.AssigneeId == me,
        "open" => this.State is "open" or "waiting",
        "done" => this.State == "done",
        _ => true,
    };
}

/// <summary>A member of a desk (rc.27).</summary>
/// <param name="Id">Their mailbox.</param>
/// <param name="Name">Their name.</param>
/// <param name="Right">read, send or manage.</param>
public sealed record DeskMember(Guid Id, string Name, string Right)
{
    /// <summary>True when they may reply, and so take or be given mail (D-304).</summary>
    public bool MaySend => this.Right is "send" or "manage";
}

/// <summary>A line of a desk item's history, in words' parts (rc.27).</summary>
/// <param name="At">When.</param>
/// <param name="Kind">What happened (<see cref="DeskEventRow.Kind"/>).</param>
/// <param name="Actor">Who did it, or empty for Anjal.</param>
/// <param name="Other">The other person (assigned to, returned by), or empty.</param>
public sealed record DeskLine(DateTimeOffset At, string Kind, string Actor, string Other);

/// <summary>A note for the desk, opened (rc.27).</summary>
/// <param name="Id">Its id.</param>
/// <param name="At">When.</param>
/// <param name="WriterId">Who wrote it.</param>
/// <param name="Writer">Their name.</param>
/// <param name="Text">The text, or null when withdrawn or not readable here.</param>
/// <param name="WithdrawnAt">When it was withdrawn.</param>
public sealed record DeskNote(Guid Id, DateTimeOffset At, Guid? WriterId, string Writer, string? Text, DateTimeOffset? WithdrawnAt);

/// <summary>What the envelope's Desk block shows for one mail (rc.27, DES-17 §2.4).</summary>
public sealed class DeskPanel
{
    /// <summary>The desk.</summary>
    public Guid DeskId { get; init; }

    /// <summary>The state.</summary>
    public DeskState State { get; init; } = new(null, "unassigned", null, string.Empty, null, false, null);

    /// <summary>The members, for "Assigned to" (only those who may send can be chosen).</summary>
    public IReadOnlyList<DeskMember> Members { get; init; } = Array.Empty<DeskMember>();

    /// <summary>The history, newest first.</summary>
    public IReadOnlyList<DeskLine> History { get; init; } = Array.Empty<DeskLine>();

    /// <summary>The notes, oldest first.</summary>
    public IReadOnlyList<DeskNote> Notes { get; init; } = Array.Empty<DeskNote>();

    /// <summary>The person may assign.</summary>
    public bool CanAssign { get; init; }

    /// <summary>The person may take it.</summary>
    public bool CanTake { get; init; }

    /// <summary>The person may mark it done or reopen it.</summary>
    public bool CanFinish { get; init; }

    /// <summary>The person's own id.</summary>
    public Guid Me { get; init; }
}

/// <summary>A desk item in the person's Today (rc.27, DES-17 §2.9).</summary>
/// <param name="DeskId">The desk.</param>
/// <param name="Desk">Its name.</param>
/// <param name="Message">The conversation's newest mail to the desk.</param>
/// <param name="State">Its state.</param>
public sealed record AssignedRow(Guid DeskId, string Desk, MessageRow Message, DeskState State);

/// <summary>A desk's figures for the console (rc.27, DES-17 §2.6).</summary>
/// <param name="Open">Replies owed.</param>
/// <param name="Overdue">Of which past their time.</param>
/// <param name="UsualReplyMinutes">The median wait for the desk's reply over 30 days, in working minutes; null when none.</param>
public sealed record DeskFigures(int Open, int Overdue, int? UsualReplyMinutes);

/// <summary>
/// Shared desks (rc.27, item 70; brief 10d; DES-17 §2): a shared mailbox used by a team. Mail is
/// assigned to a member, replied to from the desk, and marked done; a reply is due within the desk's
/// target in its working week; members see who is replying, and leave notes for each other that are
/// never sent. The conversation is the unit, found by a keyed mark of its subject.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The organisation document holding its working week (D-315).</summary>
    public const string WorkWeekKind = "work-week";

    /// <summary>How long "is replying" lasts without being renewed.</summary>
    public static readonly TimeSpan ReplyingFor = TimeSpan.FromSeconds(90);

    /// <summary>The longest note.</summary>
    public const int DeskNoteMax = 2000;

    private readonly ConcurrentDictionary<(Guid Desk, string Mark), (Guid Person, DateTimeOffset Since, DateTimeOffset Seen)> replying = new();

    private readonly ConcurrentDictionary<Guid, (long Stamp, string Open)> deskAlerts = new();

    private IDeskStore? Desks => this.store as IDeskStore;

    /// <summary>The organisation's working week (D-315).</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The week.</returns>
    public async Task<WorkWeek> WorkWeekAsync(Guid tenantId, CancellationToken ct = default) =>
        await this.ReadTenantDocumentAsync<WorkWeek>(tenantId, WorkWeekKind, ct).ConfigureAwait(false) is WorkWeek w && w.Problem() is null ? w : new WorkWeek();

    /// <summary>Set the organisation's working week. Returns the error, or null.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="week">The week.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> SetWorkWeekAsync(TenantRow tenant, WorkWeek week, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(week);
        if (week.Problem() is string problem)
        {
            return problem;
        }
        week.TimeZone = ZonedClock.For(week.TimeZone, null).ZoneId;
        week.Days = week.Days.Distinct().OrderBy(d => d).ToList();
        await this.WriteTenantDocumentAsync(tenant.Id, WorkWeekKind, week, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Set a desk's own settings (D-304, D-315). Returns the error, or null.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="deskId">The desk.</param>
    /// <param name="assignBy">"any" or "send".</param>
    /// <param name="replyTarget">One of <see cref="SharedMailbox.ReplyTargets"/>.</param>
    /// <param name="ownWeek">The desk's own week, or null to follow the organisation's.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> SetDeskSettingsAsync(TenantRow tenant, Guid deskId, string assignBy, string replyTarget, WorkWeek? ownWeek, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (assignBy is not ("any" or "send") || !SharedMailbox.ReplyTargets.Contains(replyTarget))
        {
            return "Choose who may assign and a reply time from the lists.";
        }
        if (ownWeek?.Problem() is string problem)
        {
            return problem;
        }
        string? error = null;
        bool written = await this.store.ChangeTenantDocumentAsync(tenant.Id, SharedKind, json =>
        {
            Dictionary<Guid, SharedMailbox> all = string.IsNullOrWhiteSpace(json) ? new() : System.Text.Json.JsonSerializer.Deserialize<Dictionary<Guid, SharedMailbox>>(json, DocumentJson) ?? new();
            if (!all.TryGetValue(deskId, out SharedMailbox? info))
            {
                error = "That is not a shared mailbox of this organisation.";
                return null;
            }
            info.AssignBy = assignBy;
            info.ReplyTarget = replyTarget;
            if (ownWeek is not null)
            {
                ownWeek.TimeZone = ZonedClock.For(ownWeek.TimeZone, null).ZoneId;
                ownWeek.Days = ownWeek.Days.Distinct().OrderBy(d => d).ToList();
            }
            info.OwnWeek = ownWeek;
            return System.Text.Json.JsonSerializer.Serialize(all, DocumentJson);
        }, ct).ConfigureAwait(false);
        if (written)
        {
            this.tenantDocs.TryRemove((tenant.Id, SharedKind), out _);
        }
        return error;
    }

    /// <summary>
    /// The working minutes between two moments in a working week (DES-17 §2.6): only the hours of
    /// working days count, so a mail answered first thing on Monday waited minutes, not a weekend.
    /// </summary>
    /// <param name="start">From.</param>
    /// <param name="end">To.</param>
    /// <param name="week">The week.</param>
    /// <returns>The minutes; 0 when the week cannot be read or the end is not after the start.</returns>
    public static int WorkingMinutes(DateTimeOffset start, DateTimeOffset end, WorkWeek week)
    {
        ArgumentNullException.ThrowIfNull(week);
        if (end <= start || week.Problem() is not null)
        {
            return 0;
        }
        TimeZoneInfo zone = ZonedClock.For(week.TimeZone, null).Zone;
        TimeSpan from = TimeOnly.ParseExact(week.From, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan();
        TimeSpan to = TimeOnly.ParseExact(week.To, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan();
        var days = week.Days.ToHashSet();
        DateTime a = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        DateTime b = TimeZoneInfo.ConvertTime(end, zone).DateTime;
        double minutes = 0;
        for (DateTime day = a.Date; day <= b.Date && day < a.Date.AddDays(400); day = day.AddDays(1))
        {
            if (!days.Contains((int)day.DayOfWeek))
            {
                continue;
            }
            DateTime open = day + from > a ? day + from : a;
            DateTime close = day + to < b ? day + to : b;
            if (close > open)
            {
                minutes += (close - open).TotalMinutes;
            }
        }
        return (int)Math.Round(minutes);
    }

    /// <summary>
    /// When a reply is due (D-304, D-315): the target counted in working time from the moment the
    /// oldest unanswered mail arrived. Hours walk through the working day; days keep the time of day
    /// on the next working day. Mail that arrives outside hours counts from the next opening.
    /// </summary>
    /// <param name="since">When the reply became owed.</param>
    /// <param name="target">"none", "2h", "4h", "1d" or "2d".</param>
    /// <param name="week">The working week.</param>
    /// <returns>When it is due, or null without a target.</returns>
    public static DateTimeOffset? DueAt(DateTimeOffset since, string target, WorkWeek week)
    {
        ArgumentNullException.ThrowIfNull(week);
        if (target is not ("2h" or "4h" or "1d" or "2d") || week.Problem() is not null)
        {
            return null;
        }
        TimeZoneInfo zone = ZonedClock.For(week.TimeZone, null).Zone;
        TimeSpan from = TimeOnly.ParseExact(week.From, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan();
        TimeSpan to = TimeOnly.ParseExact(week.To, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan();
        var days = week.Days.ToHashSet();
        DateTime t = TimeZoneInfo.ConvertTime(since, zone).DateTime;
        // To the next working moment.
        for (int i = 0; i < 15; i++)
        {
            if (days.Contains((int)t.DayOfWeek) && t.TimeOfDay < to)
            {
                if (t.TimeOfDay < from)
                {
                    t = t.Date + from;
                }
                break;
            }
            t = t.Date.AddDays(1) + from;
        }
        if (target is "2h" or "4h")
        {
            TimeSpan left = TimeSpan.FromHours(target == "2h" ? 2 : 4);
            for (int i = 0; i < 30 && left > TimeSpan.Zero; i++)
            {
                TimeSpan today = to - t.TimeOfDay;
                if (left <= today)
                {
                    t += left;
                    left = TimeSpan.Zero;
                    break;
                }
                left -= today;
                t = NextWorkingDay(t.Date, days) + from;
            }
        }
        else
        {
            TimeSpan timeOfDay = t.TimeOfDay;
            for (int n = target == "1d" ? 1 : 2; n > 0; n--)
            {
                t = NextWorkingDay(t.Date, days) + timeOfDay;
            }
        }
        DateTime local = DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static DateTime NextWorkingDay(DateTime day, HashSet<int> days)
    {
        DateTime d = day.AddDays(1);
        for (int i = 0; i < 7 && !days.Contains((int)d.DayOfWeek); i++)
        {
            d = d.AddDays(1);
        }
        return d;
    }

    /// <summary>The members of a desk, with their names.</summary>
    /// <param name="info">The desk's settings.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The members, by name.</returns>
    public async Task<IReadOnlyList<DeskMember>> DeskMembersAsync(SharedMailbox info, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        var list = new List<DeskMember>();
        foreach ((Guid id, string right) in info.Members)
        {
            if (await this.store.GetMailboxByIdAsync(id, ct).ConfigureAwait(false) is MailboxRow m && m.Enabled)
            {
                list.Add(new DeskMember(id, DeskNameOf(m), right));
            }
        }
        return list.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string DeskNameOf(MailboxRow m) => m.DisplayName.Trim().Length > 0 ? m.DisplayName.Trim() : m.Address;

    /// <summary>The week a desk counts in: its own, or its organisation's.</summary>
    /// <param name="desk">The desk.</param>
    /// <param name="info">Its settings.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The week.</returns>
    public async Task<WorkWeek> WeekOfDeskAsync(MailboxRow desk, SharedMailbox info, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(desk);
        ArgumentNullException.ThrowIfNull(info);
        return info.OwnWeek is WorkWeek own && own.Problem() is null ? own : await this.WorkWeekAsync(desk.TenantId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether this request can read the desk's mail (review, 28 Oct): a desk with keys whose data key
    /// is not in the request's keyring shows its subjects as empty, and an empty subject must not be
    /// taken for an untitled conversation.
    /// </summary>
    /// <param name="desk">The desk.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when its subjects are readable here.</returns>
    public async Task<bool> DeskReadableAsync(MailboxRow desk, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(desk);
        return MailKeyring.Current.DataKey(desk.Id) is not null || this.KeyManager is not MailKeyManager km
            || await km.Store.GetMailboxKeysAsync(desk.Id, ct).ConfigureAwait(false) is null;
    }

    /// <summary>A conversation's mark on a desk.</summary>
    /// <param name="desk">The desk.</param>
    /// <param name="m">Any mail of the conversation.</param>
    /// <returns>The mark, or null when the subject cannot be read here (the desk's key is not in this request).</returns>
    public string? DeskMark(MailboxRow desk, MessageRow m)
    {
        ArgumentNullException.ThrowIfNull(desk);
        ArgumentNullException.ThrowIfNull(m);
        return MailEnvelope.IsField(m.Subject) ? null : DeskMarks.ThreadMark(desk.Id, m.Subject, DeskMarks.OtherParty(desk.Address, m.FromHeader, m.ToHeader), m.Id, this.AddressPepper);
    }

    private DeskState StateOf(DeskItemRow? item, DateTimeOffset? legacyPending, SharedMailbox info, WorkWeek week, Dictionary<Guid, string> names, Guid deskId, string mark, DateTimeOffset now)
    {
        Guid? assignee = item?.AssigneeId is Guid a && info.Members.TryGetValue(a, out string? right) && right is "send" or "manage" ? a : null;
        DateTimeOffset? pending = item is null ? legacyPending : item.PendingSince;
        string state = item?.DoneAt is not null ? "done"
            : pending is null ? "waiting"
            : assignee is not null ? "open"
            : "unassigned";
        DateTimeOffset? due = state is "open" or "unassigned" && pending is DateTimeOffset p ? DueAt(p, info.ReplyTarget, week) : null;
        string? replyingName = this.replying.TryGetValue((deskId, mark), out (Guid Person, DateTimeOffset Since, DateTimeOffset Seen) r) && now - r.Seen < ReplyingFor
            ? (names.TryGetValue(r.Person, out string? n) ? n : string.Empty)
            : null;
        // A done item keeps the name of whoever did it, even when they have left the desk.
        Guid? named = assignee ?? (state == "done" ? item?.AssigneeId : null);
        return new DeskState(item?.Id, state, assignee, named is Guid id && names.TryGetValue(id, out string? who) ? who : string.Empty, due, due is DateTimeOffset d && d < now, replyingName);
    }

    /// <summary>The desk states of a list's rows (rc.27): its tabs and marks. Empty when the mailbox is not a desk.</summary>
    /// <param name="deskId">The mailbox listed.</param>
    /// <param name="rows">The rows (each leads its conversation).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Each row's state, by message id.</returns>
    public async Task<IReadOnlyDictionary<Guid, DeskState>> DeskStatesAsync(Guid deskId, IEnumerable<MessageRow> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var result = new Dictionary<Guid, DeskState>();
        if (this.Desks is not IDeskStore desks || await this.store.GetMailboxByIdAsync(deskId, ct).ConfigureAwait(false) is not MailboxRow desk
            || await this.SharedInfoAsync(desk, ct).ConfigureAwait(false) is not SharedMailbox info)
        {
            return result;
        }
        if (!await this.DeskReadableAsync(desk, ct).ConfigureAwait(false))
        {
            return result;
        }
        WorkWeek week = await this.WeekOfDeskAsync(desk, info, ct).ConfigureAwait(false);
        Dictionary<string, DeskItemRow> items = (await desks.ListDeskItemsAsync(deskId, ct).ConfigureAwait(false)).GroupBy(i => i.ThreadKey).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Dictionary<Guid, string> names = (await this.DeskMembersAsync(info, ct).ConfigureAwait(false)).ToDictionary(m => m.Id, m => m.Name);
        // Done items keep the name of whoever did them, members or not (DES-17 §2.14).
        foreach (Guid gone in items.Values.Where(i => i.DoneAt is not null && i.AssigneeId is Guid a && !names.ContainsKey(a)).Select(i => i.AssigneeId!.Value).Distinct().ToList())
        {
            names[gone] = await this.PersonNameAsync(gone, ct).ConfigureAwait(false);
        }
        IReadOnlyDictionary<Guid, string> folders = await this.FolderNamesAsync(deskId, ct).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (MessageRow m in rows)
        {
            if (this.DeskMark(desk, m) is not string mark)
            {
                continue;
            }
            items.TryGetValue(mark, out DeskItemRow? item);
            // A conversation from before rc.27 has no item: owed since its mail arrived, if it came in.
            DateTimeOffset? legacy = folders.TryGetValue(m.FolderId, out string? f) && f == FolderRow.Inbox ? m.ReceivedAt : null;
            result[m.Id] = this.StateOf(item, legacy, info, week, names, deskId, mark, now);
        }
        return result;
    }

    /// <summary>The Desk block for one mail, or null when the mailbox is not a desk or the person not its member.</summary>
    /// <param name="deskId">The desk.</param>
    /// <param name="personId">The person reading.</param>
    /// <param name="message">The mail.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The panel, or null.</returns>
    public async Task<DeskPanel?> DeskPanelAsync(Guid deskId, Guid personId, MessageRow message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (this.Desks is not IDeskStore desks || await this.store.GetMailboxByIdAsync(deskId, ct).ConfigureAwait(false) is not MailboxRow desk
            || await this.SharedInfoAsync(desk, ct).ConfigureAwait(false) is not SharedMailbox info
            || !info.Members.TryGetValue(personId, out string? right))
        {
            return null;
        }
        if (!await this.DeskReadableAsync(desk, ct).ConfigureAwait(false))
        {
            return null;
        }
        WorkWeek week = await this.WeekOfDeskAsync(desk, info, ct).ConfigureAwait(false);
        IReadOnlyList<DeskMember> members = await this.DeskMembersAsync(info, ct).ConfigureAwait(false);
        var names = members.ToDictionary(m => m.Id, m => m.Name);
        if (this.DeskMark(desk, message) is not string mark)
        {
            return null;
        }
        DeskItemRow? item = (await desks.ListDeskItemsAsync(deskId, ct).ConfigureAwait(false)).FirstOrDefault(i => i.ThreadKey == mark);
        if (item is { DoneAt: not null, AssigneeId: Guid doneBy } && !names.ContainsKey(doneBy))
        {
            names[doneBy] = await this.PersonNameAsync(doneBy, ct).ConfigureAwait(false);
        }
        IReadOnlyDictionary<Guid, string> folders = await this.FolderNamesAsync(deskId, ct).ConfigureAwait(false);
        DateTimeOffset? legacy = folders.TryGetValue(message.FolderId, out string? f) && f == FolderRow.Inbox ? message.ReceivedAt : null;
        DeskState state = this.StateOf(item, legacy, info, week, names, deskId, mark, DateTimeOffset.UtcNow);
        var history = new List<DeskLine>();
        var notes = new List<DeskNote>();
        if (item is not null)
        {
            foreach (DeskEventRow e in await desks.ListDeskEventsAsync(item.Id, ct).ConfigureAwait(false))
            {
                string actor = e.ActorId is Guid a ? (names.TryGetValue(a, out string? an) ? an : await this.PersonNameAsync(a, ct).ConfigureAwait(false)) : string.Empty;
                string other = Guid.TryParse(e.Detail, out Guid o) && e.Kind is "assigned" or "returned" ? (names.TryGetValue(o, out string? on) ? on : await this.PersonNameAsync(o, ct).ConfigureAwait(false)) : string.Empty;
                history.Add(new DeskLine(e.At, e.Kind, actor, other));
            }
            history.Reverse();
            foreach (DeskNoteRow n in await desks.ListDeskNotesAsync(item.Id, ct).ConfigureAwait(false))
            {
                string writer = n.WriterId is Guid w ? (names.TryGetValue(w, out string? wn) ? wn : await this.PersonNameAsync(w, ct).ConfigureAwait(false)) : string.Empty;
                string? text = n.WithdrawnAt is null ? OpenNote(n.Body) : null;
                notes.Add(new DeskNote(n.Id, n.At, n.WriterId, writer, text, n.WithdrawnAt));
            }
        }
        bool maySend = right is "send" or "manage";
        return new DeskPanel
        {
            DeskId = deskId,
            State = state,
            Members = members,
            History = history,
            Notes = notes,
            CanAssign = info.AssignBy == "any" || maySend,
            CanTake = maySend && state.AssigneeId != personId && state.State != "done",
            CanFinish = state.AssigneeId is null || state.AssigneeId == personId || right == "manage",
            Me = personId,
        };
    }

    private async Task<string> PersonNameAsync(Guid id, CancellationToken ct) =>
        await this.store.GetMailboxByIdAsync(id, ct).ConfigureAwait(false) is MailboxRow m ? DeskNameOf(m) : "—";

    private static string? OpenNote(string body)
    {
        if (!MailEnvelope.IsField(body))
        {
            return body;
        }
        try
        {
            return MailEnvelope.OpenField(body, MailKeyring.Current);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    // A note is sealed like the desk's mail: with the desk's data key when this request holds it, else
    // to the desk's public key; plain only for a desk that is not encrypted.
    private async Task<string> SealNoteAsync(Guid deskId, string text, CancellationToken ct)
    {
        if (MailKeyring.Current.DataKey(deskId) is byte[] dataKey)
        {
            return MailEnvelope.FieldWithKey(text, dataKey);
        }
        if (this.KeyManager is MailKeyManager km && await km.Store.GetMailboxKeysAsync(deskId, ct).ConfigureAwait(false) is MailboxKeyRow keys)
        {
            return MailEnvelope.FieldToPublicKey(text, keys.PublicKey);
        }
        return text;
    }

    /// <summary>
    /// Act on a desk's mail (DES-17 §2.5): "assign" (to a member, or nobody), "take", "done",
    /// "reopen", "note" (with its text) or "withdraw" (a note of one's own). Returns the error, or null.
    /// </summary>
    /// <param name="deskId">The desk.</param>
    /// <param name="personId">Who acts.</param>
    /// <param name="messageId">A mail of the conversation.</param>
    /// <param name="action">The action.</param>
    /// <param name="to">For assign: the member, or null for nobody.</param>
    /// <param name="text">For a note: its text; for withdraw: the note's id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> DeskActAsync(Guid deskId, Guid personId, Guid messageId, string action, Guid? to, string? text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (this.Desks is not IDeskStore desks || await this.store.GetMailboxByIdAsync(deskId, ct).ConfigureAwait(false) is not MailboxRow desk
            || await this.SharedInfoAsync(desk, ct).ConfigureAwait(false) is not SharedMailbox info
            || !info.Members.TryGetValue(personId, out string? right))
        {
            return "That desk is not open to you.";
        }
        MessageRow? m = await this.GetOwnedRowAsync(deskId, messageId, ct).ConfigureAwait(false);
        if (m is null)
        {
            return "That mail is no longer on the desk.";
        }
        bool maySend = right is "send" or "manage";
        if (!await this.DeskReadableAsync(desk, ct).ConfigureAwait(false) || this.DeskMark(desk, m) is not string mark)
        {
            return "This mail cannot be read here. Sign in with your password and try again.";
        }
        IReadOnlyDictionary<Guid, string> folders = await this.FolderNamesAsync(deskId, ct).ConfigureAwait(false);
        DateTimeOffset? pending = folders.TryGetValue(m.FolderId, out string? f) && f == FolderRow.Inbox ? m.ReceivedAt : null;
        DeskItemRow item = await desks.GetOrAddDeskItemAsync(deskId, mark, m.Id, pending, ct).ConfigureAwait(false);
        Guid? holder = item.AssigneeId is Guid h && info.Members.TryGetValue(h, out string? hr) && hr is "send" or "manage" ? h : null;
        string me = await this.PersonNameAsync(personId, ct).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        switch (action)
        {
            case "assign":
                if (info.AssignBy == "send" && !maySend)
                {
                    return "On this desk only members who may send can assign mail.";
                }
                if (to is Guid target && !(info.Members.TryGetValue(target, out string? tr) && tr is "send" or "manage"))
                {
                    return "Mail can be given only to a member who may send from the desk.";
                }
                if (to == holder)
                {
                    return null;
                }
                await desks.SetDeskAssigneeAsync(item.Id, to, ct).ConfigureAwait(false);
                await desks.AddDeskEventAsync(new DeskEventRow { ItemId = item.Id, At = now, ActorId = personId, Kind = to is null ? "unassigned" : "assigned", Detail = to?.ToString() ?? string.Empty }, ct).ConfigureAwait(false);
                if (to is Guid given && given != personId)
                {
                    this.AlertDesk(given, deskId, m.Id);
                }
                await this.DeskAuditAsync(me, to is null ? "desk.unassigned" : "desk.assigned", desk, to is Guid t2 ? await this.PersonNameAsync(t2, ct).ConfigureAwait(false) : string.Empty, ct).ConfigureAwait(false);
                return null;
            case "take":
                if (!maySend)
                {
                    return "Only members who may send from the desk can take mail.";
                }
                if (holder == personId)
                {
                    return null;
                }
                await desks.SetDeskAssigneeAsync(item.Id, personId, ct).ConfigureAwait(false);
                await desks.AddDeskEventAsync(new DeskEventRow { ItemId = item.Id, At = now, ActorId = personId, Kind = "taken" }, ct).ConfigureAwait(false);
                await this.DeskAuditAsync(me, "desk.taken", desk, string.Empty, ct).ConfigureAwait(false);
                return null;
            case "done" or "reopen":
                if (!(holder is null || holder == personId || right == "manage"))
                {
                    return "This mail is someone else's. Ask them, or a manager of the desk.";
                }
                bool done = action == "done";
                if (done == item.DoneAt is not null)
                {
                    return null;
                }
                await desks.SetDeskDoneAsync(item.Id, done ? now : null, ct).ConfigureAwait(false);
                await desks.AddDeskEventAsync(new DeskEventRow { ItemId = item.Id, At = now, ActorId = personId, Kind = done ? "done" : "reopened" }, ct).ConfigureAwait(false);
                await this.DeskAuditAsync(me, done ? "desk.done" : "desk.reopened", desk, string.Empty, ct).ConfigureAwait(false);
                return null;
            case "note":
                string body = (text ?? string.Empty).Trim();
                if (body.Length == 0)
                {
                    return "Write the note first.";
                }
                if (body.Length > DeskNoteMax)
                {
                    return $"A note is at most {DeskNoteMax} characters.";
                }
                IReadOnlyList<DeskMember> members = await this.DeskMembersAsync(info, ct).ConfigureAwait(false);
                List<Guid> mentioned = Mentioned(body, members).Where(id => id != personId).ToList();
                var note = new DeskNoteRow { ItemId = item.Id, At = now, WriterId = personId, Body = await this.SealNoteAsync(deskId, body, ct).ConfigureAwait(false), Mentions = string.Join(',', mentioned) };
                await desks.AddDeskNoteAsync(note, ct).ConfigureAwait(false);
                await desks.AddDeskEventAsync(new DeskEventRow { ItemId = item.Id, At = now, ActorId = personId, Kind = "note", Detail = note.Id.ToString() }, ct).ConfigureAwait(false);
                foreach (Guid who in mentioned)
                {
                    this.AlertDesk(who, deskId, m.Id);
                }
                await this.DeskAuditAsync(me, "desk.note.added", desk, mentioned.Count == 0 ? string.Empty : mentioned.Count.ToString(CultureInfo.InvariantCulture) + " mentioned", ct).ConfigureAwait(false);
                return null;
            case "withdraw":
                if (!Guid.TryParse(text, out Guid noteId) || !await desks.WithdrawDeskNoteAsync(noteId, item.Id, personId, now, ct).ConfigureAwait(false))
                {
                    return "Only the person who wrote a note can withdraw it.";
                }
                await desks.AddDeskEventAsync(new DeskEventRow { ItemId = item.Id, At = now, ActorId = personId, Kind = "note-withdrawn", Detail = noteId.ToString() }, ct).ConfigureAwait(false);
                await this.DeskAuditAsync(me, "desk.note.withdrawn", desk, string.Empty, ct).ConfigureAwait(false);
                return null;
            default:
                return "That is not something a desk can do.";
        }
    }

    // @Name or @local-part of a member, in the note.
    private static IEnumerable<Guid> Mentioned(string text, IReadOnlyList<DeskMember> members)
    {
        foreach (DeskMember m in members)
        {
            string first = m.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? m.Name;
            string local = m.Name.Contains('@', StringComparison.Ordinal) ? m.Name[..m.Name.IndexOf('@', StringComparison.Ordinal)] : first;
            if (text.Contains("@" + m.Name, StringComparison.CurrentCultureIgnoreCase)
                || ContainsWord(text, "@" + first) || ContainsWord(text, "@" + local))
            {
                yield return m.Id;
            }
        }
    }

    private static bool ContainsWord(string text, string word)
    {
        int at = text.IndexOf(word, StringComparison.CurrentCultureIgnoreCase);
        while (at >= 0)
        {
            int end = at + word.Length;
            if (end >= text.Length || !char.IsLetterOrDigit(text[end]))
            {
                return true;
            }
            at = text.IndexOf(word, end, StringComparison.CurrentCultureIgnoreCase);
        }
        return false;
    }

    private async Task DeskAuditAsync(string actor, string action, MailboxRow desk, string detail, CancellationToken ct) =>
        await this.messageStore.AppendAuditAsync(new AuditEvent { Actor = actor, Action = action, Subject = desk.Address, Detail = detail }, ct).ConfigureAwait(false);

    /// <summary>
    /// A reply went out from a desk (DES-17 §2.12): nothing is owed until the next mail arrives, and
    /// the history names who wrote it. Called by the send.
    /// </summary>
    /// <param name="deskId">The desk.</param>
    /// <param name="writerId">Who wrote it.</param>
    /// <param name="subject">Its subject.</param>
    /// <param name="to">Its To: the first address is the person outside the desk it answers.</param>
    /// <param name="inReplyTo">Its In-Reply-To: the desk's mail it answers, found by its Message-ID, decides the conversation; empty for a new mail.</param>
    /// <param name="sentId">Its Sent copy, if one was kept.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task NoteDeskReplyAsync(Guid deskId, Guid writerId, string subject, string to, string inReplyTo, Guid? sentId, CancellationToken ct = default)
    {
        if (this.Desks is not IDeskStore desks || await this.store.GetMailboxByIdAsync(deskId, ct).ConfigureAwait(false) is not MailboxRow desk
            || await this.SharedInfoAsync(desk, ct).ConfigureAwait(false) is not SharedMailbox info)
        {
            return;
        }
        // The mail answered decides the conversation (review, 28 Oct): a reply to a Reply-To address,
        // with an edited subject, or to an untitled mail still closes that conversation's wait.
        string mark = await this.AnsweredMarkAsync(desk, inReplyTo, ct).ConfigureAwait(false)
            ?? DeskMarks.ThreadMark(deskId, subject, DeskMarks.FirstAddress(to), sentId ?? Guid.NewGuid(), this.AddressPepper);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (DeskItemRow item, DateTimeOffset? was) = await desks.NoteDeskReplyAsync(deskId, mark, sentId, now, ct).ConfigureAwait(false);
        // The wait in working minutes (DES-17 §2.6): the usual reply time is counted as the target is.
        WorkWeek week = await this.WeekOfDeskAsync(desk, info, ct).ConfigureAwait(false);
        string waited = was is DateTimeOffset w ? WorkingMinutes(w, now, week).ToString(CultureInfo.InvariantCulture) : string.Empty;
        await desks.AddDeskEventAsync(new DeskEventRow { ItemId = item.Id, At = now, ActorId = writerId, Kind = "replied", Detail = waited }, ct).ConfigureAwait(false);
        this.replying.TryRemove((deskId, mark), out _);
        await this.DeskAuditAsync(await this.PersonNameAsync(writerId, ct).ConfigureAwait(false), "desk.replied", desk, string.Empty, ct).ConfigureAwait(false);
    }

    // The mark of the desk's mail a reply answers, by its Message-ID among the desk's newest 2,000; null when not found or not readable.
    private async Task<string?> AnsweredMarkAsync(MailboxRow desk, string inReplyTo, CancellationToken ct)
    {
        string id = (inReplyTo ?? string.Empty).Trim().Trim('<', '>');
        if (id.Length == 0 || !await this.DeskReadableAsync(desk, ct).ConfigureAwait(false))
        {
            return null;
        }
        foreach (MessageRow row in await this.store.ListMessagesAsync(desk.Id, null, 2000, 0, ct).ConfigureAwait(false))
        {
            if (string.Equals(row.MessageId.Trim().Trim('<', '>'), id, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(DeskMarks.FirstAddress(row.FromHeader), desk.Address, StringComparison.OrdinalIgnoreCase))
            {
                return this.DeskMark(desk, row);
            }
        }
        return null;
    }

    /// <summary>Note that a member is writing a reply to a desk's mail now, or has stopped (DES-17 §2.7).</summary>
    /// <param name="deskId">The desk.</param>
    /// <param name="personId">The member.</param>
    /// <param name="messageId">The mail answered.</param>
    /// <param name="on">True while writing.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task SetReplyingAsync(Guid deskId, Guid personId, Guid messageId, bool on, CancellationToken ct = default)
    {
        if (await this.GetOwnedRowAsync(deskId, messageId, ct).ConfigureAwait(false) is not MessageRow m
            || await this.store.GetMailboxByIdAsync(deskId, ct).ConfigureAwait(false) is not MailboxRow desk
            || !await this.DeskReadableAsync(desk, ct).ConfigureAwait(false))
        {
            return;
        }
        if (this.DeskMark(desk, m) is not string mark)
        {
            return;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!on)
        {
            if (this.replying.TryGetValue((deskId, mark), out (Guid Person, DateTimeOffset Since, DateTimeOffset Seen) was) && was.Person == personId)
            {
                this.replying.TryRemove((deskId, mark), out _);
            }
            return;
        }
        this.replying.AddOrUpdate((deskId, mark), (personId, now, now), (_, was) => was.Person == personId || now - was.Seen >= ReplyingFor ? (personId, was.Person == personId ? was.Since : now, now) : was);
    }

    /// <summary>Who else is writing a reply to this conversation now, and since when (DES-17 §2.7).</summary>
    /// <param name="deskId">The desk.</param>
    /// <param name="personId">The person asking.</param>
    /// <param name="message">A mail of the conversation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Their name and since when, or null.</returns>
    public async Task<(string Name, DateTimeOffset Since)?> OtherReplyingAsync(Guid deskId, Guid personId, MessageRow message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (await this.store.GetMailboxByIdAsync(deskId, ct).ConfigureAwait(false) is MailboxRow desk
            && await this.DeskReadableAsync(desk, ct).ConfigureAwait(false)
            && this.DeskMark(desk, message) is string mark && this.replying.TryGetValue((deskId, mark), out (Guid Person, DateTimeOffset Since, DateTimeOffset Seen) r)
            && r.Person != personId && DateTimeOffset.UtcNow - r.Seen < ReplyingFor)
        {
            return (await this.PersonNameAsync(r.Person, ct).ConfigureAwait(false), r.Since);
        }
        return null;
    }

    /// <summary>The desk mail assigned to a person and owing a reply, for Today (DES-17 §2.9): overdue first, then by due time.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows.</returns>
    public async Task<IReadOnlyList<AssignedRow>> AssignedToYouAsync(Guid personId, CancellationToken ct = default)
    {
        var rows = new List<AssignedRow>();
        if (this.Desks is not IDeskStore desks)
        {
            return rows;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (IGrouping<Guid, DeskItemRow> onDesk in (await desks.ListAssignedDeskItemsAsync(personId, ct).ConfigureAwait(false)).GroupBy(i => i.DeskId))
        {
            if (await this.store.GetMailboxByIdAsync(onDesk.Key, ct).ConfigureAwait(false) is not MailboxRow desk || !desk.Enabled
                || await this.SharedInfoAsync(desk, ct).ConfigureAwait(false) is not SharedMailbox info
                || !(info.Members.TryGetValue(personId, out string? right) && right is "send" or "manage"))
            {
                continue;
            }
            WorkWeek week = await this.WeekOfDeskAsync(desk, info, ct).ConfigureAwait(false);
            var names = new Dictionary<Guid, string> { [personId] = await this.PersonNameAsync(personId, ct).ConfigureAwait(false) };
            foreach (DeskItemRow item in onDesk.Where(i => i.PendingSince is not null && i.RootMessageId is not null))
            {
                MessageRow? root = await this.GetOwnedRowAsync(desk.Id, item.RootMessageId!.Value, ct).ConfigureAwait(false);
                if (root is null)
                {
                    continue;
                }
                rows.Add(new AssignedRow(desk.Id, DeskNameOf(desk), root, this.StateOf(item, null, info, week, names, desk.Id, item.ThreadKey, now)));
            }
        }
        return rows.OrderByDescending(r => r.State.Overdue).ThenBy(r => r.State.Due ?? DateTimeOffset.MaxValue).ToList();
    }

    /// <summary>
    /// Give back a person's desk mail (DES-17 §2.14): removed from a desk, left with read only, or
    /// their sign-in stopped. Their Open and Waiting items become Not assigned. Done items keep the name.
    /// </summary>
    /// <param name="personId">The person.</param>
    /// <param name="deskId">One desk, or null for every desk.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many were given back.</returns>
    public async Task<int> ReturnDeskItemsAsync(Guid personId, Guid? deskId, CancellationToken ct = default)
    {
        if (this.Desks is not IDeskStore desks)
        {
            return 0;
        }
        int n = 0;
        var perDesk = new Dictionary<Guid, int>();
        foreach (DeskItemRow item in await desks.ListAssignedDeskItemsAsync(personId, ct).ConfigureAwait(false))
        {
            if (deskId is Guid only && item.DeskId != only)
            {
                continue;
            }
            await desks.SetDeskAssigneeAsync(item.Id, null, ct).ConfigureAwait(false);
            await desks.AddDeskEventAsync(new DeskEventRow { ItemId = item.Id, Kind = "returned", Detail = personId.ToString() }, ct).ConfigureAwait(false);
            perDesk[item.DeskId] = perDesk.GetValueOrDefault(item.DeskId) + 1;
            n++;
        }
        // DES-17 §2.17: the activity log says how many came back to each desk, and from whom.
        string who = (await this.store.GetMailboxByIdAsync(personId, ct).ConfigureAwait(false))?.Address ?? personId.ToString();
        foreach ((Guid d, int count) in perDesk)
        {
            if (await this.store.GetMailboxByIdAsync(d, ct).ConfigureAwait(false) is MailboxRow desk)
            {
                await this.DeskAuditAsync("anjal", "desk.returned", desk, count.ToString(CultureInfo.InvariantCulture) + " from " + who, ct).ConfigureAwait(false);
            }
        }
        return n;
    }

    /// <summary>A desk's figures for Organisation › Shared mailboxes (DES-17 §2.6, §2.13).</summary>
    /// <param name="desk">The desk.</param>
    /// <param name="info">Its settings.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The figures.</returns>
    public async Task<DeskFigures> DeskFiguresAsync(MailboxRow desk, SharedMailbox info, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(desk);
        ArgumentNullException.ThrowIfNull(info);
        if (this.Desks is not IDeskStore desks)
        {
            return new DeskFigures(0, 0, null);
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        WorkWeek week = await this.WeekOfDeskAsync(desk, info, ct).ConfigureAwait(false);
        IReadOnlyList<DeskItemRow> items = await desks.ListDeskItemsAsync(desk.Id, ct).ConfigureAwait(false);
        var owed = items.Where(i => i.DoneAt is null && i.PendingSince is not null).ToList();
        int overdue = owed.Count(i => DueAt(i.PendingSince!.Value, info.ReplyTarget, week) is DateTimeOffset d && d < now);
        List<int> waits = (await desks.ListDeskEventsSinceAsync(desk.Id, now.AddDays(-30), ct).ConfigureAwait(false))
            .Where(e => e.Kind == "replied" && int.TryParse(e.Detail, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(e => int.Parse(e.Detail, NumberStyles.None, CultureInfo.InvariantCulture))
            .OrderBy(x => x)
            .ToList();
        return new DeskFigures(owed.Count, overdue, waits.Count == 0 ? null : waits[waits.Count / 2]);
    }

    // ---- notifications (DES-17 §2.10) ----

    private void AlertDesk(Guid personId, Guid deskId, Guid messageId) =>
        this.deskAlerts[personId] = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "/desk/open?desk=" + deskId + "&message=" + messageId);

    /// <summary>
    /// The person's desk mark for live update: rises when mail is assigned to them or they are
    /// mentioned, and - for "every new mail on my desks" - when mail arrives on any of their desks.
    /// </summary>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A number that rises; 0 when nothing.</returns>
    public async Task<long> DeskStampAsync(Guid personId, CancellationToken ct = default)
    {
        long stamp = this.deskAlerts.TryGetValue(personId, out (long Stamp, string Open) a) ? a.Stamp : 0;
        MailSettings s = await this.GetMailSettingsAsync(personId, ct).ConfigureAwait(false);
        if (s.DeskNotify == "all")
        {
            foreach ((MailboxRow desk, string _, long _) in await this.SharedForPersonAsync(personId, ct).ConfigureAwait(false))
            {
                (long _, DateTimeOffset? newest) = await this.InboxStateAsync(desk.Id, ct).ConfigureAwait(false);
                stamp = Math.Max(stamp, newest?.ToUnixTimeMilliseconds() ?? 0);
            }
        }
        return stamp;
    }

    /// <summary>Where a desk pop-up opens, when the person's choice and quiet hours allow one now; null otherwise.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="clock">Their clock.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The address to open, or null.</returns>
    public async Task<string?> DeskPopUpAsync(Guid personId, ZonedClock clock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        MailSettings s = await this.GetMailSettingsAsync(personId, ct).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!s.DesktopNotify || s.DeskNotify == "none" || InQuietHours(s.QuietFrom, s.QuietTo, TimeOnly.FromDateTime(clock.Local(now).DateTime))
            || (s.QuietWhileAway && await this.AwayActiveAsync(personId, ct).ConfigureAwait(false) is not null))
        {
            return null;
        }
        if (this.deskAlerts.TryGetValue(personId, out (long Stamp, string Open) a) && now.ToUnixTimeMilliseconds() - a.Stamp < 120_000)
        {
            return a.Open;
        }
        return s.DeskNotify == "all" ? "/today" : null;
    }

    // ---- drafts (item 72.10) ----

    /// <summary>How long an empty draft is kept untouched before it is removed (D-309).</summary>
    public static readonly TimeSpan EmptyDraftKeep = TimeSpan.FromDays(30);

    /// <summary>
    /// Whether a compose form holds nothing: no recipient, subject, file or text beyond the person's
    /// own signature (D-309). Decided when it is saved, in the person's session.
    /// </summary>
    /// <param name="mailboxId">The mailbox (its signature).</param>
    /// <param name="request">The form.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when empty.</returns>
    public async Task<bool> IsEmptyComposeAsync(Guid mailboxId, ComposeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.To.Trim().Length > 0 || request.Cc.Trim().Length > 0 || request.Bcc.Trim().Length > 0 || request.Subject.Trim().Length > 0
            || request.Attachments.Count > 0 || request.CarryIndexes.Count > 0)
        {
            return false;
        }
        string text = Words(request.BodyHtml.Length > 0 ? DraftHtmlText(request.BodyHtml) : request.Body);
        if (text.Length == 0)
        {
            return true;
        }
        (string _, string signature) = await this.GetSignatureAsync(mailboxId, ct).ConfigureAwait(false);
        return signature.Trim().Length > 0 && text == Words(signature);
    }

    private static string DraftHtmlText(string html) =>
        System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200)));

    private static string Words(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text.Replace("--", " ", StringComparison.Ordinal), @"\s+", " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200)).Trim();

    /// <summary>
    /// The daily pass (D-309): drafts saved empty and untouched for 30 days are removed, except in a
    /// mailbox under a legal hold. Returns how many were removed.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The count.</returns>
    public async Task<int> RemoveEmptyDraftsAsync(CancellationToken ct = default)
    {
        if (this.Desks is not IDeskStore desks)
        {
            return 0;
        }
        int removed = 0;
        var held = new Dictionary<Guid, bool>();
        foreach (MessageRow draft in await desks.ListEmptyDraftsAsync(DateTimeOffset.UtcNow - EmptyDraftKeep, 1000, ct).ConfigureAwait(false))
        {
            if (!held.TryGetValue(draft.MailboxId, out bool onHold))
            {
                onHold = await this.IsHeldAsync(draft.MailboxId, ct).ConfigureAwait(false);
                held[draft.MailboxId] = onHold;
            }
            if (!onHold && await this.DeleteAsync(draft.MailboxId, draft.Id, ct).ConfigureAwait(false))
            {
                removed++;
            }
        }
        if (removed > 0)
        {
            await this.messageStore.AppendAuditAsync(new AuditEvent { Actor = "anjal", Action = "drafts.empty.removed", Subject = "service", Detail = removed.ToString(CultureInfo.InvariantCulture) + " empty draft(s) older than 30 days" }, ct).ConfigureAwait(false);
        }
        return removed;
    }

    /// <summary>People's names by id (a desk's writers, a draft's editor).</summary>
    /// <param name="ids">The ids.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Their names.</returns>
    public async Task<IReadOnlyDictionary<Guid, string>> NamesOfAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var names = new Dictionary<Guid, string>();
        foreach (Guid id in ids.Distinct())
        {
            names[id] = await this.PersonNameAsync(id, ct).ConfigureAwait(false);
        }
        return names;
    }

    /// <summary>A desk's notes as CSV for a production (D-317), or null when it has none.</summary>
    /// <param name="deskId">The desk.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The CSV, or null.</returns>
    internal async Task<string?> DeskNotesCsvAsync(Guid deskId, CancellationToken ct)
    {
        if (this.Desks is not IDeskStore desks)
        {
            return null;
        }
        var csv = new System.Text.StringBuilder("written,writer,about,message_id,withdrawn,text\r\n");
        int n = 0;
        foreach (DeskItemRow item in await desks.ListDeskItemsAsync(deskId, ct).ConfigureAwait(false))
        {
            IReadOnlyList<DeskNoteRow> notes = await desks.ListDeskNotesAsync(item.Id, ct).ConfigureAwait(false);
            if (notes.Count == 0)
            {
                continue;
            }
            MessageRow? about = item.RootMessageId is Guid root ? await this.store.GetMessageByIdAsync(root, ct).ConfigureAwait(false) : null;
            foreach (DeskNoteRow note in notes)
            {
                string writer = note.WriterId is Guid w ? await this.PersonNameAsync(w, ct).ConfigureAwait(false) : string.Empty;
                csv.Append(note.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                    .Append(Csv(writer)).Append(',').Append(Csv(about?.Subject ?? string.Empty)).Append(',').Append(Csv(about?.MessageId ?? string.Empty)).Append(',')
                    .Append(note.WithdrawnAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                    .Append(Csv(OpenNote(note.Body) ?? "(not readable here)")).Append("\r\n");
                n++;
            }
        }
        return n == 0 ? null : csv.ToString();
    }
}

