using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using YobaConf.Core.Auth;
using YobaConf.Core.Bindings;

namespace YobaConf.Web.Endpoints;

// JSON admin-API for runtime api-keys (Phase G.4). Mirrors /admin/api-keys Razor UI:
// PUT to create (plaintext returned exactly once), DELETE to soft-delete, GET to list
// (no plaintexts in list). Auth — AdminTokenAuthFilter (G.2).
public static class AdminApiKeysEndpoint
{
    public static void MapAdminApiKeys(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPut("/api-keys", PutHandler);
        group.MapDelete("/api-keys/{id:long}", DeleteHandler);
        group.MapGet("/api-keys", ListHandler);
    }

    // PUT /v1/admin/api-keys — create. Returns 201 with {id, prefix, plaintext, ...}.
    // Plaintext is shown exactly once and never returnable thereafter (matches UI flow).
    static IResult PutHandler(
        HttpContext ctx,
        [FromBody] ApiKeyCreateRequest? body,
        [FromServices] IApiKeyAdmin admin,
        [FromServices] TimeProvider clock)
    {
        if (body is null)
            return Results.Json(new { error = "bad_request", reason = "request body is required" },
                statusCode: StatusCodes.Status400BadRequest);
        if (string.IsNullOrWhiteSpace(body.Description))
            return Results.Json(new { error = "bad_request", reason = "description is required" },
                statusCode: StatusCodes.Status400BadRequest);

        TagSet requiredTags;
        try
        {
            requiredTags = ParseTagSet(body.RequiredTags);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            return Results.Json(new { error = "bad_request", reason = $"requiredTags: {ex.Message}" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var prefixes = body.AllowedKeyPrefixes is { Length: > 0 } ? body.AllowedKeyPrefixes : null;

        var token = AdminTokenAuth.CurrentToken(ctx)
            ?? throw new InvalidOperationException("admin-token filter did not populate HttpContext.Items");
        var actor = AdminTokenAuth.AuditActor(token);

        var created = admin.Create(requiredTags, prefixes, body.Description.Trim(), clock.GetUtcNow(), actor);
        return Results.Json(new
        {
            id = created.Info.Id,
            prefix = created.Info.TokenPrefix,
            plaintext = created.Plaintext,
            description = created.Info.Description,
            requiredTags = created.Info.RequiredTags.ToDictionary(kv => kv.Key, kv => kv.Value),
            allowedKeyPrefixes = created.Info.AllowedKeyPrefixes,
            updatedAt = created.Info.UpdatedAt,
        }, statusCode: StatusCodes.Status201Created);
    }

    static IResult DeleteHandler(
        HttpContext ctx,
        long id,
        [FromServices] IApiKeyAdmin admin,
        [FromServices] TimeProvider clock)
    {
        var token = AdminTokenAuth.CurrentToken(ctx)
            ?? throw new InvalidOperationException("admin-token filter did not populate HttpContext.Items");
        var actor = AdminTokenAuth.AuditActor(token);

        return admin.SoftDelete(id, clock.GetUtcNow(), actor)
            ? Results.NoContent()
            : Results.Json(new { error = "not_found", reason = $"api-key {id} not found or already deleted" },
                statusCode: StatusCodes.Status404NotFound);
    }

    // GET /v1/admin/api-keys — list active keys, no plaintexts (only prefix + scope).
    static IResult ListHandler([FromServices] IApiKeyAdmin admin) =>
        Results.Ok(admin.ListActive().Select(k => new
        {
            id = k.Id,
            prefix = k.TokenPrefix,
            description = k.Description,
            requiredTags = k.RequiredTags.ToDictionary(kv => kv.Key, kv => kv.Value),
            allowedKeyPrefixes = k.AllowedKeyPrefixes,
            updatedAt = k.UpdatedAt,
        }).ToArray());

    static TagSet ParseTagSet(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
            return TagSet.Empty;
        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("must be a JSON object of {key: \"value\"} pairs");

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var p in element.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"value for key '{p.Name}' must be a JSON string");
            pairs.Add(new(p.Name, p.Value.GetString()!));
        }
        return TagSet.From(pairs);
    }
}

public sealed record ApiKeyCreateRequest(
    string? Description,
    JsonElement RequiredTags,
    string[]? AllowedKeyPrefixes);
