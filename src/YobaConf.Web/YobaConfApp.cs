namespace YobaConf.Web;

// Wiring factored out of Program.cs so integration tests can build a Kestrel-hosted
// app on an ephemeral port without going through WebApplicationFactory (which hard-codes TestServer).
// The Core-level services (IConfigStore, resolve pipeline, apikeys, audit log) will be wired
// here during Phase A.
public static class YobaConfApp
{
	public static void ConfigureServices(WebApplicationBuilder builder)
	{
		ArgumentNullException.ThrowIfNull(builder);
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

		// UseHttpsRedirection skipped under "Testing" — lets Kestrel-based tests use plain http://.
		if (!isTesting)
		{
			app.UseHttpsRedirection();
		}

		app.UseStaticFiles();
		app.UseRouting();

		// Lightweight liveness probe for Docker healthcheck and the Cake DockerSmoke task.
		// No dependencies — returns 200 as long as the process is up and serving HTTP.
		// /ready (DB-connectable check) will be added in Phase A when SqliteConfigStore lands.
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

		app.MapRazorPages();
	}
}
