using LinqToDB.Mapping;

namespace YobaConf.Core.Storage;

[Table("TagVocabulary")]
sealed class TagVocabularyRow
{
	[Column, PrimaryKey, Identity] public long Id { get; set; }
	[Column, NotNull] public string TagKey { get; set; } = string.Empty;
	[Column] public string? TagValue { get; set; }
	[Column] public string? Description { get; set; }
	[Column] public long UpdatedAt { get; set; }
	[Column] public int IsDeleted { get; set; }
}
