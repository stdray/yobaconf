using LinqToDB.Mapping;

namespace YobaConf.Core.Storage;

// linq2db row mapping for the Bindings table. Column names match SqliteSchema DDL 1:1.
// `IsDeleted` is stored as INTEGER 0/1 rather than BOOLEAN — consistent with the rest of
// the schema and SQLite's type-affinity quirks around real booleans.
[Table("Bindings")]
sealed class BindingRow
{
    [Column, PrimaryKey, Identity] public long Id { get; set; }
    [Column, NotNull] public string TagSetJson { get; set; } = string.Empty;
    [Column] public int TagCount { get; set; }
    [Column, NotNull] public string KeyPath { get; set; } = string.Empty;
    [Column] public string? ValuePlain { get; set; }
    [Column] public byte[]? Ciphertext { get; set; }
    [Column] public byte[]? Iv { get; set; }
    [Column] public byte[]? AuthTag { get; set; }
    [Column] public string? KeyVersion { get; set; }
    [Column, NotNull] public string Kind { get; set; } = string.Empty;
    [Column, NotNull] public string ContentHash { get; set; } = string.Empty;
    [Column] public long UpdatedAt { get; set; }
    [Column] public int IsDeleted { get; set; }
    [Column] public string? AliasesJson { get; set; }
}
