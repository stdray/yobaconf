using System.Collections.Immutable;

namespace YobaConf.Core;

// Output of VariableScopeResolver — variables and secrets visible at a given request path,
// already deduplicated via nearest-scope-wins per Key. Secrets and Variables live in separate
// arrays (caller decrypts secrets before merging into the HOCON fragment).
//
// Invariant: Variables.Select(v => v.Key) ∩ Secrets.Select(s => s.Key) = ∅. A Key appears
// in exactly one of the two arrays — whichever won at the nearest scope (Secret wins at
// equal scope per VariableScopeResolver rule).
public sealed record VariableSet(
	ImmutableArray<Variable> Variables,
	ImmutableArray<Secret> Secrets)
{
	public static VariableSet Empty { get; } = new(ImmutableArray<Variable>.Empty, ImmutableArray<Secret>.Empty);
}
