namespace YobaConf.Core;

public interface IConfigStore
{
	// Exact-path lookup — no scope walk, no fallthrough. Callers (NodeResolver /
	// IncludePreprocessor) decide when to traverse ancestors.
	HoconNode? FindNode(NodePath path);

	// Variables/Secrets defined *exactly* at this scope — the scope walk + nearest-wins
	// deduplication lives in VariableScopeResolver, not here. Soft-deleted rows are
	// included in the result; callers filter by `IsDeleted` if they need live data only.
	// Storage implementations may return either pre-materialised lists or lazy enumerables
	// as long as the interface type is satisfied.
	IReadOnlyList<Variable> FindVariables(NodePath scope);
	IReadOnlyList<Secret> FindSecrets(NodePath scope);
}
