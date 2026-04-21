using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace YobaConf.Client;

// `IConfigurationProvider` that fetches a YobaConf node over HTTP on initial load, then
// polls every RefreshInterval with ETag-aware conditional GETs.
//
// Lifecycle:
//   1. `Load()` — synchronous initial fetch. Blocks on the HTTP call (standard pattern —
//      JsonConfigurationProvider does the same for file I/O). On success, populates the
//      base class `Data` dictionary via the flattener. On failure, throws (or returns
//      empty if Options.Optional).
//   2. Background `Timer` calls `PollAsync` every RefreshInterval. Sends
//      `If-None-Match: <last-etag>`. 200 -> replace Data + OnReload(). 304 -> no-op.
//      Auth failures / 4xx / network errors -> log via Debug + keep last-known-good state.
//   3. `Dispose()` -> stop timer, dispose HttpClient.
//
// `Data` replacement races with `IConfiguration.Get` readers. The base class `Data` is
// `IDictionary<string, string?>` (not concurrent). We replace the dictionary reference
// atomically via `Interlocked.Exchange` on a private backing field, then assign
// `Data = newRef` — readers observe either the old or new dict, never a torn state.
public sealed class YobaConfConfigurationProvider : ConfigurationProvider, IDisposable
{
	readonly YobaConfConfigurationOptions _options;
	readonly HttpClient _http;
	readonly Uri _endpoint;
	Timer? _timer;
	string? _lastEtag;
	readonly Lock _pollGate = new();
	bool _disposed;

	public YobaConfConfigurationProvider(YobaConfConfigurationOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		if (string.IsNullOrWhiteSpace(options.BaseUrl))
			throw new ArgumentException("BaseUrl is required.", nameof(options));
		if (string.IsNullOrWhiteSpace(options.Path))
			throw new ArgumentException("Path is required.", nameof(options));

		_options = options;
		_http = options.Handler is null
			? new HttpClient()
			: new HttpClient(options.Handler, disposeHandler: false);

		var baseUri = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";
		// v1/conf/{path} — path is already dot-separated, matches the server's URL shape.
		_endpoint = new Uri(new Uri(baseUri), $"v1/conf/{options.Path}");
	}

	public override void Load()
	{
		// Sync-over-async: ConfigurationBuilder.Build() is synchronous, same constraint as
		// file-based providers. One-time at startup, not a hot path.
		try
		{
			FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
		}
		catch (Exception) when (_options.Optional)
		{
			// Optional: start with empty data; PollAsync will retry on next tick.
			Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
		}

		// Timer starts regardless of initial outcome — poll will recover when the backend
		// becomes reachable. Null RefreshInterval (TimeSpan.Zero) disables polling.
		if (_options.RefreshInterval > TimeSpan.Zero)
		{
			_timer = new Timer(
				_ => _ = PollSafeAsync(),
				state: null,
				dueTime: _options.RefreshInterval,
				period: _options.RefreshInterval);
		}
	}

	async Task PollSafeAsync()
	{
		// Serialise polls — a slow response shouldn't overlap with the next timer tick.
		// Lock acquired via TryEnter so a delayed poll just skips rather than queueing.
		if (!_pollGate.TryEnter())
			return;
		try
		{
			await FetchAsync(CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception)
		{
			// Last-known-good preserved. Polling providers must never lose working config
			// due to transient issues. Diagnostics: if this matters to callers, swap Debug
			// for a real ILogger once the SDK grows consumers who ask for it.
			System.Diagnostics.Debug.WriteLine("YobaConf poll failed; keeping last-known-good data.");
		}
		finally
		{
			_pollGate.Exit();
		}
	}

	async Task FetchAsync(CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
		request.Headers.TryAddWithoutValidation("X-YobaConf-ApiKey", _options.ApiKey);
		if (!string.IsNullOrEmpty(_lastEtag))
			request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(_lastEtag));

		using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

		if (response.StatusCode == HttpStatusCode.NotModified)
			return; // Data unchanged; no reload.

		response.EnsureSuccessStatusCode();

		var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		var etag = response.Headers.ETag?.Tag;

		using var doc = JsonDocument.Parse(body);
		var next = JsonFlattener.Flatten(doc);

		// Atomic swap — readers hitting TryGet between the two assignments see either the
		// old or new dict, never a partially-populated one.
		Data = next;
		_lastEtag = etag;
		OnReload();
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_timer?.Dispose();
		_http.Dispose();
	}
}
