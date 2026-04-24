using YobaConf.Core.Bindings;

namespace YobaConf.Core.Resolve;

// Discriminated outcome of a resolve. Exactly one of Success / Conflict is produced; the
// HTTP layer maps Success → 200 (body=Json, header=ETag) and Conflict → 409 with the
// diagnostic payload. We model it as an abstract record so callers pattern-match on the
// concrete type — no sentinel-string or Nullable fields.
public abstract record ResolveOutcome;

public sealed record ResolveSuccess(string Json, string ETag) : ResolveOutcome;

public sealed record ResolveConflict(string KeyPath, IReadOnlyList<ConflictCandidate> Candidates)
    : ResolveOutcome;

// One of the tied-at-max-specificity bindings fighting over the same key. `ValueDisplay`
// is safe to surface in 409 diagnostics — Plain bindings show the JSON-encoded scalar
// verbatim, Secret bindings show the literal string "<secret>" (never decrypted value).
public sealed record ConflictCandidate(long BindingId, TagSet TagSet, BindingKind Kind, string ValueDisplay);
