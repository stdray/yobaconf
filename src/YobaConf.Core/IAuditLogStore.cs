namespace YobaConf.Core;

// Read-side of the audit log. Write-side is integrated into IConfigStoreAdmin implementations
// (every successful Upsert* / SoftDelete* appends) — callers don't append directly.
public interface IAuditLogStore
{
	// All audit entries for the given path's scope. `includeDescendants = true` returns
	// every row whose Path is equal to or a descendant of `path`. Variables and Secrets
	// entries are keyed by their ScopePath, so they're included when the owning node's
	// path falls into the scope. ApiKeys: their Path field is interpreted as the key's
	// RootPath.
	//
	// Newest-first. Use skip/take for paging; recommended page size 50-100.
	IReadOnlyList<AuditEntry> FindByPath(NodePath path, bool includeDescendants, int skip, int take);

	// Fetch one entry by Id — used by rollback handlers to reconstruct the pre-change
	// state before issuing the restore Upsert.
	AuditEntry? FindById(long id);
}
