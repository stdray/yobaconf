using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using YobaConf.Core.Auth;
using YobaConf.Core.Bindings;
using YobaConf.Core.Resolve;
using YobaConf.Core.Security;

namespace YobaConf.Web.Endpoints;

// Main consumer-facing endpoint. Query-params ≡ tag-vector (except `apiKey`). Auth: either
// `X-YobaConf-ApiKey` header or `?apiKey=` query-string. Returns canonical JSON + ETag on
// success, 304 on match, 409 with diagnostic on conflict, 401/403/400 on auth/scope/slug.
public static class ConfEndpoint
{
	public static void MapConfEndpoint(this IEndpointRouteBuilder endpoints)
	{
		ArgumentNullException.ThrowIfNull(endpoints);
		endpoints.MapGet("/v1/conf", Handle).AllowAnonymous();
	}

	static IResult Handle(
		HttpContext ctx,
		[FromServices] IApiKeyStore apiKeys,
		[FromServices] IBindingStore bindings,
		[FromServices] IServiceProvider sp)
	{
		var encryptor = sp.GetService<ISecretEncryptor>();
		// 1. Extract token: header takes precedence; query-string fallback for clients that
		//    can't set arbitrary headers (curl -G, browser debugging).
		var token = ctx.Request.Headers["X-YobaConf-ApiKey"].FirstOrDefault()
			?? ctx.Request.Query["apiKey"].FirstOrDefault();

		var validation = apiKeys.Validate(token);
		if (validation is ApiKeyValidation.Invalid invalid)
			return Results.Json(
				new { error = "unauthorized", reason = invalid.Reason },
				statusCode: StatusCodes.Status401Unauthorized);
		var apiKey = ((ApiKeyValidation.Valid)validation).Key;

		// 2. Build tag-vector from every query-param except `apiKey`. Last-wins on duplicates
		//    (standard HTTP semantics, matches spec §4 note).
		var tagVector = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var pair in ctx.Request.Query)
		{
			if (string.Equals(pair.Key, "apiKey", StringComparison.Ordinal)) continue;
			var value = pair.Value.LastOrDefault() ?? string.Empty;

			if (!Slug.IsValid(pair.Key))
				return Results.Json(
					new { error = "bad_request", reason = $"tag-key '{pair.Key}' is not a valid slug" },
					statusCode: StatusCodes.Status400BadRequest);
			if (!Slug.IsValid(value))
				return Results.Json(
					new { error = "bad_request", reason = $"tag-value '{value}' for key '{pair.Key}' is not a valid slug" },
					statusCode: StatusCodes.Status400BadRequest);
			tagVector[pair.Key] = value;
		}

		// 3. Scope check — request vector must be a superset of the api-key's required tags.
		var scopeError = IApiKeyStore.CheckScope(apiKey, tagVector);
		if (scopeError is not null)
			return Results.Json(
				new { error = "forbidden", reason = scopeError },
				statusCode: StatusCodes.Status403Forbidden);

		// 4. Resolve.
		var pipeline = new ResolvePipeline(bindings, encryptor);
		var outcome = pipeline.Resolve(tagVector, apiKey.AllowedKeyPrefixes);

		// 5. Shape response.
		return outcome switch
		{
			ResolveSuccess success => BuildSuccess(ctx, success),
			ResolveConflict conflict => BuildConflict(conflict),
			_ => throw new InvalidOperationException("unknown outcome"),
		};
	}

	static IResult BuildSuccess(HttpContext ctx, ResolveSuccess success)
	{
		var ifNoneMatch = ctx.Request.Headers.IfNoneMatch.FirstOrDefault();
		if (ifNoneMatch is not null && StripQuotes(ifNoneMatch) == success.ETag)
		{
			ctx.Response.Headers.ETag = $"\"{success.ETag}\"";
			return Results.StatusCode(StatusCodes.Status304NotModified);
		}
		ctx.Response.Headers.ETag = $"\"{success.ETag}\"";
		return Results.Content(success.Json, "application/json");
	}

	static IResult BuildConflict(ResolveConflict conflict) =>
		Results.Json(new
		{
			error = "conflict",
			key = conflict.KeyPath,
			tiedBindings = conflict.Candidates.Select(c => new
			{
				id = c.BindingId,
				tagSet = c.TagSet.CanonicalJson,
				kind = c.Kind.ToString(),
				value = c.ValueDisplay,
			}).ToArray(),
			hint = "Add a more-specific overlay covering both tag-sets to disambiguate.",
		}, statusCode: StatusCodes.Status409Conflict);

	static string StripQuotes(string etag) =>
		etag is ['"', .. var inner, '"'] ? new string(inner) : etag;
}
