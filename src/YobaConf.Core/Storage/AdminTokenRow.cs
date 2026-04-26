using LinqToDB.Mapping;

namespace YobaConf.Core.Storage;

[Table("AdminTokens")]
sealed class AdminTokenRow
{
    [Column, PrimaryKey, Identity] public long Id { get; set; }
    [Column, NotNull] public string Username { get; set; } = string.Empty;
    [Column, NotNull] public string TokenHash { get; set; } = string.Empty;
    [Column, NotNull] public string TokenPrefix { get; set; } = string.Empty;
    [Column, NotNull] public string Description { get; set; } = string.Empty;
    [Column] public long UpdatedAt { get; set; }
    [Column] public int IsDeleted { get; set; }
}
