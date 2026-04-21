namespace YobaConf.Client;

// Caller-provided knobs for AddYobaConf. `Path` uses dot-notation (matches URL shape at
// /v1/conf/{path}): `projects.yoba.prod`, not slash-form.
//
// `Handler` is an escape hatch for tests — inject the WebApplicationFactory's
// Server.CreateHandler() so the SDK talks to an in-process yobaconf without touching the
// network. Production uses the default SocketsHttpHandler.
public sealed class YobaConfConfigurationOptions
{
	// e.g. "https://yobaconf.3po.su". Trailing slash optional — normalised internally.
	public string BaseUrl { get; set; } = string.Empty;

	// Plaintext token. Sent as `X-YobaConf-ApiKey` header on every request.
	public string ApiKey { get; set; } = string.Empty;

	// Dot-separated node path — same shape as the URL segment. `projects.yoba.prod`.
	public string Path { get; set; } = string.Empty;

	// How often to re-poll for changes. Each poll uses `If-None-Match: <etag>`, so 304s
	// are cheap. Minimum sensible value is a few seconds; the SDK doesn't enforce a floor.
	public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

	// When true, initial load failures (404, auth errors, network errors) don't throw —
	// the provider starts with empty data and retries on the next poll tick. Matches the
	// ConfigurationBuilder `optional: true` convention. Default false: missing config at
	// startup is usually a broken-deploy signal, fail-fast surfaces it.
	public bool Optional { get; set; }

	// Testing-only: inject a custom HttpMessageHandler (e.g. WebApplicationFactory's
	// TestServer handler). Production leaves this null and the provider builds its own
	// HttpClient with the default handler.
	public HttpMessageHandler? Handler { get; set; }
}
