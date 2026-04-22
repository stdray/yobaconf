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

	// AuditLog — append-only history of every IConfigStoreAdmin write (Upsert + SoftDelete).
	// Rows are never mutated; rollback creates a new Upsert that generates a new audit entry
	// referring to the restored-from point via `Actor="restore:<id>"`.
	//
	// Value payloads (TEXT to keep one schema for Node/Variable/Secret/ApiKey):
	//   Node:      OldValue/NewValue = RawContent; Hash fields = sha256 hex.
	//   Variable:  OldValue/NewValue = plaintext Value; Hash = sha256 hex.
	//   Secret:    OldValue/NewValue = "{b64(ciphertext)}|{b64(iv)}|{b64(authTag)}|{keyVersion}".
	//              Plaintext never appears; rollback re-Upserts the encrypted tuple verbatim.
	//   ApiKey:    OldValue/NewValue = serialized (TokenPrefix|RootPath|Description). Plaintext
	//              token never stored post-creation; Hash field carries sha256(token).
	public const string CreateAuditLogTable = """
		CREATE TABLE IF NOT EXISTS AuditLog (
			Id         INTEGER PRIMARY KEY AUTOINCREMENT,
			At         INTEGER NOT NULL,
			Actor      TEXT    NOT NULL,
			Action     TEXT    NOT NULL,
			EntityType TEXT    NOT NULL,
			Path       TEXT    NOT NULL,
			EntryKey   TEXT    NULL,
			OldValue   TEXT    NULL,
			NewValue   TEXT    NULL,
			OldHash    TEXT    NULL,
			NewHash    TEXT    NULL
		);
		""";

	// Composite index for the two most common queries: "history for path (newest first)" and
	// "history for path + its descendants (newest first)". LIKE 'path%' scans the Path prefix
	// then At DESC for day-grouped timeline rendering.
	public const string CreateAuditLogPathAtIndex = """
		CREATE INDEX IF NOT EXISTS ix_auditlog_path_at
		    ON AuditLog(Path, At DESC);
		""";

	// Global "all activity" view (no path filter) — powers the admin-wide /History default.
	public const string CreateAuditLogAtIndex = """
		CREATE INDEX IF NOT EXISTS ix_auditlog_at
		    ON AuditLog(At DESC);
		""";

	public static readonly IReadOnlyList<string> AllStatements =
	[
		CreateNodesTable,
		CreateVariablesTable,
		CreateVariablesScopeKeyUniqueIndex,
		CreateSecretsTable,
		CreateSecretsScopeKeyUniqueIndex,
		CreateAuditLogTable,
		CreateAuditLogPathAtIndex,
		CreateAuditLogAtIndex,
	];
}
