using LinqToDB.Mapping;

namespace YobaConf.Core.Storage;

[Table("Users")]
sealed class UserRow
{
	[Column, PrimaryKey, NotNull] public string Username { get; set; } = string.Empty;
	[Column, NotNull] public string PasswordHash { get; set; } = string.Empty;
	[Column] public long CreatedAt { get; set; }
}
