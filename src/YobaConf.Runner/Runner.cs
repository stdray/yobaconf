using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace YobaConf.Runner;

// Exit-code contract (spec §9.2). Consumers / orchestrators rely on these for healthcheck
// + crashloop decisions — don't re-number after it ships.
public static class ExitCodes
{
	public const int Success = 0;
	public const int Conflict = 2;          // 409 from /v1/conf — admin must add overlay
	public const int ScopeMismatch = 3;     // 403 from /v1/conf — api-key doesn't cover the request
	public const int ConnectionError = 4;   // Fetch failed (DNS, timeout, 5xx, 4xx other than 403/409)
	public const int InvalidArgs = 5;       // Runner invocation couldn't parse
}

public sealed record RunnerOptions(
	string Endpoint,
	string ApiKey,
	IReadOnlyDictionary<string, string> Tags,
	string? Template,
	IReadOnlyList<string> ChildArgs);

// Abstracts the "apply env vars and exec the child" step so tests can observe what the
// runner would launch without actually spawning a subprocess.
public interface IChildExec
{
	Task<int> RunAsync(IReadOnlyDictionary<string, string> env, IReadOnlyList<string> childArgs, CancellationToken ct);
}

public sealed class Runner
{
	readonly HttpClient _http;
	readonly IChildExec _exec;
	readonly TextWriter _stderr;

	public Runner(HttpClient http, IChildExec exec, TextWriter stderr)
	{
		_http = http;
		_exec = exec;
		_stderr = stderr;
	}

	public async Task<int> RunAsync(RunnerOptions options, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(options);

		// The conf endpoint expects query-string tag params plus optional `template`. We
		// carry the api-key on the X-YobaConf-ApiKey header rather than `?apiKey=` so
		// caller-side access logs don't leak the token in URLs.
		var query = BuildQuery(options.Tags, options.Template);
		var uri = new Uri(new Uri(options.Endpoint.TrimEnd('/') + "/"), $"v1/conf{query}");

		using var request = new HttpRequestMessage(HttpMethod.Get, uri);
		request.Headers.TryAddWithoutValidation("X-YobaConf-ApiKey", options.ApiKey);

		HttpResponseMessage response;
		try
		{
			response = await _http.SendAsync(request, ct).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
		{
			await _stderr.WriteLineAsync($"yobaconf-run: connection error to {options.Endpoint}: {ex.Message}").ConfigureAwait(false);
			return ExitCodes.ConnectionError;
		}

		using (response)
		{
			switch (response.StatusCode)
			{
				case HttpStatusCode.OK:
					var env = await ParseFlatJsonAsync(response, ct).ConfigureAwait(false);
					if (env is null)
					{
						await _stderr.WriteLineAsync("yobaconf-run: server returned non-flat JSON; use --template flat|dotnet|envvar|envvar-deep").ConfigureAwait(false);
						return ExitCodes.ConnectionError;
					}
					return await _exec.RunAsync(env, options.ChildArgs, ct).ConfigureAwait(false);

				case HttpStatusCode.Conflict:
					var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
					await _stderr.WriteLineAsync("yobaconf-run: 409 conflict from /v1/conf — admin must add a more-specific overlay:").ConfigureAwait(false);
					await _stderr.WriteLineAsync(body).ConfigureAwait(false);
					return ExitCodes.Conflict;

				case HttpStatusCode.Forbidden:
					await _stderr.WriteLineAsync("yobaconf-run: 403 scope mismatch — api-key doesn't cover the requested tag-vector").ConfigureAwait(false);
					return ExitCodes.ScopeMismatch;

				default:
					var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
					await _stderr.WriteLineAsync($"yobaconf-run: {(int)response.StatusCode} from /v1/conf: {errorBody}").ConfigureAwait(false);
					return ExitCodes.ConnectionError;
			}
		}
	}

	static string BuildQuery(IReadOnlyDictionary<string, string> tags, string? template)
	{
		var parts = new List<string>(tags.Count + 1);
		foreach (var (k, v) in tags)
			parts.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}");
		if (!string.IsNullOrEmpty(template))
			parts.Add($"template={Uri.EscapeDataString(template)}");
		return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
	}

	// Parses the response body into a flat string dictionary. Non-flat (nested) responses
	// from `template=flat` — or the default — aren't usable as env vars; the caller should
	// pick a non-Flat template explicitly.
	static async Task<Dictionary<string, string>?> ParseFlatJsonAsync(HttpResponseMessage response, CancellationToken ct)
	{
		using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(ct).ConfigureAwait(false);
		if (doc?.RootElement.ValueKind != JsonValueKind.Object) return null;

		var env = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var prop in doc.RootElement.EnumerateObject())
		{
			// Nested object = Flat template response. Not usable as env vars.
			if (prop.Value.ValueKind == JsonValueKind.Object) return null;
			env[prop.Name] = StringifyValue(prop.Value);
		}
		return env;
	}

	static string StringifyValue(JsonElement el) => el.ValueKind switch
	{
		JsonValueKind.String => el.GetString() ?? string.Empty,
		JsonValueKind.Number => el.GetRawText(),
		JsonValueKind.True => "true",
		JsonValueKind.False => "false",
		JsonValueKind.Null => string.Empty,
		// Array / nested object — stringify as raw JSON for app-level parsing. Consumers
		// wanting typed access should avoid these values in their bindings.
		_ => el.GetRawText(),
	};
}
