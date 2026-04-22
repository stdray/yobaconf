using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using YobaConf.Core;
using YobaConf.Core.Observability;
using YobaConf.Core.Security;
using YobaConf.Core.Storage;

namespace YobaConf.Web;

// Wiring factored out of Program.cs so integration tests can spin up the pipeline via
// WebApplicationFactory<Program> and override DI for test fakes.
public static class YobaConfApp
{
	public static void ConfigureServices(WebApplicationBuilder builder)
	{
		ArgumentNullException.ThrowIfNull(builder);

		// Caddy on the host terminates TLS on :443 and reverse-proxies to 127.0.0.1:8081
		// (spec §11). Without this wiring HttpContext.Request.IsHttps = false behind the
		// loopback proxy — UseHttpsRedirection loops 307, cookie Secure-flag wrong.
		// Only loopback is trusted; other X-Forwarded-* sources are ignored.
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
		builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));

		// SqliteConfigStore implements both IConfigStore (reads) and IConfigStoreAdmin
		// (writes). Same singleton under both interfaces: the resolve pipeline takes
		// IConfigStore → can't accidentally write; admin-UI takes IConfigStoreAdmin.
		builder.Services.AddSingleton<SqliteConfigStore>();
		builder.Services.AddSingleton<IConfigStore>(sp => sp.GetRequiredService<SqliteConfigStore>());
		builder.Services.AddSingleton<IConfigStoreAdmin>(sp => sp.GetRequiredService<SqliteConfigStore>());
		builder.Services.AddSingleton<IAuditLogStore>(sp => sp.GetRequiredService<SqliteConfigStore>());
		builder.Services.AddSingleton<IApiKeyStore, ConfigApiKeyStore>();

		// AES-256-GCM secrets encryption (Phase C). Master key from env var
		// YOBACONF_MASTER_KEY (base64, 32 bytes decoded). Empty/missing = encryptor not
		// registered; ConfEndpointHandler / Node page fetch via GetService so null is
		// handled, and Resolve raises a clear error only if secrets are in scope without
		// an encryptor. Prod containers inject the env from GH secrets; dev sets it via
		// `dotnet user-secrets set YOBACONF_MASTER_KEY ...`; tests leave it unset by
		// default and secret-aware tests register a stub via service override.
		var masterKey = builder.Configuration["YOBACONF_MASTER_KEY"];
		if (!string.IsNullOrWhiteSpace(masterKey))
			builder.Services.AddSingleton<ISecretEncryptor>(new AesGcmSecretEncryptor(masterKey));
		// TimeProvider.System by default; tests override with FakeTimeProvider if they need
		// deterministic UpdatedAt values on upsert.
		builder.Services.AddSingleton(TimeProvider.System);

		// Persist DataProtection keys across container restarts. Default ASP.NET behavior
		// is an in-memory master key regenerated per process — every redeploy then
		// invalidates every prior cookie + antiforgery token ("key not found in key ring").
		// Config-driven: empty `DataProtection:KeysDirectory` = no persistence (tests and
		// dev-without-user-secrets fall here, getting the harmless in-memory default);
		// prod sets it via ENV in the Dockerfile (`DataProtection__KeysDirectory=/app/data/keys`)
		// pointing at the mounted data volume that survives redeploys. No XML encryptor:
		// filesystem permissions (single-user app-UID, volume chowned 1654:1654 per
		// deploy.md Step 3) are the boundary.
		var keysDir = builder.Configuration["DataProtection:KeysDirectory"];
		if (!string.IsNullOrWhiteSpace(keysDir))
		{
			Directory.CreateDirectory(keysDir);
			builder.Services.AddDataProtection()
				.PersistKeysToFileSystem(new DirectoryInfo(keysDir))
				.SetApplicationName("yobaconf");
		}

		// Cookie-auth for admin UI. Single admin (AdminOptions: username + PBKDF2 hash).
		// Multi-admin with a DB-backed user store is Phase B+.
		builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
			.AddCookie(o =>
			{
				o.LoginPath = "/Login";
				o.AccessDeniedPath = "/Login";
				o.ExpireTimeSpan = TimeSpan.FromDays(7);
				o.SlidingExpiration = true;
			});

		// Fallback policy: every Razor Page + endpoint requires auth unless explicitly
		// opted out. Login page carries [AllowAnonymous]; API endpoints (/v1/conf,
		// /health, /version) use .AllowAnonymous() since they have their own auth
		// mechanisms (API-key) or are intentionally public.
		builder.Services.AddAuthorizationBuilder()
			.SetFallbackPolicy(new AuthorizationPolicyBuilder()
				.RequireAuthenticatedUser()
				.Build());

		builder.Services.AddRazorPages();

		// Phase C.5 OpenTelemetry self-emission. Gated on `OpenTelemetry:Enabled == true`
		// AND non-empty `OpenTelemetry:OtlpEndpoint`. Default appsettings has both off,
		// so dev/tests don't register the exporter; prod turns both on via env vars. No
		// environment-name check — Testing env inherits the same config-driven gate, and
		// tests that want to exercise emission register their own ActivityListener
		// (see tests/YobaConf.Tests/Observability/TracingTests.cs).
		//
		// The OTLP exporter ships spans to yobalog's `/v1/traces` endpoint over HTTP/Protobuf.
		// Auth reuses YobaLog:ApiKey — same key, same workspace as CLEF logs ingestion.
		// Resource attribute `service.name` is the yobalog workspace tag used to group
		// spans by service in the waterfall UI.
		var otelEnabled = builder.Configuration.GetValue("OpenTelemetry:Enabled", false);
		var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
		if (otelEnabled && !string.IsNullOrWhiteSpace(otlpEndpoint))
		{
			var serviceName = builder.Configuration["OpenTelemetry:ServiceName"] ?? "yobaconf";
			var serviceVersion = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev";
			var apiKey = builder.Configuration["YobaLog:ApiKey"] ?? string.Empty;

			builder.Services.AddOpenTelemetry()
				.ConfigureResource(r => r.AddService(serviceName, serviceVersion: serviceVersion))
				.WithTracing(tracing => tracing
					.AddSource(
						ActivitySources.ResolveSourceName,
						ActivitySources.StorageSqliteSourceName)
					.AddAspNetCoreInstrumentation(opts =>
					{
						// Skip probe noise — /health /ready get hit every few seconds by
						// Docker healthcheck / Caddy probe / future k8s readiness; filling
						// yobalog's spans.db with these is pure waste.
						opts.Filter = ctx =>
							!ctx.Request.Path.StartsWithSegments("/health")
							&& !ctx.Request.Path.StartsWithSegments("/ready");
					})
					.AddOtlpExporter(o =>
					{
						o.Endpoint = new Uri(otlpEndpoint);
						o.Protocol = OtlpExportProtocol.HttpProtobuf;
						// X-Seq-ApiKey auth — yobalog's OTLP endpoint reuses the Seq-compat
						// auth header (spec yobalog doc/spec.md §1). Same key value as logs.
						o.Headers = $"X-Seq-ApiKey={apiKey}";
					}));
		}
	}

	public static void Configure(WebApplication app)
	{
		ArgumentNullException.ThrowIfNull(app);

		// Standard ASP.NET template pattern: production error handling + HSTS outside
		// Development. No Testing-env special case — integration tests via
		// WebApplicationFactory land here too and behave like prod; tests that want
		// to assert on specific error status-codes hit endpoints directly, which
		// short-circuit the exception handler.
		if (!app.Environment.IsDevelopment())
		{
			app.UseExceptionHandler("/Error");
			app.UseHsts();
		}

		// ForwardedHeaders must come before UseAuthentication so scheme-aware middleware
		// sees the real client scheme (spec §11, Caddy). No UseHttpsRedirection inside the
		// app: Caddy on the host already redirects :80 -> :443 at the edge, and the app's
		// own upstream connection from Caddy is loopback HTTP by design. A second layer of
		// redirect inside ASP.NET would be dead code at best and a "HTTPS port not
		// determined" warning at worst — which is exactly what Kestrel emitted before this
		// was dropped.
		app.UseForwardedHeaders();

		app.UseStaticFiles();
		app.UseRouting();
		app.UseAuthentication();
		app.UseAuthorization();

		// Liveness probe — public (Docker healthcheck, Cake DockerSmoke). No dependencies.
		// Returns 200 as long as the process is up and serving HTTP; use /ready for
		// "can-serve-requests" semantics (DB reachable).
		app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

		// Readiness probe — exercises the config store (DB reachability). Returns 503 when
		// `IConfigStore.ListNodePaths()` throws so orchestrators can evict the instance from
		// load-balancer pools while startup migrations or disk issues resolve. Anonymous —
		// probes don't carry auth and a failing probe must not require credentials to observe.
		app.MapGet("/ready", (IConfigStore store) =>
		{
			try
			{
				_ = store.ListNodePaths();
				return Results.Ok(new { status = "ready" });
			}
			catch (Exception ex)
			{
				return Results.Json(
					new { status = "not ready", error = ex.Message },
					statusCode: StatusCodes.Status503ServiceUnavailable);
			}
		}).AllowAnonymous();

		// Build provenance — public. GitVersion injects via Docker env vars; local dev
		// falls through to dev/local/empty.
		app.MapGet("/version", () => Results.Ok(new
		{
			semVer = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev",
			shortSha = Environment.GetEnvironmentVariable("GIT_SHORT_SHA") ?? "local",
			commitDate = Environment.GetEnvironmentVariable("GIT_COMMIT_DATE") ?? string.Empty,
		})).AllowAnonymous();

		// Canonical config-resolve — API-key auth (not cookie), so AllowAnonymous to the
		// cookie-auth fallback. ConfEndpointHandler enforces its own auth inside.
		app.MapGet("/v1/conf/{**urlPath}", ConfEndpointHandler.Handle).AllowAnonymous();

		// Admin logout — POST-only. Requires auth (no AllowAnonymous), so the cookie
		// middleware 401s unauthenticated callers before SignOutAsync runs.
		app.MapPost("/Logout", async (HttpContext ctx) =>
		{
			await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
			return Results.Redirect("/Login");
		});

		app.MapRazorPages();
	}
}
