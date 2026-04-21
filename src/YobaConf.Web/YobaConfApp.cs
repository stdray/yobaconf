using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using YobaConf.Core;
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

		// ForwardedHeaders must come before UseHttpsRedirection + UseAuthentication so
		// scheme-aware middleware sees the real client scheme (spec §11, Caddy).
		app.UseForwardedHeaders();

		// Tests use plain http:// so skip HTTPS redirection in Testing env.
		if (!isTesting)
		{
			app.UseHttpsRedirection();
		}

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
