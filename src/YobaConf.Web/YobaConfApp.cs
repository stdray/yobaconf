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
		builder.Services.AddSingleton<IApiKeyStore, ConfigApiKeyStore>();
		// TimeProvider.System by default; tests override with FakeTimeProvider if they need
		// deterministic UpdatedAt values on upsert.
		builder.Services.AddSingleton(TimeProvider.System);

		// Persist DataProtection keys across container restarts. Default behavior is a
		// fresh in-memory master key per process — every redeploy invalidates every
		// previously-issued cookie and antiforgery token ("key not found in key ring").
		// Mount `/app/data` (the same volume SQLite lives on) — the `keys` sub-dir is
		// created on first boot and chowned to the chiseled app UID via the Docker mount.
		// No XML encryptor: filesystem permissions (single-user app-UID, volume owner
		// 1654:1654 per deploy.md Step 3) are the boundary. Gated off Testing so
		// WebApplicationFactory tests don't fight over a shared keys directory.
		if (!builder.Environment.IsEnvironment("Testing"))
		{
			var dataDir = builder.Configuration["SqliteConfigStore:DataDirectory"] ?? "./data";
			var keysDir = Path.Combine(dataDir, "keys");
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

		// Phase C.5 OpenTelemetry self-emission. Gated on OpenTelemetry:Enabled == true
		// (default false in appsettings — production turns it on via env var) AND
		// !IsEnvironment("Testing") so unit/integration tests don't pay the
		// ActivityListener tax. Tests that need to assert emission opt in via their own
		// ActivityListener (see tests/YobaConf.Tests/Observability/TracingTests.cs).
		//
		// The OTLP exporter ships spans to yobalog's `/v1/traces` endpoint over HTTP/Protobuf.
		// Auth reuses YobaLog:ApiKey — same key, same workspace as CLEF logs ingestion.
		// Resource attribute `service.name` is the yobalog workspace tag used to group
		// spans by service in the waterfall UI.
		var otelEnabled = builder.Configuration.GetValue("OpenTelemetry:Enabled", false);
		var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
		if (otelEnabled
			&& !builder.Environment.IsEnvironment("Testing")
			&& !string.IsNullOrWhiteSpace(otlpEndpoint))
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

		var isTesting = app.Environment.EnvironmentName.Equals("Testing", StringComparison.Ordinal);

		if (!app.Environment.IsDevelopment() && !isTesting)
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
