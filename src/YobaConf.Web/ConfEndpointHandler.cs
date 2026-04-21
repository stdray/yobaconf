using YobaConf.Core;
using YobaConf.Core.Security;

namespace YobaConf.Web;

// Request handler for `GET /v1/conf/{**urlPath}`. Order of checks is security-first per
// spec §4: auth before lookup (403 wins over 404 — we never leak existence via 404 to an
// unauthorised caller).
//
// Flow:
//   1. Extract API-key token from header or query string. Missing/empty -> 401.
//   2. Validate token against IApiKeyStore. Invalid -> 401.
//   3. Parse the URL path as NodePath (dot-separated externally, slash-separated internally).
//      Bad slug -> 400.
//   4. Scope check: requested path must be a descendant-or-equal of the key's RootPath.
//      Out of scope -> 403 (before we look anything up in the store).
//   5. Run the resolve pipeline. NodeNotFoundException -> 404.
//   6. Compare `If-None-Match` with computed ETag. Match -> 304 (no body, ETag header).
//   7. Otherwise 200 with canonical JSON body + ETag header.
//
// Errors from the pipeline (HoconParserException, IncludeScopeViolationException, etc.)
// propagate as 500s via the default ASP.NET exception middleware — they indicate
// misconfigured nodes, not client errors, so returning detail to the client is fine in
// dev mode and hidden by UseExceptionHandler("/Error") in prod.
static class ConfEndpointHandler
{
	const string ApiKeyHeader = "X-YobaConf-ApiKey";
	const string ApiKeyQuery = "apiKey";

	public static IResult Handle(
		string? urlPath,
		HttpContext ctx,
		IConfigStore store,
		IApiKeyStore keys,
		IServiceProvider services)
	{
		var token = ExtractToken(ctx);
		if (string.IsNullOrEmpty(token))
			return Results.Unauthorized();

		var apiKey = keys.Validate(token);
		if (apiKey is null)
			return Results.Unauthorized();

		// GetService (not GetRequiredService) — Testing env skips DI registration so
		// fixtures don't all need YOBACONF_MASTER_KEY. Resolve raises with a clear
		// message if secrets are in scope but encryptor is null, deferring the check
		// to the same throw-site as a missing-key misconfig in prod.
		var encryptor = services.GetService<ISecretEncryptor>();

		NodePath requestedPath;
		try
		{
			requestedPath = NodePath.ParseUrl(urlPath ?? string.Empty);
		}
		catch (ArgumentException)
		{
			return Results.BadRequest(new { error = "invalid path" });
		}

		if (!IsWithinScope(apiKey.RootPath, requestedPath))
			// `Results.Forbid()` requires a registered authentication scheme — we don't have
			// one for this endpoint (API-key auth is custom). Plain 403 status code does the
			// same job without dragging in cookie-auth's ForbidAsync.
			return Results.StatusCode(StatusCodes.Status403Forbidden);

		ResolveResult result;
		try
		{
			result = ResolvePipeline.Resolve(requestedPath, store, encryptor);
		}
		catch (NodeNotFoundException)
		{
			return Results.NotFound();
		}

		var etagValue = $"\"{result.ETag}\"";
		var ifNoneMatch = ctx.Request.Headers.IfNoneMatch.ToString();
		if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == etagValue)
		{
			ctx.Response.Headers.ETag = etagValue;
			return Results.StatusCode(StatusCodes.Status304NotModified);
		}

		ctx.Response.Headers.ETag = etagValue;
		return Results.Content(result.Json, "application/json; charset=utf-8");
	}

	static string? ExtractToken(HttpContext ctx)
	{
		if (ctx.Request.Headers.TryGetValue(ApiKeyHeader, out var header))
		{
			var value = header.ToString();
			if (!string.IsNullOrEmpty(value))
				return value;
		}
		if (ctx.Request.Query.TryGetValue(ApiKeyQuery, out var query))
		{
			var value = query.ToString();
			if (!string.IsNullOrEmpty(value))
				return value;
		}
		return null;
	}

	// RootPath is ancestor-or-equal of requestedPath. Ordinal segment-wise comparison
	// via NodePath.IsAncestorOf — prevents the `yobaproj/yobaapp` key from reaching
	// `yobaproj/yobaapplication` via raw-string prefix match (spec §8).
	static bool IsWithinScope(NodePath scope, NodePath path) =>
		scope.Equals(path) || scope.IsAncestorOf(path);
}
