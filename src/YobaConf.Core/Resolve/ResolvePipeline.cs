using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YobaConf.Core.Bindings;
using YobaConf.Core.Observability;
using YobaConf.Core.Security;
using YobaConf.Core.Tags;

namespace YobaConf.Core.Resolve;

// Subset-merge resolve (spec §4). Input: tag-vector from the HTTP request. Output: either
// canonical JSON + ETag (Success) or a 409-shaped diagnostic (Conflict). The pipeline is
// deterministic — same (bindings snapshot, tagVector) always yields the same Outcome.
//
// Stages execute in order inside their own OTel spans (children of `yobaconf.resolve`):
//   1. candidate-lookup  — store.FindMatching(tagVector)
//   2. group-by-key      — split candidates by KeyPath
//   3. conflict-check    — pick winner per group, 409 on incomparable tie with diverging values
//   4. decrypt-secrets   — materialize plaintext for Kind=Secret winners
//   5. expand-dotted     — flat (key-path, value) list → nested tree
//   6. canonical-json    — serialize with ordinal-sorted keys at every level
//   7. etag-compute      — first 16 hex chars of sha256(canonical-json)
//
// The encryptor is optional: pipelines with no Secret bindings in scope never need one,
// and startup without YOBACONF_MASTER_KEY boots fine (resolve only errors if a Secret
// binding actually wins a group). Callers surface that error as 500 — failing closed is
// safer than serving cleartext-placeholders.
public sealed class ResolvePipeline
{
	readonly IBindingStore _store;
	readonly ISecretEncryptor? _encryptor;
	readonly ITagVocabularyStore? _vocabulary;
	readonly ResolveOptions _options;

	public ResolvePipeline(
		IBindingStore store,
		ISecretEncryptor? encryptor = null,
		ITagVocabularyStore? vocabulary = null,
		ResolveOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(store);
		_store = store;
		_encryptor = encryptor;
		_vocabulary = vocabulary;
		_options = options ?? new();
	}

	public ResolveOutcome Resolve(IReadOnlyDictionary<string, string> tagVector) =>
		Resolve(tagVector, allowedKeyPrefixes: null, template: ResponseTemplate.Flat);

	public ResolveOutcome Resolve(IReadOnlyDictionary<string, string> tagVector, IReadOnlyList<string>? allowedKeyPrefixes) =>
		Resolve(tagVector, allowedKeyPrefixes, template: ResponseTemplate.Flat);

	public ResolveOutcome Resolve(
		IReadOnlyDictionary<string, string> tagVector,
		IReadOnlyList<string>? allowedKeyPrefixes,
		ResponseTemplate template)
	{
		ArgumentNullException.ThrowIfNull(tagVector);

		using var root = ActivitySources.Resolve.StartActivity("yobaconf.resolve");
		root?.SetTag("yobaconf.tag-vector.count", tagVector.Count);
		root?.SetTag("yobaconf.template", template.ToString());

		var candidates = CandidateLookup(tagVector);
		if (allowedKeyPrefixes is { Count: > 0 })
			candidates = FilterByPrefix(candidates, allowedKeyPrefixes);
		root?.SetTag("yobaconf.matched.count", candidates.Count);

		var groups = GroupByKey(candidates);

		var winners = new List<Binding>(groups.Count);
		foreach (var group in groups)
		{
			var (winner, conflict) = PickWinner(group.Key, group.Value, root);
			if (conflict is not null)
			{
				root?.SetTag("yobaconf.conflict.key", conflict.KeyPath);
				root?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "conflict");
				return conflict;
			}
			winners.Add(winner!);
		}

		var resolvedLeaves = DecryptSecrets(winners);

		var json = template == ResponseTemplate.Flat
			? CanonicalJson(ExpandDotted(resolvedLeaves))
			: CanonicalFlat(ApplyTemplate(winners, resolvedLeaves, template));
		var etag = ComputeETag(json);

