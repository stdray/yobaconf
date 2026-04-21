namespace YobaConf.Core;

// appsettings.json shape for the config-backed API-key store (spec §2 admin backdoor).
// Example:
//   "ApiKeys": {
//     "Keys": [
//       { "Token": "<shortguid-22-chars>", "RootPath": "", "Description": "master" },
//       { "Token": "<another>", "RootPath": "projects/yoba", "Description": "yoba-runtime" }
//     ]
//   }
// Plaintext lives in config so `docker run -e ApiKeys__Keys__0__Token=...` or a mounted
// appsettings.Production.json both work. DB-backed rotation is Phase B (SqliteApiKeyStore).
public sealed record ApiKeyOptions
{
	public IReadOnlyList<ApiKeyConfigEntry> Keys { get; init; } = [];
}

public sealed record ApiKeyConfigEntry
{
	public string Token { get; init; } = string.Empty;
	public string RootPath { get; init; } = string.Empty;
	public string Description { get; init; } = string.Empty;
}
