namespace YobaConf.Core;

// One immutable row in the audit log. Produced by every successful write through
// IConfigStoreAdmin (Upsert* / SoftDelete*). Never updated — audit rows are append-only.
//
// Serialization of value payloads:
//   Node: OldValue/NewValue = RawContent (HOCON text); Hash fields = sha256 hex.
//   Variable: OldValue/NewValue = Value (plaintext); Hash fields = sha256 hex of value.
//   Secret: OldValue/NewValue = base64 of a four-field bundle
//           "{base64(Ciphertext)}|{base64(Iv)}|{base64(AuthTag)}|{KeyVersion}" — rollback
//           uses this verbatim to re-Upsert. Plaintext never appears in audit rows.
//   ApiKey: OldValue/NewValue = TokenPrefix + RootPath + Description (tuple-joined); Hash
//           = sha256 of full token. Plaintext token never stored post-creation.
//
// Key is set for Variable/Secret (the key inside the scope) and for ApiKey (the token
// prefix). Null for Node-level entries. Path is always the NodePath.
public sealed record AuditEntry(
	long Id,
	DateTimeOffset At,
	string Actor,
	AuditAction Action,
	AuditEntityType EntityType,
	NodePath Path,
	string? Key,
	string? OldValue,
	string? NewValue,
	string? OldHash,
	string? NewHash);

public enum AuditAction
{
	Created,
	Updated,
	Deleted,
	Restored,
}

public enum AuditEntityType
{
	Node,
	Variable,
	Secret,
	ApiKey,
}
