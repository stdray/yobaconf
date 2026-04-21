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
		app.MapRazorPages();
	}
}
