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
public interface IConfigStoreAdmin
{
	void UpsertNode(NodePath path, string rawContent, DateTimeOffset updatedAt);
	void UpsertVariable(NodePath scope, string key, string value, DateTimeOffset updatedAt);
	void UpsertSecret(NodePath scope, string key, byte[] encryptedValue, byte[] iv, byte[] authTag, string keyVersion, DateTimeOffset updatedAt);
	void SoftDeleteNode(NodePath path);
	void SoftDeleteVariable(NodePath scope, string key);
	void SoftDeleteSecret(NodePath scope, string key);
}
