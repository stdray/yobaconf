namespace YobaConf.Core.Tags;

// Declares which tag-keys + values are "known" in this deployment. Used only for
// non-blocking editor warnings — free-form keys still resolve correctly, spec §15.2.
//
// Semantics:
//   Value=null            → "key is declared; any value is allowed". Listing (env, null)
//                            silences the unknown-key warning for env=<anything>.
//   Value="<specific>"    → "this exact pair is allowed". Multiple rows per key stack.
//
// Empty vocabulary = no warnings (opt-in). Once the first row lands, any tag-key missing
// from the distinct set surfaces a warning in the Bindings editor.
public sealed record TagVocabularyEntry(
    long Id,
    string Key,
    string? Value,
    string? Description,
    int Priority,
    DateTimeOffset UpdatedAt);

public interface ITagVocabularyStore
{
    IReadOnlyList<TagVocabularyEntry> ListActive();

    // Distinct Keys across live rows — used by the editor to test "is this key known".
    IReadOnlyList<string> DistinctKeys();
}

public interface ITagVocabularyAdmin
{
    // Insert a new entry. Throws InvalidOperationException if (Key, Value) already exists
    // among live rows — deliberate, stops silent dedupe + preserves the UNIQUE invariant.
    TagVocabularyEntry Create(string key, string? value, string? description, int priority, DateTimeOffset at, string actor = "system");

    // Soft-delete by row id. Returns false if the row is unknown or already deleted.
    bool SoftDelete(long id, DateTimeOffset at, string actor = "system");
}
