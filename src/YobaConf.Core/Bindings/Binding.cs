namespace YobaConf.Core.Bindings;

public enum BindingKind
{
	Plain = 0,
	Secret = 1,
}

// Domain-facing binding record. Mirrors the Bindings table with the storage-level
// denormalized `TagCount` hidden behind `TagSet.Count`. New-row construction leaves
// `Id = 0`; the store assigns the autoincrement value and returns a refreshed instance
// via UpsertOutcome.
//
// For Kind=Plain: `ValuePlain` holds a JSON-encoded scalar ("\"prod\"", "42", "true",
// "null"). Non-string literals are allowed so numeric/bool types survive the resolve
// pipeline's expand-dotted + canonical-JSON stages without re-parsing.
//
// For Kind=Secret: ValuePlain is null; Ciphertext/Iv/AuthTag/KeyVersion hold the
// AesGcmSecretEncryptor output bundle.
//
// `ContentHash` is sha256-hex of the serialized value — plaintext-JSON for Plain,
// ciphertext-bytes for Secret. Used for optimistic-locking + audit-diff in later phases.
public sealed record Binding
{
	public required long Id { get; init; }
	public required TagSet TagSet { get; init; }
	public required string KeyPath { get; init; }
	public required BindingKind Kind { get; init; }

	public string? ValuePlain { get; init; }

	public byte[]? Ciphertext { get; init; }
	public byte[]? Iv { get; init; }
	public byte[]? AuthTag { get; init; }
	public string? KeyVersion { get; init; }

	public required string ContentHash { get; init; }
	public required DateTimeOffset UpdatedAt { get; init; }
	public bool IsDeleted { get; init; }
}
