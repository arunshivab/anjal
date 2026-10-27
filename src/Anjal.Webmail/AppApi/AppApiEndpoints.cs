namespace Anjal.Webmail.AppApi;

/// <summary>rc.26 (DES-14 §1): the application API's endpoint group, mapped from <see cref="AppApiRoutes.All"/>.</summary>
public static class AppApiEndpoints
{
    /// <summary>Map every route under /api/v1.</summary>
    /// <param name="app">The application.</param>
    public static void MapAppApi(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        RouteGroupBuilder group = app.MapGroup(AppApiRoutes.Base).AllowAnonymous().DisableAntiforgery();
        foreach (AppApiRoute route in AppApiRoutes.All)
        {
            group.MapMethods(route.Path, new[] { route.Method }, (HttpContext http, AppApiService api) => api.DispatchAsync(http, route))
                .WithName("appapi-" + route.Operation);
        }
        // DES-14 §23: the same description beside the developers' help article.
        app.MapGet("/help/api/openapi.json", (HttpContext http) => Results.Text(AppOpenApi.Document(http.Request.Scheme + "://" + http.Request.Host), "application/json")).AllowAnonymous();
    }
}
