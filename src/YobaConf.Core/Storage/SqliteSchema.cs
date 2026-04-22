namespace YobaConf.Core.Storage;

// DDL for the tagged-v2 SQLite schema. CREATE-IF-NOT-EXISTS everywhere — SqliteBindingStore
// replays `AllStatements` on every startup. Four tables land together in A.1 even though
// only Bindings is populated now; ApiKeys (A.3) / Users (B.1) / AuditLog (D.1) fill in
// later. Creating upfront avoids a schema migration at each phase boundary.
static class SqliteSchema
{
	// Bindings — flat (TagSet, KeyPath, Value). UNIQUE partial index on (TagSetJson,
	// KeyPath) WHERE IsDeleted=0 lets soft-deleted history stack while keeping at most
	// one active row per coordinate. `TagCount` is denormalized specificity — set on
	// upsert, cached for the resolve grouping stage.
	public const string CreateBindingsTable = """
		CREATE TABLE IF NOT EXISTS Bindings (
			Id          INTEGER PRIMARY KEY AUTOINCREMENT,
			TagSetJson  TEXT    NOT NULL,
			TagCount    INTEGER NOT NULL,
			KeyPath     TEXT    NOT NULL,
			ValuePlain  TEXT    NULL,
			Ciphertext  BLOB    NULL,
			Iv          BLOB    NULL,
			AuthTag     BLOB    NULL,
			KeyVersion  TEXT    NULL,
			Kind        TEXT    NOT NULL,
			ContentHash TEXT    NOT NULL,
			UpdatedAt   INTEGER NOT NULL,
			IsDeleted   INTEGER NOT NULL DEFAULT 0,
			AliasesJson TEXT    NULL
		);
		""";

	// `AliasesJson` column was added in C.1 — CREATE TABLE above now includes it so
	// fresh databases pick it up idempotently. Existing v2-A1 databases (if any — no
	// prod data exists) can be wiped or migrated manually.

	public const string CreateBindingsTagSetKeyLiveIndex = """
		CREATE UNIQUE INDEX IF NOT EXISTS ux_bindings_tagset_key_live
		    ON Bindings(TagSetJson, KeyPath) WHERE IsDeleted = 0;
		""";

	// Covers `WHERE KeyPath LIKE ?` for API-key prefix filtering (A.3).
	public const string CreateBindingsKeyIndex = """
		CREATE INDEX IF NOT EXISTS ix_bindings_key
		    ON Bindings(KeyPath) WHERE IsDeleted = 0;
		""";

	// ApiKeys — cookie-auth-admin creates keys via /admin/api-keys (B.3). TokenHash is
	// sha256 of a 22-char ShortGuid token; TokenPrefix displays the first 6 chars for
	// identification. RequiredTagsJson uses the same canonical shape as Bindings.
	public const string CreateApiKeysTable = """
		CREATE TABLE IF NOT EXISTS ApiKeys (
			Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
			TokenHash          TEXT    NOT NULL,
			TokenPrefix        TEXT    NOT NULL,
			RequiredTagsJson   TEXT    NOT NULL,
			AllowedKeyPrefixes TEXT    NULL,
			Description        TEXT    NOT NULL,
			UpdatedAt          INTEGER NOT NULL,
			IsDeleted          INTEGER NOT NULL DEFAULT 0
		);
		""";

	public const string CreateApiKeysTokenHashIndex = """
		CREATE UNIQUE INDEX IF NOT EXISTS ux_apikeys_token_hash_live
		    ON ApiKeys(TokenHash) WHERE IsDeleted = 0;
		""";

	// Users — cookie-auth admin accounts. B.1 wires the login flow; until then the
	// Admin-section from appsettings.json is the sole credential path (config-fallback
	// in the empty-table state).
	public const string CreateUsersTable = """
		CREATE TABLE IF NOT EXISTS Users (
			Username     TEXT    PRIMARY KEY,
			PasswordHash TEXT    NOT NULL,
			CreatedAt    INTEGER NOT NULL
		);
		""";

	// AuditLog — append-only. EntityType ∈ {Binding, ApiKey, User}. TagSetJson + KeyPath
	// locate Binding entries; ApiKey/User entries use Username/TokenPrefix via KeyPath.
	// Rollback writes a fresh Upsert that generates a new audit row with Actor="restore:<id>".
	public const string CreateAuditLogTable = """
		CREATE TABLE IF NOT EXISTS AuditLog (
			Id         INTEGER PRIMARY KEY AUTOINCREMENT,
			At         INTEGER NOT NULL,
			Actor      TEXT    NOT NULL,
			Action     TEXT    NOT NULL,
			EntityType TEXT    NOT NULL,
			TagSetJson TEXT    NULL,
			KeyPath    TEXT    NULL,
			OldValue   TEXT    NULL,
			NewValue   TEXT    NULL,
			OldHash    TEXT    NULL,
			NewHash    TEXT    NULL
		);
		""";

	public const string CreateAuditLogAtIndex = """
		CREATE INDEX IF NOT EXISTS ix_auditlog_at
		    ON AuditLog(At DESC);
		""";

	public const string CreateAuditLogTagSetAtIndex = """
		CREATE INDEX IF NOT EXISTS ix_auditlog_tagset_at
		    ON AuditLog(TagSetJson, At DESC);
		""";

	public static readonly IReadOnlyList<string> AllStatements =
	[
		CreateBindingsTable,
		CreateBindingsTagSetKeyLiveIndex,
		CreateBindingsKeyIndex,
		CreateApiKeysTable,
		CreateApiKeysTokenHashIndex,
		CreateUsersTable,
		CreateAuditLogTable,
		CreateAuditLogAtIndex,
		CreateAuditLogTagSetAtIndex,
	];
}
