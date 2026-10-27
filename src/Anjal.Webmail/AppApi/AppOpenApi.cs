using System.Text.Json;
using System.Text.Json.Nodes;

namespace Anjal.Webmail.AppApi;

/// <summary>
/// rc.26 (DES-14 §23): the OpenAPI 3.1 description of the application API, made from
/// <see cref="AppApiRoutes.All"/> - the same table the endpoint group is mapped from, so there is no
/// hand-kept copy to fall behind. Served without a key at /api/v1/openapi.json.
/// </summary>
public static class AppOpenApi
{
    private static readonly Dictionary<string, string[]> QueryParameters = new(StringComparer.Ordinal)
    {
        ["listMessages"] = new[] { "since", "until", "tag", "status", "limit", "cursor" },
        ["listEvents"] = new[] { "since", "limit" },
        ["listSuppressions"] = new[] { "address", "reason", "cursor" },
        ["listInbox"] = new[] { "since", "limit", "acknowledged" },
    };

    private static readonly Dictionary<string, string> BodySchemas = new(StringComparer.Ordinal)
    {
        ["sendEmail"] = "SendRequest",
        ["sendBatch"] = "BatchRequest",
        ["addWebhook"] = "WebhookRequest",
        ["addSuppression"] = "SuppressionRequest",
        ["ackInboxMails"] = "AckRequest",
    };

