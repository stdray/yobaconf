namespace YobaConf.Core;

// A validated API key with its scope. TokenHash + TokenPrefix are for UI identification
// (spec §3); ConfigApiKeyStore leaves them empty/short since config-backed keys are
// plaintext in appsettings and have no DB row. SqliteApiKeyStore (Phase B) will populate
// both properly.
public sealed record ApiKey(
	string TokenHash,
	string TokenPrefix,
	NodePath RootPath,
	string Description);
