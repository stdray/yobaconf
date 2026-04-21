using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace YobaConf.Core;

// Reads plaintext API-key entries from appsettings on construction, validates tokens in
// constant time. Admin-only; SqliteApiKeyStore (Phase B) adds DB-backed rotation on top
// via a CompositeApiKeyStore fronting both. ConfigApiKeyStore alone is enough for Phase A
// dog-food — a couple of master-ish tokens in appsettings/env vars.
//
// Invalid RootPath in config fails-fast at construction with ArgumentException — better
// to crash at startup than serve wrong scope at runtime.
public sealed class ConfigApiKeyStore : IApiKeyStore
{
	readonly IReadOnlyList<Entry> entries;

	public ConfigApiKeyStore(IOptions<ApiKeyOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		entries = [.. options.Value.Keys.Select(e => new Entry(
			TokenBytes: Encoding.UTF8.GetBytes(e.Token),
			Token: e.Token,
			RootPath: NodePath.ParseDb(e.RootPath),
			Description: e.Description))];
	}

	public ApiKey? Validate(string plaintextToken)
	{
		ArgumentNullException.ThrowIfNull(plaintextToken);
		if (entries.Count == 0)
			return null;

		var inputBytes = Encoding.UTF8.GetBytes(plaintextToken);
		foreach (var e in entries)
		{
			// FixedTimeEquals requires equal-length buffers. Length mismatch short-circuits —
			// leaks token length but not the token content. Acceptable for pet-scale; if
			// paranoia kicks in, pad buffers to a fixed max length first.
			if (e.TokenBytes.Length != inputBytes.Length)
				continue;
			if (CryptographicOperations.FixedTimeEquals(e.TokenBytes, inputBytes))
			{
				return new ApiKey(
					TokenHash: string.Empty,
					TokenPrefix: e.Token.Length >= 6 ? e.Token[..6] : e.Token,
					RootPath: e.RootPath,
					Description: e.Description);
			}
		}
		return null;
	}

	sealed record Entry(byte[] TokenBytes, string Token, NodePath RootPath, string Description);
}