    /// <summary>The description, as JSON text.</summary>
    /// <param name="serverBase">The server's base address, such as https://mail.anjal.co.in.</param>
    /// <returns>The JSON.</returns>
    public static string Document(string serverBase)
    {
        ArgumentNullException.ThrowIfNull(serverBase);
        var paths = new JsonObject();
        foreach (IGrouping<string, AppApiRoute> path in AppApiRoutes.All.GroupBy(r => r.Path))
        {
            var item = new JsonObject();
            foreach (AppApiRoute route in path)
            {
                var operation = new JsonObject
                {
                    ["operationId"] = route.Operation,
                    ["summary"] = route.Summary,
                    ["tags"] = new JsonArray(Tag(route.Path)),
                };
                var parameters = new JsonArray();
                foreach (string name in Placeholders(route.Path))
                {
                    parameters.Add(new JsonObject { ["name"] = name, ["in"] = "path", ["required"] = true, ["schema"] = new JsonObject { ["type"] = "string" } });
                }
                foreach (string name in QueryParameters.GetValueOrDefault(route.Operation, Array.Empty<string>()))
                {
                    parameters.Add(new JsonObject { ["name"] = name, ["in"] = "query", ["required"] = false, ["schema"] = new JsonObject { ["type"] = name == "limit" ? "integer" : "string" } });
                }
                if (route.Operation is "sendEmail" or "sendBatch")
                {
                    parameters.Add(new JsonObject { ["name"] = "Idempotency-Key", ["in"] = "header", ["required"] = false, ["schema"] = new JsonObject { ["type"] = "string", ["maxLength"] = 128 } });
                }
                if (route.MustSign)
                {
                    foreach (string h in new[] { "Anjal-Key-Id", "Anjal-Timestamp", "Anjal-Signature" })
                    {
                        parameters.Add(new JsonObject { ["name"] = h, ["in"] = "header", ["required"] = true, ["schema"] = new JsonObject { ["type"] = "string" } });
                    }
                }
                if (parameters.Count > 0)
                {
                    operation["parameters"] = parameters;
                }
                if (route.HasBody)
                {
                    operation["requestBody"] = new JsonObject
                    {
                        ["required"] = true,
                        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Ref(BodySchemas.GetValueOrDefault(route.Operation, "Object")) } },
                    };
                }
                var responses = new JsonObject();
                foreach (int code in route.Success)
                {
                    responses[code.ToString(System.Globalization.CultureInfo.InvariantCulture)] = code == 204
                        ? new JsonObject { ["description"] = "Done" }
                        : new JsonObject { ["description"] = code == 503 ? "Not ready" : "Success", ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "object" } } } };
                }
                foreach (string code in route.Anonymous ? Array.Empty<string>() : new[] { "401", "403", "404", "409", "413", "415", "422", "429", "500", "503" })
                {
                    if (!responses.ContainsKey(code))
                    {
                        responses[code] = new JsonObject { ["$ref"] = "#/components/responses/Error" };
                    }
                }
                operation["responses"] = responses;
                operation["security"] = route.Anonymous ? new JsonArray() : new JsonArray(new JsonObject { ["apiKey"] = new JsonArray() }, new JsonObject { ["bearer"] = new JsonArray() });
                item[route.Method.ToLowerInvariant()] = operation;
            }
            paths[path.Key] = item;
        }
        var doc = new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "Anjal application API",
                ["version"] = "1.0",
                ["description"] = "Send mail, read its status and events, receive mail encrypted to your own key. ANJAL-DES-14. Never put patient details in tags or metadata.",
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = serverBase.TrimEnd('/') + AppApiRoutes.Base }),
            ["paths"] = paths,
            ["components"] = Components(),
        };
        return doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Every "METHOD /path" the description lists, for the completeness test.</summary>
    /// <param name="json">The description.</param>
    /// <returns>The operations.</returns>
    public static IReadOnlyList<string> Operations(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument doc = JsonDocument.Parse(json);
        var found = new List<string>();
        foreach (JsonProperty path in doc.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (JsonProperty method in path.Value.EnumerateObject())
            {
                found.Add(method.Name.ToUpperInvariant() + " " + AppApiRoutes.Base + path.Name);
            }
        }
        return found;
    }

    private static IEnumerable<string> Placeholders(string path) =>
        path.Split('/').Where(p => p.StartsWith('{') && p.EndsWith('}')).Select(p => p[1..^1]);

    private static string Tag(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries)[0].Replace(".json", string.Empty, StringComparison.Ordinal);

    private static JsonObject Ref(string name) => new() { ["$ref"] = "#/components/schemas/" + name };

    private static JsonObject Components()
    {
        JsonObject Address() => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["email"] = new JsonObject { ["type"] = "string" }, ["address"] = new JsonObject { ["type"] = "string" }, ["name"] = new JsonObject { ["type"] = "string" } },
        };
        JsonObject OneOrMany() => new() { ["oneOf"] = new JsonArray(Ref("Address"), new JsonObject { ["type"] = "array", ["items"] = Ref("Address") }) };
        return new JsonObject
        {
            ["securitySchemes"] = new JsonObject
            {
                ["apiKey"] = new JsonObject { ["type"] = "apiKey", ["in"] = "header", ["name"] = "X-Api-Key" },
                ["bearer"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" },
            },
            ["responses"] = new JsonObject
            {
                ["Error"] = new JsonObject
                {
                    ["description"] = "A refusal: {error, message, requestId} and details",
                    ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Ref("Error") } },
                },
            },
            ["schemas"] = new JsonObject
            {
                ["Address"] = Address(),
                ["Error"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("error", "message", "requestId"),
                    ["properties"] = new JsonObject { ["error"] = new JsonObject { ["type"] = "string" }, ["message"] = new JsonObject { ["type"] = "string" }, ["requestId"] = new JsonObject { ["type"] = "string" } },
                },
                ["SendRequest"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("from", "to"),
                    ["properties"] = new JsonObject
                    {
                        ["from"] = Ref("Address"),
                        ["to"] = OneOrMany(),
                        ["cc"] = OneOrMany(),
                        ["bcc"] = OneOrMany(),
                        ["replyTo"] = OneOrMany(),
                        ["subject"] = new JsonObject { ["type"] = "string" },
                        ["text"] = new JsonObject { ["type"] = "string" },
                        ["html"] = new JsonObject { ["type"] = "string" },
                        ["attachments"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["filename"] = new JsonObject { ["type"] = "string" }, ["contentType"] = new JsonObject { ["type"] = "string" }, ["content"] = new JsonObject { ["type"] = "string", ["contentEncoding"] = "base64" } } },
                        },
                        ["headers"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
                        ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["maxItems"] = 10 },
                        ["metadata"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
                        ["scheduledAt"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
                        ["unsubscribe"] = new JsonObject { ["type"] = "boolean" },
                        ["template"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" }, ["language"] = new JsonObject { ["type"] = "string" }, ["values"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } } },
                        },
                    },
                },
                ["BatchRequest"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("messages"),
                    ["properties"] = new JsonObject { ["messages"] = new JsonObject { ["type"] = "array", ["maxItems"] = 100, ["items"] = Ref("SendRequest") } },
                },
                ["WebhookRequest"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("url"),
                    ["properties"] = new JsonObject { ["url"] = new JsonObject { ["type"] = "string" }, ["events"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } },
                },
                ["SuppressionRequest"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("address"),
                    ["properties"] = new JsonObject { ["address"] = new JsonObject { ["type"] = "string" }, ["note"] = new JsonObject { ["type"] = "string" } },
                },
                ["AckRequest"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("ids"),
                    ["properties"] = new JsonObject { ["ids"] = new JsonObject { ["type"] = "array", ["maxItems"] = 500, ["items"] = new JsonObject { ["type"] = "string" } } },
                },
                ["Object"] = new JsonObject { ["type"] = "object" },
            },
        };
    }
}
