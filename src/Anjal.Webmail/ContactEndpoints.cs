using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Mvc;

namespace Anjal.Webmail;

/// <summary>
/// rc.12 (items 18, 29, 30): the endpoints behind the Contacts page and the
/// "Add to contacts" offers. Every one needs the signed-in mailbox and the
/// page's antiforgery token, like every other form.
/// </summary>
internal static class ContactEndpoints
{
    private static readonly char[] MemberSeparators = { ',', ';', '\n' };

    internal static void Map(WebApplication app)
    {
        app.MapPost("/contacts/add", async (HttpContext http, [FromForm] string? address, [FromForm] string? firstName, [FromForm] string? lastName, [FromForm] string? always, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            Contact contact = await svc.FindContactAsync(mailboxId.Value, address ?? string.Empty, ct).ConfigureAwait(false)
                ?? new Contact { Address = address ?? string.Empty };
            if (contact.FirstName.Length == 0)
            {
                contact.FirstName = firstName ?? string.Empty;
            }
            if (contact.LastName.Length == 0)
            {
                contact.LastName = lastName ?? string.Empty;
            }
            string? error = await svc.SaveContactAsync(mailboxId.Value, contact, ct).ConfigureAwait(false);
            if (always == "1")
            {
                ContactSettings settings = await svc.GetContactSettingsAsync(mailboxId.Value, ct).ConfigureAwait(false);
                settings.SaveNewSendersAutomatically = true;
                await svc.SetContactSettingsAsync(mailboxId.Value, settings, ct).ConfigureAwait(false);
            }
            string onward = Program.SafeBack(back, "/contacts");
            return Results.Redirect(onward + (onward.Contains('?', StringComparison.Ordinal) ? "&" : "?") + (error is null ? "contactsaved=1" : "contacterror=1"));
        }).RequireAuthorization();

        app.MapPost("/contacts/not-offered", async (HttpContext http, [FromForm] string? address, [FromForm] string? back, [FromForm] string? mode, [FromForm] string? message, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            // rc.15 (item 18): "Not now" passes over this one mail; the sender's next mail asks again.
            if (mode == "later" && Guid.TryParse(message, out Guid skipped))
            {
                ContactSettings later = await svc.GetContactSettingsAsync(mailboxId.Value, ct).ConfigureAwait(false);
                if (!later.SkippedMessages.Contains(skipped.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    later.SkippedMessages.Add(skipped.ToString());
                    while (later.SkippedMessages.Count > 2000)
                    {
                        later.SkippedMessages.RemoveAt(0);
                    }
                    await svc.SetContactSettingsAsync(mailboxId.Value, later, ct).ConfigureAwait(false);
                }
                return Results.Redirect(Program.SafeBack(back, "/folder/INBOX"));
            }
            if (!string.IsNullOrWhiteSpace(address))
            {
                ContactSettings settings = await svc.GetContactSettingsAsync(mailboxId.Value, ct).ConfigureAwait(false);
                if (!settings.NotOffered.Contains(address.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    settings.NotOffered.Add(address.Trim());
                    while (settings.NotOffered.Count > 5000)
                    {
                        settings.NotOffered.RemoveAt(0);
                    }
                    await svc.SetContactSettingsAsync(mailboxId.Value, settings, ct).ConfigureAwait(false);
                }
            }
            return Results.Redirect(Program.SafeBack(back, "/folder/INBOX"));
        }).RequireAuthorization();

        app.MapPost("/contacts/save", async (HttpContext http, [FromForm] string? id, [FromForm] string? firstName, [FromForm] string? lastName, [FromForm] string? address, [FromForm] string? organisation, [FromForm] string? phone, [FromForm] string? notes, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            var contact = new Contact
            {
                Id = Guid.TryParse(id, out Guid existing) ? existing : Guid.NewGuid(),
                FirstName = firstName ?? string.Empty,
                LastName = lastName ?? string.Empty,
                Address = address ?? string.Empty,
                Organisation = organisation ?? string.Empty,
                Phone = phone ?? string.Empty,
                Notes = notes ?? string.Empty,
            };
            string? error = await svc.SaveContactAsync(mailboxId.Value, contact, ct).ConfigureAwait(false);
            return Results.Redirect(error is null
                ? $"/contacts?open={contact.Id}&saved=1"
                : $"/contacts?{(Guid.TryParse(id, out Guid back) ? "open=" + back + "&edit=1" : "new=1")}&error={Uri.EscapeDataString(error)}");
        }).RequireAuthorization();

        app.MapPost("/contacts/{id:guid}/delete", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.DeleteContactAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return Results.Redirect("/contacts?deleted=1");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/contacts/merge", async (HttpContext http, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            int merged = await svc.MergeDuplicateContactsAsync(mailboxId.Value, ct).ConfigureAwait(false);
            return Results.Redirect($"/contacts?merged={merged}");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/contacts/import", async (HttpContext http, IFormFileCollection files, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            IFormFile? file = files.GetFile("file");
            if (file is null || file.Length == 0 || file.Length > 5 * 1024 * 1024)
            {
                return Results.Redirect("/contacts?importerror=1");
            }
            string text;
            using (var reader = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            ImportResult result = await svc.ImportContactsAsync(mailboxId.Value, text, ct).ConfigureAwait(false);
            return Results.Redirect($"/contacts?added={result.Added}&updated={result.Updated}&skipped={result.Skipped}");
        }).RequireAuthorization();

        app.MapGet("/contacts/export", async (HttpContext http, string? format, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            IReadOnlyList<Contact> all = await svc.ListContactsAsync(mailboxId.Value, ct).ConfigureAwait(false);
            return format == "vcf"
                ? Results.File(System.Text.Encoding.UTF8.GetBytes(MailboxService.ContactsToVCard(all)), "text/vcard", "contacts.vcf")
                : Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(MailboxService.ContactsToCsv(all))).ToArray(), "text/csv", "contacts.csv");
        }).RequireAuthorization();

        app.MapPost("/contacts/groups/save", async (HttpContext http, [FromForm] string? id, [FromForm] string? name, [FromForm] string? members, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            var group = new ContactGroup
            {
                Id = Guid.TryParse(id, out Guid existing) ? existing : Guid.NewGuid(),
                Name = name ?? string.Empty,
                Addresses = (members ?? string.Empty).Split(MemberSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            };
            string? error = await svc.SaveGroupAsync(mailboxId.Value, group, ct).ConfigureAwait(false);
            return Results.Redirect(error is null
                ? $"/contacts?tab=groups&open={group.Id}&saved=1"
                : $"/contacts?tab=groups&new=1&error={Uri.EscapeDataString(error)}");
        }).RequireAuthorization();

        app.MapPost("/contacts/groups/{id:guid}/delete", async (HttpContext http, Guid id, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            await svc.DeleteGroupAsync(mailboxId.Value, id, ct).ConfigureAwait(false);
            return Results.Redirect("/contacts?tab=groups&deleted=1");
        }).RequireAuthorization().AddEndpointFilter(Program.RequireAntiforgery);

        app.MapPost("/contacts/settings", async (HttpContext http, [FromForm] string? always, [FromForm] string? toggle, [FromForm] string? ask, [FromForm] string? recipients, [FromForm] string? back, MailboxService svc, CancellationToken ct) =>
        {
            Guid? mailboxId = WebmailAuthService.PersonIdOf(http.User);
            if (mailboxId is null)
            {
                return Results.Redirect("/sign-in");
            }
            ContactSettings settings = await svc.GetContactSettingsAsync(mailboxId.Value, ct).ConfigureAwait(false);
            // rc.13: Mail settings put it the other way round - "Ask to save them to contacts".
            if (toggle == "recipients")
            {
                // rc.15 (item 29): people you write to join your contacts.
                settings.SaveRecipientsAutomatically = recipients == "1";
            }
            else
            {
                settings.SaveNewSendersAutomatically = toggle == "ask" ? ask != "1" : always == "1";
            }
            await svc.SetContactSettingsAsync(mailboxId.Value, settings, ct).ConfigureAwait(false);
            return Results.Redirect(string.IsNullOrEmpty(back) ? "/contacts?settings=1" : LocalPath.Safe(back));
        }).RequireAuthorization();
    }
}
