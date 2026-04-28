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
using YobaConf.Core.Bindings;
using YobaConf.Core.Observability;
using YobaConf.Core.Resolve;
using YobaConf.Core.Security;
using YobaConf.Core.Storage;
using YobaConf.Web.Endpoints;

namespace YobaConf.Web;

// v2 skeleton — v1 path-tree / HOCON / ResolvePipeline / SqliteConfigStore / ConfEndpointHandler
// all purged in Phase A.0. Storage + resolve come back in A.1 / A.2 / A.4; until then the app
// serves Login + Error + probes only, enough for the Razor-pages pipeline to boot and E2E
// login-flow to work.
public static class YobaConfApp
{
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Caddy on the host terminates TLS on :443 and reverse-proxies to 127.0.0.1:8081
        // (spec §12). Without this wiring HttpContext.Request.IsHttps = false behind the
        // loopback proxy — UseHttpsRedirection would loop 307, cookie Secure-flag wrong.
        // Only loopback is trusted; other X-Forwarded-* sources are ignored.
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            o.KnownProxies.Add(IPAddress.Loopback);
            o.KnownProxies.Add(IPAddress.IPv6Loopback);
        });

        builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));
        // Resolve conflict tie-breaker options — default-off (UsePriorityTieBreaker=false).
        // Set via appsettings.json section ResolveConflicts or env var ResolveConflicts__UsePriorityTieBreaker.
        builder.Services.Configure<ResolveOptions>(builder.Configuration.GetSection("ResolveConflicts"));
        // Bootstrap api-keys from config — read by ConfigApiKeyStore. Empty section =
        // no bootstrap keys, only SqliteApiKeyStore is consulted.
        builder.Services.Configure<Core.Auth.BootstrapApiKeyOptions>(
            builder.Configuration.GetSection("BootstrapApiKeys"));

        // SQLite-backed bindings store (A.1). Config-gated so integration tests without
        // `Storage:DataDirectory` set boot without a store — /ready then returns 200 as
        // "service up, no store". Prod appsettings ships `Storage:DataDirectory=./data`;
        // GitHub Actions / Dockerfile override to an absolute mounted volume.
        var storageDir = builder.Configuration["Storage:DataDirectory"];
        if (!string.IsNullOrWhiteSpace(storageDir))
        {
            builder.Services.Configure<SqliteBindingStoreOptions>(builder.Configuration.GetSection("Storage"));
            builder.Services.AddSingleton<SqliteBindingStore>();
            builder.Services.AddSingleton<IBindingStore>(sp => sp.GetRequiredService<SqliteBindingStore>());
            builder.Services.AddSingleton<IBindingStoreAdmin>(sp => sp.GetRequiredService<SqliteBindingStore>());

            // Api-keys store shares the same DB file (one SQLite database, multiple tables).
            // IApiKeyStore is wrapped in CompositeApiKeyStore so bootstrap keys from
            // appsettings (BootstrapApiKeys) validate alongside admin-minted keys. IApiKeyAdmin
            // stays SQLite-only — config keys are immutable from the admin UI.
            builder.Services.AddSingleton<SqliteApiKeyStore>();
            builder.Services.AddSingleton<Core.Auth.ConfigApiKeyStore>();
            builder.Services.AddSingleton<Core.Auth.IApiKeyStore>(sp =>
                new Core.Auth.CompositeApiKeyStore(
                    sp.GetRequiredService<Core.Auth.ConfigApiKeyStore>(),
                    sp.GetRequiredService<SqliteApiKeyStore>()));
            builder.Services.AddSingleton<Core.Auth.IApiKeyAdmin>(sp => sp.GetRequiredService<SqliteApiKeyStore>());

            // Users store — cookie-auth admin accounts. Login falls back to config-admin when
            // the table is empty (bootstrap); once the first DB user lands the config path is
            // ignored until the DB is emptied again.
            builder.Services.AddSingleton<SqliteUserStore>();
            builder.Services.AddSingleton<Core.Auth.IUserStore>(sp => sp.GetRequiredService<SqliteUserStore>());
            builder.Services.AddSingleton<Core.Auth.IUserAdmin>(sp => sp.GetRequiredService<SqliteUserStore>());

            // Admin tokens — personal access tokens for the admin JSON API (Phase G.1).
            // Shares the binding-store DB file. Auth middleware (G.2) calls Validate;
            // /Admin/Profile (G.5) calls IAdminTokenAdmin for the per-user CRUD surface.
            builder.Services.AddSingleton<SqliteAdminTokenStore>();
            builder.Services.AddSingleton<Core.Auth.IAdminTokenStore>(sp => sp.GetRequiredService<SqliteAdminTokenStore>());
            builder.Services.AddSingleton<Core.Auth.IAdminTokenAdmin>(sp => sp.GetRequiredService<SqliteAdminTokenStore>());

            // Audit log — read surface only; writes flow through the admin stores (spec §7
            // invariant: "only storage impl populates").
            builder.Services.AddSingleton<SqliteAuditLogStore>();
            builder.Services.AddSingleton<Core.Audit.IAuditLogStore>(sp => sp.GetRequiredService<SqliteAuditLogStore>());

            // Tag vocabulary (E.2) — declarative set of known tag-keys + optional allowed
            // values. Non-blocking: bindings editor surfaces a warning when used key is
            // missing; empty vocab = free-form (no warnings).
            builder.Services.AddSingleton<SqliteTagVocabularyStore>();
            builder.Services.AddSingleton<Core.Tags.ITagVocabularyStore>(sp => sp.GetRequiredService<SqliteTagVocabularyStore>());
            builder.Services.AddSingleton<Core.Tags.ITagVocabularyAdmin>(sp => sp.GetRequiredService<SqliteTagVocabularyStore>());
        }

        // AES-256-GCM secrets encryption — master key from env var YOBACONF_MASTER_KEY
        // (base64, 32 bytes decoded). Empty/missing = encryptor not registered; A.2 resolve
        // pipeline raises a clear error only if secret-bindings are in scope without an
        // encryptor. Prod injects via GH secrets; dev via `dotnet user-secrets`.
        var masterKey = builder.Configuration["YOBACONF_MASTER_KEY"];
        if (!string.IsNullOrWhiteSpace(masterKey))
            builder.Services.AddSingleton<ISecretEncryptor>(new AesGcmSecretEncryptor(masterKey));

        // TimeProvider.System by default; tests override with FakeTimeProvider for
        // deterministic UpdatedAt values on upsert.
        builder.Services.AddSingleton(TimeProvider.System);

        // Transient secret-reveal cache (E.3). 10s TTL on the per-user cache entry.
        builder.Services.AddMemoryCache();

        // Persist DataProtection keys across container restarts. Without this, every redeploy
        // regenerates the in-memory master key and invalidates prior auth-cookies / antiforgery
        // tokens — users get logged out on every deploy. Config-driven: empty
        // `DataProtection:KeysDirectory` = no persistence (tests, dev-without-secrets);
        // prod Dockerfile sets `DataProtection__KeysDirectory=/app/data/keys`.
        var keysDir = builder.Configuration["DataProtection:KeysDirectory"];
        if (!string.IsNullOrWhiteSpace(keysDir))
        {
            Directory.CreateDirectory(keysDir);
            builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
                .SetApplicationName("yobaconf");
        }

        // Cookie-auth for admin UI. Until B.1 (SqliteUserStore) lands, login falls back to
        // config-admin via AdminOptions. Multi-admin DB-backed users come in B.1.
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.LoginPath = "/Login";
                o.AccessDeniedPath = "/Login";
                o.ExpireTimeSpan = TimeSpan.FromDays(7);
                o.SlidingExpiration = true;
            });

        // Fallback policy: every Razor Page + endpoint requires auth unless explicitly
        // opted out. Login carries [AllowAnonymous]; probes use .AllowAnonymous().
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        builder.Services.AddRazorPages();

        // OpenTelemetry self-emission. Gated on `OpenTelemetry:Enabled == true` AND non-empty
        // `OpenTelemetry:OtlpEndpoint`. Default appsettings has both off, so dev/tests don't
        // pay the ActivityListener tax; prod turns both on via env vars. The OTLP exporter
        // ships spans to yobalog's `/v1/traces` (HTTP/Protobuf, X-Seq-ApiKey auth — same key
        // as CLEF logs). ActivitySource names will rewire in A.5 to tagged-resolve stages.
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
                        opts.Filter = ctx =>
                            !ctx.Request.Path.StartsWithSegments("/health")
                            && !ctx.Request.Path.StartsWithSegments("/ready");
                    })
                    .AddOtlpExporter(o =>
                    {
                        o.Endpoint = new Uri(otlpEndpoint);
                        o.Protocol = OtlpExportProtocol.HttpProtobuf;
                        o.Headers = $"X-Seq-ApiKey={apiKey}";
                    }));
        }
    }

    public static void Configure(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        // ForwardedHeaders before auth so scheme-aware middleware sees the real client
        // scheme (spec §12, Caddy). No UseHttpsRedirection — Caddy on the host handles it.
        app.UseForwardedHeaders();

        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        // Liveness probe — public (Docker healthcheck, Cake DockerSmoke). Returns 200 as long
        // as the process is up and serving HTTP. MapMethods covers GET + HEAD (spec invariant
        // from yobalog: HEAD probes must hit the route, not fall through to cookie-auth challenge).
        app.MapMethods("/health", ["GET", "HEAD"], () => Results.Ok(new { status = "healthy" }))
            .AllowAnonymous();

        // Readiness probe — if the SQLite store is registered, touch it to confirm the
        // file + schema are reachable. Unregistered store (integration tests, or the very
        // early bootstrap state) still reports ready.
        app.MapMethods("/ready", ["GET", "HEAD"], (IServiceProvider sp) =>
        {
            var store = sp.GetService<IBindingStore>();
            if (store is null)
                return Results.Ok(new { status = "ready" });
            try
            {
                _ = store.ListActive();
                return Results.Ok(new { status = "ready" });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "storage unavailable",
                    detail: ex.Message);
            }
        }).AllowAnonymous();

        // Build provenance — public. GitVersion injects via Docker env vars.
        app.MapMethods("/version", ["GET", "HEAD"], () => Results.Ok(new
        {
            semVer = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev",
            shortSha = Environment.GetEnvironmentVariable("GIT_SHORT_SHA") ?? "local",
            commitDate = Environment.GetEnvironmentVariable("GIT_COMMIT_DATE") ?? string.Empty,
        })).AllowAnonymous();

        // Admin logout — POST-only, requires auth (no AllowAnonymous).
        app.MapPost("/Logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/Login");
        });

        app.MapConfEndpoint();

        // /v1/admin/* — JSON admin-API for scripting, gated by admin-token auth (Phase G).
        // The group bypasses the cookie-auth fallback policy via AllowAnonymous (set in
        // AdminTokenAuth.RequireAdminToken); the AdminTokenAuthFilter is the actual gate.
        var adminGroup = app.MapGroup("/v1/admin").RequireAdminToken();
        adminGroup.MapAdminBindings();
        adminGroup.MapAdminApiKeys();

        app.MapRazorPages();
    }
}
