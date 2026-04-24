namespace YobaConf.Core.Bindings;

// Read-only contract. The resolve pipeline + admin UI list views depend on this; the
// admin-write contract lives on a separate interface (`IBindingStoreAdmin`) so consumer
// code that only needs to read cannot accidentally mutate.
public interface IBindingStore
{
    Binding? FindById(long id);

    // All non-deleted bindings, ordered by (TagSetJson, KeyPath) for stable admin-UI
    // rendering. Pet-scale (≤200 rows) — no pagination in MVP.
    IReadOnlyList<Binding> ListActive();

    // Subset-matching query — every binding whose TagSet ⊆ tagVector. Drives the
    // candidate-lookup stage of the resolve pipeline (A.2). Implementation is SQLite
    // JSON1-based (`json_each(TagSetJson)` unwrapped, NOT EXISTS mismatch predicate)
    // but the interface stays shape-agnostic.
    IReadOnlyList<Binding> FindMatching(IReadOnlyDictionary<string, string> tagVector);
}

public interface IBindingStoreAdmin
{
    // Upsert by (TagSet, KeyPath). If an active row exists at that coordinate it's
    // updated in place (Id preserved); otherwise a new row is inserted. Soft-deleted
    // rows at the same coordinate are left alone — they stay in AuditLog-reachable
    // form — and a new Id is assigned. Returns the post-write Binding plus the prior
    // hash (null for insert) so the audit layer can emit a diff entry.
    //
    // `actor` is the identity threaded through to AuditLog — pages pass the cookie-auth
    // username; background wiring passes "system"; rollback passes `restore:<audit-id>`.
    UpsertOutcome Upsert(Binding binding, string actor = "system");

    // Soft-delete by Id. Flips IsDeleted=1, bumps UpdatedAt. Returns true if the row
    // existed and was active; false if missing or already deleted.
    bool SoftDelete(long id, DateTimeOffset at, string actor = "system");
}

public readonly record struct UpsertOutcome(Binding Binding, string? OldHash);
