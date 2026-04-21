using System.Collections.Immutable;

namespace YobaConf.Core;

// Collects variables + secrets visible at `requestedPath` per spec §1 / §4.4:
//   - scope walk: from requestedPath up to Root, inclusive at both ends
//   - nearest-scope wins on Key collision (a Key defined at path `a/b/c` overrides the
//     same Key defined at `a/b` or at `""` root)
//   - Secret wins at equal scope (if a Variable and a Secret share a Key at the same scope,
//     the Secret is returned — explicit secret declaration beats an accidentally-same-name
//     variable; spec doesn't say this explicitly but it's the only sane default)
//   - soft-deleted rows (IsDeleted=true) are skipped as if they don't exist
//
// Result: `VariableSet` with two disjoint arrays. Caller (ResolvePipeline) decrypts secrets
// and renders both into a HOCON fragment before parsing.
public static class VariableScopeResolver
{
	public static VariableSet Resolve(NodePath requestedPath, IConfigStore store)
	{
		ArgumentNullException.ThrowIfNull(store);

		var secretsByKey = new Dictionary<string, Secret>(StringComparer.Ordinal);
		var variablesByKey = new Dictionary<string, Variable>(StringComparer.Ordinal);
		var seenKeys = new HashSet<string>(StringComparer.Ordinal);

		for (NodePath? current = requestedPath; current is not null; current = current.Value.Parent)
		{
			var scope = current.Value;

			// Process secrets before variables at each scope so that at-equal-scope
			// collisions resolve in favour of the Secret. `seenKeys.Add` acts as the
			// dedup gate: first sighting (nearest scope, Secret-preferred) wins.
			foreach (var s in store.FindSecrets(scope))
			{
				if (s.IsDeleted)
					continue;
				if (seenKeys.Add(s.Key))
					secretsByKey[s.Key] = s;
			}

			foreach (var v in store.FindVariables(scope))
			{
				if (v.IsDeleted)
					continue;
				if (seenKeys.Add(v.Key))
					variablesByKey[v.Key] = v;
			}
		}

		return new VariableSet(
			[.. variablesByKey.Values],
			[.. secretsByKey.Values]);
	}
}
