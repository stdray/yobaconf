using System.Collections;
using System.Text;
using System.Text.Json;

namespace YobaConf.Core.Bindings;

// Immutable ordered set of tag-key → tag-value pairs, with a canonical JSON serialization
// that is byte-identical for equal contents. This is load-bearing for the Bindings storage:
//   * UNIQUE index on (TagSetJson, KeyPath) relies on byte-identity — any non-determinism
//     in the serialization breaks deduplication.
//   * ETag determinism downstream chains off the same property.
//
// Canonical form: JSON object, keys sorted by Ordinal string comparison, no whitespace,
// no trailing commas. Values are always strings (tag-values are slugs — no numbers, bools,
// nulls). The empty tag-set serializes to "{}".
//
// Construction is validating — every key and value must pass `Slug` to catch typos at
// the boundary rather than letting garbage reach SQLite.
public sealed class TagSet : IEnumerable<KeyValuePair<string, string>>, IEquatable<TagSet>
{
	public static readonly TagSet Empty = new(new SortedDictionary<string, string>(StringComparer.Ordinal), "{}");

	readonly SortedDictionary<string, string> _pairs;
	readonly string _canonicalJson;

	TagSet(SortedDictionary<string, string> pairs, string canonicalJson)
	{
		_pairs = pairs;
		_canonicalJson = canonicalJson;
	}

	public static TagSet From(IEnumerable<KeyValuePair<string, string>> pairs)
	{
		ArgumentNullException.ThrowIfNull(pairs);

		var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var (key, value) in pairs)
		{
			Slug.Require(key, "tag-key");
			Slug.Require(value, "tag-value");
			if (sorted.ContainsKey(key))
				throw new ArgumentException($"Duplicate tag-key '{key}' — each key may appear once.", nameof(pairs));
			sorted[key] = value;
		}

		return sorted.Count == 0 ? Empty : new TagSet(sorted, SerializeCanonical(sorted));
	}

	public static TagSet FromCanonicalJson(string canonicalJson)
	{
		ArgumentNullException.ThrowIfNull(canonicalJson);
		using var doc = JsonDocument.Parse(canonicalJson);
		if (doc.RootElement.ValueKind != JsonValueKind.Object)
			throw new ArgumentException("TagSet JSON must be an object.", nameof(canonicalJson));

		var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var p in doc.RootElement.EnumerateObject())
		{
			if (p.Value.ValueKind != JsonValueKind.String)
				throw new ArgumentException(
					$"Tag-value for '{p.Name}' must be a JSON string.", nameof(canonicalJson));
			Slug.Require(p.Name, "tag-key");
			var value = p.Value.GetString()!;
			Slug.Require(value, "tag-value");
			sorted[p.Name] = value;
		}
		return sorted.Count == 0 ? Empty : new TagSet(sorted, SerializeCanonical(sorted));
	}

	public string CanonicalJson => _canonicalJson;

	public int Count => _pairs.Count;

	public bool ContainsKey(string key) => _pairs.ContainsKey(key);

	public bool TryGetValue(string key, out string value) => _pairs.TryGetValue(key, out value!);

	public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _pairs.GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => _pairs.GetEnumerator();

	// Subset check: self.TagSet ⊆ superset iff every (k,v) in self appears identically
	// in superset. Empty set is subset of everything. Used by resolve's candidate-lookup
	// and by API-key RequiredTags validation.
	public bool IsSubsetOf(IReadOnlyDictionary<string, string> superset)
	{
		ArgumentNullException.ThrowIfNull(superset);
		foreach (var (k, v) in _pairs)
			if (!superset.TryGetValue(k, out var theirs) || !string.Equals(v, theirs, StringComparison.Ordinal))
				return false;
		return true;
	}

	public bool Equals(TagSet? other) =>
		other is not null && string.Equals(_canonicalJson, other._canonicalJson, StringComparison.Ordinal);

	public override bool Equals(object? obj) => obj is TagSet ts && Equals(ts);

	public override int GetHashCode() => _canonicalJson.GetHashCode(StringComparison.Ordinal);

	public override string ToString() => _canonicalJson;

	static string SerializeCanonical(SortedDictionary<string, string> pairs)
	{
		// Hand-rolled rather than Utf8JsonWriter so the output format is pinned — future
		// STJ behaviour changes (escape-encoder upgrades, etc.) cannot shift canonical
		// bytes out from under the UNIQUE index. Slug regex restricts characters to
		// [a-z0-9-$] which need no JSON escaping.
		if (pairs.Count == 0) return "{}";
		var sb = new StringBuilder();
		sb.Append('{');
		var first = true;
		foreach (var (k, v) in pairs)
		{
			if (!first) sb.Append(',');
			first = false;
			sb.Append('"').Append(k).Append('"').Append(':').Append('"').Append(v).Append('"');
		}
		sb.Append('}');
		return sb.ToString();
	}
}
