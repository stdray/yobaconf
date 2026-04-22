using LinqToDB.Mapping;

namespace YobaConf.Core.Storage;

// linq2db DTO for the AuditLog table. Append-only: callers never update rows. Value payloads
// are serialized via the rules in AuditEntry.cs; EntryKey is null for Node-level entries.
[Table("AuditLog")]
sealed class AuditLogRow
{
	[Column, PrimaryKey, Identity] public long Id { get; set; }
	[Column, NotNull] public long At { get; set; }
	[Column, NotNull] public string Actor { get; set; } = string.Empty;
	[Column, NotNull] public string Action { get; set; } = string.Empty;
	[Column, NotNull] public string EntityType { get; set; } = string.Empty;
	[Column, NotNull] public string Path { get; set; } = string.Empty;
	[Column] public string? EntryKey { get; set; }
	[Column] public string? OldValue { get; set; }
	[Column] public string? NewValue { get; set; }
	[Column] public string? OldHash { get; set; }
	[Column] public string? NewHash { get; set; }
}
