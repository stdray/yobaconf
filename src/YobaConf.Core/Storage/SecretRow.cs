using LinqToDB.Mapping;

namespace YobaConf.Core.Storage;

[Table("Secrets")]
sealed class SecretRow
{
	[Column, PrimaryKey, Identity] public long Id { get; set; }
	[Column, NotNull] public string Key { get; set; } = string.Empty;
	[Column, NotNull] public byte[] EncryptedValue { get; set; } = [];
	[Column, NotNull] public byte[] Iv { get; set; } = [];
	[Column, NotNull] public byte[] AuthTag { get; set; } = [];
	[Column, NotNull] public string KeyVersion { get; set; } = string.Empty;
	[Column, NotNull] public string ScopePath { get; set; } = string.Empty;
	[Column, NotNull] public string ContentHash { get; set; } = string.Empty;
	[Column, NotNull] public long UpdatedAt { get; set; }
	[Column, NotNull] public int IsDeleted { get; set; }
}