		return new ResolveSuccess(json, etag);
	}

	IReadOnlyList<Binding> CandidateLookup(IReadOnlyDictionary<string, string> tagVector)
	{
		using var span = ActivitySources.Resolve.StartActivity("candidate-lookup");
		return _store.FindMatching(tagVector);
	}

	static List<Binding> FilterByPrefix(IReadOnlyList<Binding> candidates, IReadOnlyList<string> prefixes)
	{
		using var span = ActivitySources.Resolve.StartActivity("prefix-filter");
		var filtered = new List<Binding>(candidates.Count);
		foreach (var b in candidates)
			foreach (var p in prefixes)
				if (b.KeyPath.StartsWith(p, StringComparison.Ordinal))
				{
					filtered.Add(b);
					break;
				}
		return filtered;
	}

	static Dictionary<string, List<Binding>> GroupByKey(IReadOnlyList<Binding> candidates)
	{
		using var span = ActivitySources.Resolve.StartActivity("group-by-key");
		var groups = new Dictionary<string, List<Binding>>(StringComparer.Ordinal);
		foreach (var binding in candidates)
		{
			if (!groups.TryGetValue(binding.KeyPath, out var list))
			{
				list = [];
				groups[binding.KeyPath] = list;
			}
			list.Add(binding);
		}
		return groups;
	}

	// Pick semantics (spec §4):
	//   * Max specificity (TagCount) wins. Higher TagCount = more tags = more specific.
	//   * Tied-at-max with a unique value → deterministic pick (lowest Id).
	//   * Tied-at-max with diverging values → 409 Conflict.
	//   * For Secret winners any tie is conservatively treated as conflict — comparing
	//     ciphertext is meaningless (IV is per-encrypt) and decrypt-to-compare leaks nothing
	//     useful at this layer. Admin resolves by writing a more-specific overlay.
	//
	// Optional priority tie-breaker (E.5): when UsePriorityTieBreaker is true and the
	// top-tier is tied, narrow to the binding(s) with the highest max-vocabulary-priority
	// among their tags. If still tied → 409 falls through.
	(Binding? Winner, ResolveConflict? Conflict) PickWinner(
		string keyPath,
		List<Binding> group,
		System.Diagnostics.Activity? root)
	{
		using var span = ActivitySources.Resolve.StartActivity("conflict-check");
		span?.SetTag("yobaconf.key-path", keyPath);

		var maxTagCount = 0;
		foreach (var b in group)
			if (b.TagSet.Count > maxTagCount)
				maxTagCount = b.TagSet.Count;

		var topTier = new List<Binding>();
		foreach (var b in group)
			if (b.TagSet.Count == maxTagCount)
				topTier.Add(b);

		if (topTier.Count == 1)
			return (topTier[0], null);

		// E.5 priority tie-breaker: narrow topTier by max vocab priority score.
		if (topTier.Count > 1 && _options.UsePriorityTieBreaker && _vocabulary is not null)
		{
			var vocab = _vocabulary.ListActive();
			var priorityByKey = vocab
				.GroupBy(v => v.Key, StringComparer.Ordinal)
				.ToDictionary(g => g.Key, g => g.Max(v => v.Priority), StringComparer.Ordinal);

			int ScoreOf(Binding b)
			{
				var max = 0;
				foreach (var kv in b.TagSet)
					if (priorityByKey.TryGetValue(kv.Key, out var p) && p > max)
						max = p;
				return max;
			}

			var maxScore = topTier.Max(ScoreOf);
			topTier = topTier.Where(b => ScoreOf(b) == maxScore).ToList();
			root?.SetTag("yobaconf.priority-tier-breaker", true);
		}

		var anySecret = topTier.Any(b => b.Kind == BindingKind.Secret);
		if (anySecret)
			return (null, BuildConflict(keyPath, topTier));

		var firstValue = topTier[0].ValuePlain ?? string.Empty;
		var allIdentical = topTier.All(b => string.Equals(b.ValuePlain ?? string.Empty, firstValue, StringComparison.Ordinal));
		if (allIdentical)
			return (topTier.OrderBy(b => b.Id).First(), null);

		return (null, BuildConflict(keyPath, topTier));
	}

	static ResolveConflict BuildConflict(string keyPath, List<Binding> tied)
	{
		var candidates = tied
			.OrderBy(b => b.Id)
			.Select(b => new ConflictCandidate(
				b.Id,
				b.TagSet,
				b.Kind,
				b.Kind == BindingKind.Secret ? "<secret>" : b.ValuePlain ?? "null"))
			.ToArray();
		return new ResolveConflict(keyPath, candidates);
	}

	List<(string KeyPath, string ValueJson)> DecryptSecrets(List<Binding> winners)
	{
		using var span = ActivitySources.Resolve.StartActivity("decrypt-secrets");
		var leaves = new List<(string, string)>(winners.Count);
		foreach (var b in winners)
		{
			if (b.Kind == BindingKind.Plain)
			{
				leaves.Add((b.KeyPath, b.ValuePlain ?? "null"));
				continue;
			}
			if (_encryptor is null)
				throw new InvalidOperationException(
					$"Binding '{b.KeyPath}' (id={b.Id}) is a Secret but no ISecretEncryptor is registered. " +
					"Set YOBACONF_MASTER_KEY to enable secret resolution.");
			var plaintext = _encryptor.Decrypt(b.Ciphertext!, b.Iv!, b.AuthTag!, b.KeyVersion!);
			leaves.Add((b.KeyPath, JsonSerializer.Serialize(plaintext)));
		}
		return leaves;
	}

	// Expand dotted KeyPaths into a nested tree. `db.host` + `db.port` merges under a
	// single `db` branch. Scalar-under-branch collisions throw — indicates an admin
	// mistake (binding `db` scalar coexisting with `db.host`).
	static ResolveTree.Branch ExpandDotted(List<(string KeyPath, string ValueJson)> leaves)
	{
		using var span = ActivitySources.Resolve.StartActivity("expand-dotted");
		var root = new ResolveTree.Branch();
		foreach (var (keyPath, valueJson) in leaves)
		{
			var segments = keyPath.Split('.');
			var cursor = root;
			for (var i = 0; i < segments.Length - 1; i++)
			{
				var seg = segments[i];
				if (cursor.Children.TryGetValue(seg, out var child))
				{
					if (child is not ResolveTree.Branch branch)
						throw new InvalidOperationException(
							$"KeyPath '{keyPath}' collides with a scalar binding at segment '{seg}'.");
					cursor = branch;
				}
				else
				{
					var branch = new ResolveTree.Branch();
					cursor.Children[seg] = branch;
					cursor = branch;
				}
			}
			var leafSeg = segments[^1];
			if (cursor.Children.TryGetValue(leafSeg, out var existing))
			{
				throw new InvalidOperationException(
					existing is ResolveTree.Branch
						? $"KeyPath '{keyPath}' scalar collides with a nested branch."
						: $"KeyPath '{keyPath}' duplicates an existing leaf.");
			}
			cursor.Children[leafSeg] = new ResolveTree.Leaf(valueJson);
		}
		return root;
	}

	// Apply non-Flat response template to the winner list → flat (key, value) pairs where
	// key is per-template derived or, for bindings that set an alias override for this
	// template, the literal alias.
	static SortedDictionary<string, string> ApplyTemplate(
		List<Binding> winners,
		List<(string KeyPath, string ValueJson)> leaves,
		ResponseTemplate template)
	{
		using var span = ActivitySources.Resolve.StartActivity("apply-template");
		span?.SetTag("yobaconf.template", template.ToString());

		var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
		var templateKey = TemplateKey(template);
		for (var i = 0; i < winners.Count; i++)
		{
			var winner = winners[i];
			var leaf = leaves[i];
			string responseKey;
			if (winner.Aliases is not null && winner.Aliases.TryGetValue(templateKey, out var alias))
				responseKey = alias;
			else
				responseKey = ResponseTemplateParser.Derive(leaf.KeyPath, template);
			result[responseKey] = leaf.ValueJson;
		}
		return result;
	}

	static string TemplateKey(ResponseTemplate t) => t switch
	{
		ResponseTemplate.Dotnet => "dotnet",
		ResponseTemplate.Envvar => "envvar",
		ResponseTemplate.EnvvarDeep => "envvar-deep",
		_ => "flat",
	};

	static string CanonicalFlat(SortedDictionary<string, string> pairs)
	{
		using var span = ActivitySources.Resolve.StartActivity("canonical-json");
		var sb = new StringBuilder();
		sb.Append('{');
		var first = true;
		foreach (var (k, v) in pairs)
		{
			if (!first) sb.Append(',');
			first = false;
			sb.Append('"').Append(EscapeJsonString(k)).Append('"').Append(':').Append(v);
		}
		sb.Append('}');
		return sb.ToString();
	}

	static string EscapeJsonString(string s)
	{
		// Template-derived keys may contain uppercase letters + underscore that the raw
		// KeyPath slug excludes. None of them need JSON escaping under STJ's default rules
		// (no quotes, backslashes, or control chars), so a plain quote is safe. Upper-case
		// letters + underscores stay literal.
		foreach (var c in s)
			if (c is '"' or '\\' || c < 0x20)
				return JsonEncodedTextSerialize(s);
		return s;
	}

	static string JsonEncodedTextSerialize(string s) =>
		System.Text.Json.JsonSerializer.Serialize(s)[1..^1]; // strip surrounding quotes

	static string CanonicalJson(ResolveTree.Branch tree)
	{
		using var span = ActivitySources.Resolve.StartActivity("canonical-json");
		var sb = new StringBuilder();
		Write(tree, sb);
		return sb.ToString();

		static void Write(ResolveTree node, StringBuilder sb)
		{
			switch (node)
			{
				case ResolveTree.Leaf leaf:
					sb.Append(leaf.ValueJson);
					break;
				case ResolveTree.Branch branch:
					sb.Append('{');
					var first = true;
					foreach (var (key, child) in branch.Children)
					{
						if (!first) sb.Append(',');
						first = false;
						// Segments pass Slug — no escaping needed, quote verbatim.
						sb.Append('"').Append(key).Append('"').Append(':');
						Write(child, sb);
					}
					sb.Append('}');
					break;
			}
		}
	}

	static string ComputeETag(string canonicalJson)
	{
		using var span = ActivitySources.Resolve.StartActivity("etag-compute");
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
		// First 16 hex chars = 8 bytes = 64 bits of entropy — plenty for collision-avoidance
		// at our scale (ETag determinism matters, not cryptographic uniqueness).
		return Convert.ToHexStringLower(hash.AsSpan(0, 8));
	}

	abstract record ResolveTree
	{
		public sealed record Leaf(string ValueJson) : ResolveTree;

		public sealed record Branch : ResolveTree
		{
			public SortedDictionary<string, ResolveTree> Children { get; } = new(StringComparer.Ordinal);
		}
	}
}
