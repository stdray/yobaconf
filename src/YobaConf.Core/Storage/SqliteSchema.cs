using LinqToDB.Data;

namespace YobaConf.Core.Storage;

// DDL for the tagged-v2 SQLite schema. CREATE-IF-NOT-EXISTS everywhere — every store's
// ctor calls `EnsureSchema(db)` which runs the v1-migration first (idempotent after the
// first successful bump) and then replays `AllStatements`.
//
// Migration context: the v1 path-tree deploy persisted `Nodes` / `Variables` / `Secrets`
// plus an `AuditLog` whose column layout (Path / ScopePath / EntryKey) is incompatible
// with the v2 AuditLog (TagSetJson / KeyPath). Because Docker's /app/data volume survives
// redeploys, a v2 container booting against a v1 volume blows up when
// `CreateAuditLogTagSetAtIndex` tries to index a column that doesn't exist. The migration
// below is gated on `PRAGMA user_version` so it fires exactly once per database lifetime:
// v1 DBs arrive at 0, get their v1 tables dropped + v2 recreated, and the version stamps
// to 2 so subsequent boots preserve audit history.
//
// Fresh (never-booted) DBs are also at version 0 and hit the same path — harmless:
// DROP TABLE IF EXISTS on non-existent tables is a no-op, and CREATE runs cleanly. After
// version=2, the drop branch never re-fires.
static class SqliteSchema
{
	public const int CurrentSchemaVersion = 3;

	public static void EnsureSchema(DataConnection db)
	{
		ArgumentNullException.ThrowIfNull(db);

		var current = db.Query<long>("PRAGMA user_version;").First();

		// v1 → v2: drop path-tree leftovers + incompatible AuditLog layout. Gated on < 2
		// so v2-or-newer DBs never touch this destructive branch again.
		if (current < 2)
		{
			db.Execute("DROP TABLE IF EXISTS Nodes;");
			db.Execute("DROP TABLE IF EXISTS Variables;");
			db.Execute("DROP TABLE IF EXISTS Secrets;");
			db.Execute("DROP TABLE IF EXISTS AuditLog;");
		}

		// v2 → v3: TagVocabulary table is additive (CREATE IF NOT EXISTS below). No drops.
		if (current < CurrentSchemaVersion)
			db.Execute($"PRAGMA user_version = {CurrentSchemaVersion};");

		foreach (var stmt in AllStatements)
			db.Execute(stmt);
	}


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

	// TagVocabulary — declares known tag keys + optional allowed values. A row with
	// TagValue=NULL means "this key is known; any value is allowed". Rows with
	// TagValue=<non-null> declare specific allowed values. Empty table = free-form (no
	// warnings). Bindings editor cross-references distinct keys for the unknown-key
	// warning (non-blocking — spec 15.2 defers hard validation until typos become a
	// real pain-point).
	public const string CreateTagVocabularyTable = """
		CREATE TABLE IF NOT EXISTS TagVocabulary (
			Id          INTEGER PRIMARY KEY AUTOINCREMENT,
			TagKey      TEXT    NOT NULL,
			TagValue    TEXT    NULL,
			Description TEXT    NULL,
			UpdatedAt   INTEGER NOT NULL,
			IsDeleted   INTEGER NOT NULL DEFAULT 0
		);
		""";

	// Unique on (Key, Value) among live rows. NULL-value rows are distinct per SQLite's
	// NULL-never-equal semantics — you can have (env, null) + (env, prod) simultaneously,
	// which matches the "key-declared plus specific allowed values" pattern.
	public const string CreateTagVocabularyKeyValueLiveIndex = """
		CREATE UNIQUE INDEX IF NOT EXISTS ux_tagvocab_key_value_live
		    ON TagVocabulary(TagKey, TagValue) WHERE IsDeleted = 0;
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
		CreateTagVocabularyTable,
		CreateTagVocabularyKeyValueLiveIndex,
	];
}
