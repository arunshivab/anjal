namespace Anjal.Webmail.AppApi;

/// <summary>One route of the application API: what the endpoint group maps and the API description lists.</summary>
/// <param name="Method">GET, POST or DELETE.</param>
/// <param name="Path">The path under /api/v1, with {parameters}.</param>
/// <param name="Operation">The operation's id.</param>
/// <param name="Summary">What it does.</param>
/// <param name="Success">Its success status codes.</param>
/// <param name="MustSign">True when every request must be signed (the inbox, DES-14 §14).</param>
/// <param name="Anonymous">True when no key is needed (the API description).</param>
/// <param name="HasBody">True when it takes a JSON body.</param>
public sealed record AppApiRoute(string Method, string Path, string Operation, string Summary, int[] Success, bool MustSign = false, bool Anonymous = false, bool HasBody = false);

/// <summary>rc.26 (DES-14 §24): every endpoint of the application API, in one place.</summary>
public static class AppApiRoutes
{
    /// <summary>The base of every path.</summary>
    public const string Base = "/api/v1";

    /// <summary>The routes, in the order the description lists them.</summary>
    public static readonly IReadOnlyList<AppApiRoute> All = new[]
    {
        new AppApiRoute("POST", "/messages/email", "sendEmail", "Send one message", new[] { 202 }, HasBody: true),
        new AppApiRoute("POST", "/messages/email/batch", "sendBatch", "Send up to 100 messages; one result each", new[] { 200 }, HasBody: true),
        new AppApiRoute("GET", "/messages/{id}", "getMessage", "Status of one message", new[] { 200 }),
        new AppApiRoute("GET", "/messages", "listMessages", "The application's messages, newest first", new[] { 200 }),
        new AppApiRoute("GET", "/events", "listEvents", "Events, oldest first", new[] { 200 }),
        new AppApiRoute("GET", "/webhooks", "listWebhooks", "The application's webhooks", new[] { 200 }),
        new AppApiRoute("POST", "/webhooks", "addWebhook", "Add a webhook; its secret is returned once", new[] { 201 }, HasBody: true),
        new AppApiRoute("DELETE", "/webhooks/{id}", "removeWebhook", "Remove a webhook", new[] { 204 }),
        new AppApiRoute("POST", "/webhooks/{id}/test", "testWebhook", "Send a webhook.test event to one webhook", new[] { 202 }),
        new AppApiRoute("GET", "/limits", "getLimits", "The application's limits and today's use", new[] { 200 }),
        new AppApiRoute("GET", "/health", "getHealth", "Can this application send now", new[] { 200, 503 }),
        new AppApiRoute("GET", "/suppressions", "listSuppressions", "The do-not-send list, or one address on it", new[] { 200 }),
        new AppApiRoute("POST", "/suppressions", "addSuppression", "Suppress an address by hand", new[] { 201 }, HasBody: true),
        new AppApiRoute("DELETE", "/suppressions/{address}", "removeSuppression", "Take an address off the list (hard_bounce and manual only)", new[] { 204 }),
        new AppApiRoute("GET", "/inbox", "listInbox", "New mail, oldest first (signed)", new[] { 200 }, MustSign: true),
        new AppApiRoute("GET", "/inbox/{id}", "getInboxMail", "One mail's encrypted envelope (signed)", new[] { 200 }, MustSign: true),
        new AppApiRoute("GET", "/inbox/{id}/raw", "getInboxMailRaw", "The same envelope (kept for v0.3 applications; signed)", new[] { 200 }, MustSign: true),
        new AppApiRoute("POST", "/inbox/{id}/ack", "ackInboxMail", "Acknowledge one mail (signed)", new[] { 204 }, MustSign: true),
        new AppApiRoute("POST", "/inbox/ack", "ackInboxMails", "Acknowledge up to 500 mails (signed)", new[] { 200 }, MustSign: true, HasBody: true),
        new AppApiRoute("GET", "/openapi.json", "getOpenApi", "This description", new[] { 200 }, Anonymous: true),
    };
}
