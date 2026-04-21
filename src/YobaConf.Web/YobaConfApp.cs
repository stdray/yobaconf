using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using YobaConf.Core;
using YobaConf.Core.Storage;

namespace YobaConf.Web;

// Wiring factored out of Program.cs so integration tests can build a Kestrel-hosted
// app on an ephemeral port without going through WebApplicationFactory (which hard-codes TestServer).
public static class YobaConfApp
{
	public static void ConfigureServices(WebApplicationBuilder builder)
	{
		ArgumentNullException.ThrowIfNull(builder);

		// Caddy on the host terminates TLS on :443 and reverse-proxies to 127.0.0.1:8081
		// (see spec §11). Without this wiring HttpContext.Request.IsHttps is false behind
		// the loopback proxy — UseHttpsRedirection loops 307, cookie Secure-flag is wrong.
		// Defaults cleared so we trust 127.0.0.1 only; any other X-Forwarded-* source is ignored.
		builder.Services.Configure<ForwardedHeadersOptions>(o =>
		{
			o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
			o.KnownIPNetworks.Clear();
			o.KnownProxies.Clear();
			o.KnownProxies.Add(IPAddress.Loopback);
			o.KnownProxies.Add(IPAddress.IPv6Loopback);
		});

		// Options bindings
		builder.Services.Configure<SqliteConfigStoreOptions>(builder.Configuration.GetSection("SqliteConfigStore"));
		builder.Services.Configure<ApiKeyOptions>(builder.Configuration.GetSection("ApiKeys"));

		// Core services
		builder.Services.AddSingleton<IConfigStore, SqliteConfigStore>();
		builder.Services.AddSingleton<IApiKeyStore, ConfigApiKeyStore>();

		builder.Services.AddRazorPages();
	}

	public static void Configure(WebApplication app)
	{
		ArgumentNullException.ThrowIfNull(app);

		var isTesting = app.Environment.EnvironmentName.Equals("Testing", StringComparison.Ordinal);

		if (!app.Environment.IsDevelopment() && !isTesting)
		{
			app.UseExceptionHandler("/Error");
			app.UseHsts();
		}

		// ForwardedHeaders must run before any middleware that inspects the scheme (in
		// particular UseHttpsRedirection). Under Caddy the incoming scheme on the socket
		// is http; the middleware rewrites HttpContext.Request.Scheme to https so that
		// UseHttpsRedirection sees the real client scheme and doesn't bounce 307 → 307.
		app.UseForwardedHeaders();

		// UseHttpsRedirection skipped under "Testing" — lets Kestrel-based tests use plain http://.
		if (!isTesting)
		{
			app.UseHttpsRedirection();
		}

		app.UseStaticFiles();
		app.UseRouting();

		// Lightweight liveness probe for Docker healthcheck and the Cake DockerSmoke task.
		// No dependencies — returns 200 as long as the process is up and serving HTTP.
		// /ready (DB-connectable check) will be added when DB-probe pattern gets factored out.
		app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

		// Build provenance: GitVersion injects APP_VERSION / GIT_SHORT_SHA / GIT_COMMIT_DATE
		// into the Docker image as env vars (see src/YobaConf.Web/Dockerfile ARG/ENV pairs).
		// Local dev has no such vars — fall through to "dev"/"local"/empty so the endpoint
		// is always available for quick "which build is this?" inspection.
		app.MapGet("/version", () => Results.Ok(new
		{
			semVer = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev",
			shortSha = Environment.GetEnvironmentVariable("GIT_SHORT_SHA") ?? "local",
			commitDate = Environment.GetEnvironmentVariable("GIT_COMMIT_DATE") ?? string.Empty,
		}));

		// Canonical config-resolve endpoint (spec §4, §8). URL path uses dot-notation
		// (`yobaproj.yobaapp.prod`); internally converted to `NodePath.ParseUrl`.
		// Catch-all `{**urlPath}` supports arbitrarily-deep paths without route template
		// constraints. Auth via `X-YobaConf-ApiKey` header or `?apiKey=` query string.
		app.MapGet("/v1/conf/{**urlPath}", ConfEndpointHandler.Handle);

		app.MapRazorPages();
	}
}
