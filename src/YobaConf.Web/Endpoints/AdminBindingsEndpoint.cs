using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using YobaConf.Core.Bindings;
using YobaConf.Core.Security;

namespace YobaConf.Web.Endpoints;

// JSON admin-API for bindings (Phase G.3). Mirrors the operations available through the
// Razor Bindings UI (Edit / Delete / Index list) so scripts can do bulk-set / cross-env
// updates without clicking. Auth — AdminTokenAuthFilter (G.2).
//
// Request shapes are deliberately flat and JSON-typed: callers send the raw value (`"x"`,
// 42, true) under `value` for Plain, plaintext string for Secret. Server round-trips into
// the canonical JSON-encoded-scalar form `Binding.ValuePlain` expects.
public static class AdminBindingsEndpoint
{
    public static void MapAdminBindings(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPut("/bindings", PutHandler);
        group.MapDelete("/bindings/{id:long}", DeleteHandler);
        group.MapGet("/bindings", ListHandler);
    }

    // PUT /v1/admin/bindings — upsert by (TagSet, KeyPath). Returns {id, etag, created}.
    static IResult PutHandler(
        HttpContext ctx,
        [FromBody] BindingUpsertRequest? body,
        [FromServices] IBindingStore store,
        [FromServices] IBindingStoreAdmin admin,
        [FromServices] IServiceProvider sp)
    {
        if (body is null)
            return Results.Json(new { error = "bad_request", reason = "request body is required" },
                statusCode: StatusCodes.Status400BadRequest);

        TagSet tagSet;
        try
        {
            tagSet = ParseTagSet(body.TagSet);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            return Results.Json(new { error = "bad_request", reason = $"tagSet: {ex.Message}" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            Slug.RequireKeyPath(body.KeyPath);
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new { error = "bad_request", reason = ex.Message },
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!Enum.TryParse<BindingKind>(body.Kind, ignoreCase: false, out var kind))
            return Results.Json(new { error = "bad_request", reason = $"kind '{body.Kind}' must be 'Plain' or 'Secret'" },
                statusCode: StatusCodes.Status400BadRequest);

        var clock = sp.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        Binding candidate;

        if (kind == BindingKind.Secret)
        {
            var encryptor = sp.GetService<ISecretEncryptor>();
            if (encryptor is null)
                return Results.Json(new
                {
                    error = "service_unavailable",
                    reason = "Secret bindings require YOBACONF_MASTER_KEY to be configured.",
                }, statusCode: StatusCodes.Status503ServiceUnavailable);

            if (body.Value.ValueKind != JsonValueKind.String)
                return Results.Json(new { error = "bad_request", reason = "Secret value must be a JSON string (plaintext)." },
                    statusCode: StatusCodes.Status400BadRequest);

            var bundle = encryptor.Encrypt(body.Value.GetString()!);
            candidate = new Binding
            {
                Id = 0,
                TagSet = tagSet,
                KeyPath = body.KeyPath,
                Kind = BindingKind.Secret,
                Ciphertext = bundle.Ciphertext,
                Iv = bundle.Iv,
                AuthTag = bundle.AuthTag,
                KeyVersion = bundle.KeyVersion,
                ContentHash = string.Empty,
                UpdatedAt = now,
            };
        }
        else
        {
            // Plain: round-trip the JSON value through System.Text.Json so storage is the
            // compact canonical-scalar form ResolvePipeline expects (`"x"`, `42`, `true`,
            // `null`). This stays consistent with how the Edit page produces ValuePlain.
            var canonicalScalar = body.Value.ValueKind switch
            {
                JsonValueKind.Undefined => null,
                _ => body.Value.GetRawText(),
            };
            if (canonicalScalar is null)
                return Results.Json(new { error = "bad_request", reason = "value is required for Plain bindings" },
                    statusCode: StatusCodes.Status400BadRequest);

            candidate = new Binding
            {
                Id = 0,
                TagSet = tagSet,
                KeyPath = body.KeyPath,
                Kind = BindingKind.Plain,
                ValuePlain = canonicalScalar,
                ContentHash = string.Empty,
                UpdatedAt = now,
            };
        }

        var token = AdminTokenAuth.CurrentToken(ctx)
            ?? throw new InvalidOperationException("admin-token filter did not populate HttpContext.Items");
        var actor = AdminTokenAuth.AuditActor(token);

        var outcome = admin.Upsert(candidate, actor);
        var created = outcome.OldHash is null;
        return Results.Json(new
        {
            id = outcome.Binding.Id,
            etag = outcome.Binding.ContentHash,
            created,
        }, statusCode: created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    // DELETE /v1/admin/bindings/{id} — soft-delete. 204 on success, 404 if missing.
    static IResult DeleteHandler(
        HttpContext ctx,
        long id,
        [FromServices] IBindingStoreAdmin admin,
        [FromServices] TimeProvider clock)
    {
        var token = AdminTokenAuth.CurrentToken(ctx)
            ?? throw new InvalidOperationException("admin-token filter did not populate HttpContext.Items");
        var actor = AdminTokenAuth.AuditActor(token);

        return admin.SoftDelete(id, clock.GetUtcNow(), actor)
            ? Results.NoContent()
            : Results.Json(new { error = "not_found", reason = $"binding {id} not found or already deleted" },
                statusCode: StatusCodes.Status404NotFound);
    }

    // GET /v1/admin/bindings?tag=k=v&key=prefix — list with optional filters. Multiple
    // `tag=` params AND together (binding's TagSet must contain every entry). Single
    // `key=` matches as a prefix (no glob — `db.` matches `db.host` and `db.port` but not
    // `cache.db`). Pet-scale (≤200 rows) — no pagination.
    static IResult ListHandler(
        HttpContext ctx,
        [FromServices] IBindingStore store)
    {
        // Parse `tag=k=v` filters. Accept `tag=k=v` form only — bare `tag=k` is a 400.
        var tagFilters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in ctx.Request.Query["tag"])
        {
            if (string.IsNullOrEmpty(raw)) continue;
            var eq = raw.IndexOf('=');
            if (eq <= 0 || eq == raw.Length - 1)
                return Results.Json(new { error = "bad_request", reason = $"tag filter '{raw}' must be 'key=value'" },
                    statusCode: StatusCodes.Status400BadRequest);
            var k = raw[..eq];
            var v = raw[(eq + 1)..];
            if (!Slug.IsValid(k) || !Slug.IsValid(v))
                return Results.Json(new { error = "bad_request", reason = $"tag filter '{raw}' contains invalid slug" },
                    statusCode: StatusCodes.Status400BadRequest);
            tagFilters[k] = v;
        }
        var keyPrefix = ctx.Request.Query["key"].FirstOrDefault();

        var rows = store.ListActive()
            .Where(b => MatchesTagFilter(b, tagFilters))
            .Where(b => string.IsNullOrEmpty(keyPrefix) || b.KeyPath.StartsWith(keyPrefix, StringComparison.Ordinal))
            .Select(ToListItem)
            .ToArray();
        return Results.Ok(rows);
    }

    static bool MatchesTagFilter(Binding b, Dictionary<string, string> filters)
    {
        if (filters.Count == 0) return true;
        foreach (var (k, v) in filters)
        {
            if (!b.TagSet.TryGetValue(k, out var bv) || !string.Equals(bv, v, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    static object ToListItem(Binding b) => new
    {
        id = b.Id,
        tagSet = b.TagSet.ToDictionary(kv => kv.Key, kv => kv.Value),
        keyPath = b.KeyPath,
        kind = b.Kind.ToString(),
        // Secret values are redacted server-side — plaintext never leaves through the list
        // surface. Callers distinguish via `kind == "Secret"` and use the dedicated
        // single-read reveal endpoint (E.3) when they need plaintext.
        value = b.Kind == BindingKind.Plain ? ParsePlainScalar(b.ValuePlain) : null,
        updatedAt = b.UpdatedAt,
        etag = b.ContentHash,
    };

    static JsonElement? ParsePlainScalar(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // ValuePlain is supposed to be a JSON-encoded scalar; if a row predates that
            // contract, fall back to wrapping the raw text as a string so the list is
            // still serializable. Defensive — shouldn't fire on data written by current
            // upsert paths.
            return JsonDocument.Parse(JsonSerializer.Serialize(raw)).RootElement.Clone();
        }
    }

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

// JSON request body for PUT /v1/admin/bindings. `Value` is a polymorphic JSON value:
// string / number / bool / null for Plain; string (plaintext) for Secret.
public sealed record BindingUpsertRequest(
    JsonElement TagSet,
    string KeyPath,
    string Kind,
    JsonElement Value);
