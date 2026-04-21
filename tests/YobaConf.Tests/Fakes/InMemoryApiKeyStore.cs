namespace YobaConf.Tests.Fakes;

// Test double for IApiKeyStore. Integration tests seed a list of (token, RootPath) pairs
// and swap the real ConfigApiKeyStore for this fake via `WithWebHostBuilder`. Plaintext
// token comparison is ordinal — timing-safety is a production concern that belongs in
// ConfigApiKeyStore alone.
public sealed class InMemoryApiKeyStore : IApiKeyStore
{
	readonly IReadOnlyList<(string Token, ApiKey Key)> entries;

	public InMemoryApiKeyStore(IEnumerable<(string Token, NodePath RootPath, string Description)> keys)
	{
		entries = [.. keys.Select(k => (
			k.Token,
			new ApiKey(
				TokenHash: string.Empty,
				TokenPrefix: k.Token.Length >= 6 ? k.Token[..6] : k.Token,
				RootPath: k.RootPath,
				Description: k.Description)))];
	}

	public ApiKey? Validate(string plaintextToken) =>
		entries.FirstOrDefault(e => e.Token == plaintextToken).Key;
}
