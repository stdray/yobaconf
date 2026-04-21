using System.Reflection;
using Microsoft.Playwright;

namespace YobaConf.E2ETests.Infrastructure;

// Playwright tracing -> `tests/YobaConf.E2ETests/bin/<cfg>/<tfm>/artifacts/<test>.zip`.
// Always-on recording + always-save. Local dev: ~500KB per test, gitignored. CI can
// `upload-artifact: if: failure()` to keep artifacts only on red runs; greens cost nothing.
// Avoids xUnit 2.x's lack of test-outcome access in DisposeAsync — keep everything, let CI
// discriminate.
public static class TraceArtifact
{
	static readonly string ArtifactsDir = Path.Combine(AppContext.BaseDirectory, "artifacts");

	public static async Task StartAsync(IBrowserContext ctx) =>
		await ctx.Tracing.StartAsync(new TracingStartOptions
		{
			Screenshots = true,
			Snapshots = true,
			Sources = true,
		});

	public static async Task StopAndSaveAsync(IBrowserContext ctx, ITestOutputHelper output)
	{
		Directory.CreateDirectory(ArtifactsDir);
		var slug = Sanitize(ExtractTestName(output));
		var path = Path.Combine(ArtifactsDir, slug + ".zip");
		await ctx.Tracing.StopAsync(new TracingStopOptions { Path = path });
	}

	// xUnit 2.x ITestOutputHelper has a non-public `test` field carrying the running ITest
	// instance. DisplayName = `Namespace.Class.Method(params)` for a [Fact]. Stable across 2.x.
	static string ExtractTestName(ITestOutputHelper output)
	{
		var field = output.GetType().GetField("test", BindingFlags.Instance | BindingFlags.NonPublic);
		if (field?.GetValue(output) is ITest test)
			return test.DisplayName;
		return "unknown-" + Guid.NewGuid().ToString("N")[..8];
	}

	static string Sanitize(string s)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var chars = s.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray();
		var result = new string(chars);
		return result.Length > 120 ? result[..120] : result;
	}
}
