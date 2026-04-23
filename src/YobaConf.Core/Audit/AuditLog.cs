namespace YobaConf.Core.Audit;

// Append-only history row (spec §3). Populated by every IBindingStoreAdmin / IApiKeyAdmin
// / IUserAdmin write — storage is the single writer, pages pass the actor through. Reads
// power the /History page + rollback UI.
//
// Payload columns are nullable because the three EntityTypes reuse them differently:
//   Binding: TagSetJson + KeyPath identify the row; Old/NewValue are JSON-encoded scalars
//            for Plain, or a ciphertext bundle string for Secret. Hashes are sha256 hex.
//   ApiKey:  KeyPath carries the TokenPrefix for display; Old/NewValue is a serialized
//            summary (description + required-tags JSON + allowed-prefixes JSON).
//   User:    KeyPath carries the Username; Old/NewValue is "password-set" / "password-changed"
//            marker (never plaintext).
public sealed record AuditLogEntry(
	long Id,
	DateTimeOffset At,
	string Actor,
	AuditAction Action,
	AuditEntityType EntityType,
	string? TagSetJson,
	string? KeyPath,
	string? OldValue,
	string? NewValue,
	string? OldHash,
	string? NewHash);

public enum AuditAction
{
	Created = 0,
	Updated,
	Deleted,
	Restored,
}

public enum AuditEntityType
{
	Binding = 0,
	ApiKey,
	User,
	TagVocabulary,
}

public interface IAuditLogStore
{
	// Reverse chrono — newest first for the history page default view.
	IReadOnlyList<AuditLogEntry> ListRecent(int limit);

	// Filter by entity type / actor / key-path substring. All optional — null means any.
	IReadOnlyList<AuditLogEntry> Query(AuditEntityType? entityType, string? actor, string? keyPathSubstring, int limit);

	AuditLogEntry? FindById(long id);
}
