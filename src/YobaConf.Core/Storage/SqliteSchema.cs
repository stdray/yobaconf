namespace YobaConf.Core.Storage;

// Schema DDL for the SQLite-backed IConfigStore. CREATE-IF-NOT-EXISTS everywhere — on
// every startup `SqliteConfigStore` replays `AllStatements` idempotently. Schema changes
// before Phase B (audit log, editing CRUD) land as additional statements here; once audit
// lands we'll need a proper migration framework, but for Phase A idempotent DDL is enough.
static class SqliteSchema
{
	// Nodes — the config tree. One row per path, immutable UNIQUE (soft-delete keeps row).
	// ContentHash = sha256(RawContent) hex, used for optimistic locking in UI (Phase B).
	public const string CreateNodesTable = """
		CREATE TABLE IF NOT EXISTS Nodes (
			Id          INTEGER PRIMARY KEY AUTOINCREMENT,
			Path        TEXT    NOT NULL UNIQUE,
			RawContent  TEXT    NOT NULL,
			ContentHash TEXT    NOT NULL,
			UpdatedAt   INTEGER NOT NULL,
			IsDeleted   INTEGER NOT NULL DEFAULT 0
		);
		""";

	// Nodes.Path unique constraint creates an automatic index; FindNode's lookup is indexed.

	// Variables — plaintext scope-bound key/value pairs.
	public const string CreateVariablesTable = """
		CREATE TABLE IF NOT EXISTS Variables (
			Id          INTEGER PRIMARY KEY AUTOINCREMENT,
			Key         TEXT    NOT NULL,
			Value       TEXT    NOT NULL,
			ScopePath   TEXT    NOT NULL,
			ContentHash TEXT    NOT NULL,
			UpdatedAt   INTEGER NOT NULL,
			IsDeleted   INTEGER NOT NULL DEFAULT 0
		);
		""";

	// Partial unique index: at most one live variable per (ScopePath, Key). Soft-deleted
	// rows allowed as duplicates so audit history can grow over time without breaking
	// new live inserts. Also covers FindVariables scope scan (index starts with ScopePath).
	public const string CreateVariablesScopeKeyUniqueIndex = """
		CREATE UNIQUE INDEX IF NOT EXISTS ux_variables_scope_key_live
		    ON Variables(ScopePath, Key) WHERE IsDeleted = 0;
		""";

	// Secrets — AES-256-GCM ciphertext + IV + AuthTag + KeyVersion (spec §3). Payload is
	// opaque to this layer; ISecretDecryptor (Phase C) handles plaintext.
	public const string CreateSecretsTable = """
		CREATE TABLE IF NOT EXISTS Secrets (
			Id             INTEGER PRIMARY KEY AUTOINCREMENT,
			Key            TEXT    NOT NULL,
			EncryptedValue BLOB    NOT NULL,
			Iv             BLOB    NOT NULL,
			AuthTag        BLOB    NOT NULL,
			KeyVersion     TEXT    NOT NULL,
			ScopePath      TEXT    NOT NULL,
			ContentHash    TEXT    NOT NULL,
			UpdatedAt      INTEGER NOT NULL,
			IsDeleted      INTEGER NOT NULL DEFAULT 0
		);
		""";

	public const string CreateSecretsScopeKeyUniqueIndex = """
		CREATE UNIQUE INDEX IF NOT EXISTS ux_secrets_scope_key_live
		    ON Secrets(ScopePath, Key) WHERE IsDeleted = 0;
		""";

	public static readonly IReadOnlyList<string> AllStatements =
	[
		CreateNodesTable,
		CreateVariablesTable,
		CreateVariablesScopeKeyUniqueIndex,
		CreateSecretsTable,
		CreateSecretsScopeKeyUniqueIndex,
	];
}
