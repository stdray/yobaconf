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

	// Per-template alias override — spec §9.1. Dictionary keyed by template name
	// ("dotnet", "envvar", "envvar-deep"); value is the literal response-key to emit
	// under that template. Null / missing key → derive from KeyPath.
	//
	// Primary use case: platform-mandated names that don't slug-transform cleanly, e.g.
	// `AWS_ACCESS_KEY_ID` (envvar wants literal UPPERCASE underscore), `PATH`, `HOME`.
	// Admin writes `AliasesJson = {"envvar": "AWS_ACCESS_KEY_ID"}` on the binding.
	public IReadOnlyDictionary<string, string>? Aliases { get; init; }
}
