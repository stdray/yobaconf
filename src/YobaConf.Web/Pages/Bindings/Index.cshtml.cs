using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaConf.Core.Bindings;
using YobaConf.Core.Security;

namespace YobaConf.Web.Pages.Bindings;

public sealed class IndexModel : PageModel
{
	readonly IBindingStore _store;
	readonly ISecretEncryptor? _encryptor;

	public IndexModel(IBindingStore store, ISecretEncryptor? encryptor = null)
	{
		_store = store;
		_encryptor = encryptor;
	}

	public string? KeyQuery { get; private set; }
	public IReadOnlyDictionary<string, string> TagFilter { get; private set; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	public IReadOnlyList<string> FacetKeys { get; private set; } = [];
	public IReadOnlyDictionary<string, IReadOnlyList<string>> FacetValues { get; private set; } =
		new Dictionary<string, IReadOnlyList<string>>();

	public IReadOnlyList<Row> Rows { get; private set; } = [];
	public long? RevealedId { get; private set; }
	public string? ErrorMessage { get; set; }
	public string? SuccessMessage { get; set; }

	public sealed record Row(Binding Binding, string? RevealedValue);

	public void OnGet(string? q, long? revealId)
	{
		KeyQuery = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
		RevealedId = revealId;

		// Pull every active binding so we can build facet dropdowns from actually-used tag
		// keys+values without a separate DISTINCT query. Pet-scale (≤200 rows) makes the full
		// fetch fine.
		var all = _store.ListActive();

		var facetKeys = new SortedSet<string>(StringComparer.Ordinal);
		var facetValues = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
		foreach (var b in all)
			foreach (var (k, v) in b.TagSet)
			{
				facetKeys.Add(k);
				if (!facetValues.TryGetValue(k, out var set))
				{
					set = new SortedSet<string>(StringComparer.Ordinal);
					facetValues[k] = set;
				}
				set.Add(v);
			}
		FacetKeys = [.. facetKeys];
		FacetValues = facetKeys.ToDictionary(k => k, k => (IReadOnlyList<string>)[.. facetValues[k]]);

		// Collect tag filters from query-string `t.<key>=<value>`.
		var filter = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var kv in Request.Query)
		{
			if (!kv.Key.StartsWith("t.", StringComparison.Ordinal)) continue;
			var tagKey = kv.Key[2..];
			var tagValue = kv.Value.LastOrDefault();
			if (string.IsNullOrEmpty(tagValue)) continue;
			filter[tagKey] = tagValue;
		}
		TagFilter = filter;

		var matching = all.Where(b => MatchesTagFilter(b, filter) && MatchesKeyQuery(b, KeyQuery));

		var rows = new List<Row>();
		foreach (var b in matching)
		{
			string? revealed = null;
			if (b.Kind == BindingKind.Secret && revealId == b.Id && _encryptor is not null)
			{
				try
				{
					revealed = _encryptor.Decrypt(b.Ciphertext!, b.Iv!, b.AuthTag!, b.KeyVersion!);
				}
				catch
				{
					revealed = "<decrypt-failed>";
				}
			}
			rows.Add(new Row(b, revealed));
		}
		Rows = rows;
	}

	static bool MatchesTagFilter(Binding b, IReadOnlyDictionary<string, string> filter)
	{
		foreach (var (k, v) in filter)
			if (!b.TagSet.TryGetValue(k, out var bv) || !string.Equals(bv, v, StringComparison.Ordinal))
				return false;
		return true;
	}

	static bool MatchesKeyQuery(Binding b, string? q)
	{
		if (string.IsNullOrEmpty(q)) return true;
		// Glob `*` at the end = prefix match; otherwise substring.
		if (q.EndsWith('*'))
			return b.KeyPath.StartsWith(q[..^1], StringComparison.Ordinal);
		return b.KeyPath.Contains(q, StringComparison.Ordinal);
	}
}
