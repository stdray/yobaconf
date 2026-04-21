using LinqToDB.Mapping;

namespace YobaConf.Core.Storage;

// linq2db DTO for the Nodes table. Internal — callers consume the domain `HoconNode`
// record, not this row shape.
[Table("Nodes")]
sealed class NodeRow
{
	[Column, PrimaryKey, Identity] public long Id { get; set; }
	[Column, NotNull] public string Path { get; set; } = string.Empty;
	[Column, NotNull] public string RawContent { get; set; } = string.Empty;
	[Column, NotNull] public string ContentHash { get; set; } = string.Empty;
	[Column, NotNull] public long UpdatedAt { get; set; }
	[Column, NotNull] public int IsDeleted { get; set; }
}
