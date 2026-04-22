using LinqToDB.Mapping;

namespace YobaConf.Core.Storage;

[Table("AuditLog")]
sealed class AuditLogRow
{
	[Column, PrimaryKey, Identity] public long Id { get; set; }
	[Column] public long At { get; set; }
	[Column, NotNull] public string Actor { get; set; } = string.Empty;
	[Column, NotNull] public string Action { get; set; } = string.Empty;
	[Column, NotNull] public string EntityType { get; set; } = string.Empty;
	[Column] public string? TagSetJson { get; set; }
	[Column] public string? KeyPath { get; set; }
	[Column] public string? OldValue { get; set; }
	[Column] public string? NewValue { get; set; }
	[Column] public string? OldHash { get; set; }
	[Column] public string? NewHash { get; set; }
}
