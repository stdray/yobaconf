namespace YobaConf.Core;

public interface IConfigStore
{
	// Exact-path lookup — no scope walk, no fallthrough. Callers (NodeResolver /
	// IncludePreprocessor) decide when to traverse ancestors.
	HoconNode? FindNode(NodePath path);

	// All non-deleted node paths in the store, in ordinal path order. Used by the admin
	// tree-view UI (Phase A Index page). Live rows only.
	IReadOnlyList<NodePath> ListNodePaths();

	// Variables/Secrets defined *exactly* at this scope — the scope walk + nearest-wins
	// deduplication lives in VariableScopeResolver, not here. Soft-deleted rows are
	// included in the result; callers filter by `IsDeleted` if they need live data only.
	// Storage implementations may return either pre-materialised lists or lazy enumerables
	// as long as the interface type is satisfied.
	IReadOnlyList<Variable> FindVariables(NodePath scope);
	IReadOnlyList<Secret> FindSecrets(NodePath scope);
}

// Mutation contract, separate from the read-side so API endpoints and the resolve pipeline
// can take `IConfigStore` without accidentally getting write access. Admin UI / paste-import
// flows inject IConfigStoreAdmin. SqliteConfigStore implements both interfaces; DI registers
// the same instance for both.
//
// Phase B semantics:
//   * `actor` — who's making the change; lands in AuditLog.Actor. "admin" for UI writes,
//     "import" for paste-import, "restore:<id>" for rollback, "system" for bootstrap.
//   * `expectedHash` — optimistic-locking cookie. null = force-upsert (insert or overwrite
//     latest). Non-null = "I saw ContentHash=X; reject if it changed since." Outcome
//     Conflict means caller should re-fetch and show merge UI.
//   * Every Inserted/Updated/Deleted outcome appends exactly one AuditLog row with the
//     pre-change and post-change values. Conflict outcomes do not append.
public interface IConfigStoreAdmin
{
	// `actor` default = "system" for test convenience and bootstrap paths; production
	// handlers (Razor Pages, Import, rollback) always pass an explicit actor (cookie-auth
	// username or a synthetic tag like "restore:<id>" / "import").
	UpsertOutcome UpsertNode(NodePath path, string rawContent, DateTimeOffset updatedAt, string actor = "system", string? expectedHash = null);
	UpsertOutcome UpsertVariable(NodePath scope, string key, string value, DateTimeOffset updatedAt, string actor = "system", string? expectedHash = null);
	UpsertOutcome UpsertSecret(NodePath scope, string key, byte[] encryptedValue, byte[] iv, byte[] authTag, string keyVersion, DateTimeOffset updatedAt, string actor = "system", string? expectedHash = null);
	UpsertOutcome SoftDeleteNode(NodePath path, string actor = "system", string? expectedHash = null);
	UpsertOutcome SoftDeleteVariable(NodePath scope, string key, string actor = "system", string? expectedHash = null);
	UpsertOutcome SoftDeleteSecret(NodePath scope, string key, string actor = "system", string? expectedHash = null);
}
