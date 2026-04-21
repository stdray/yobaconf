using System.Security.Cryptography;
using System.Text;
using Hocon;
using YobaConf.Core.Include;
using YobaConf.Core.Observability;
using YobaConf.Core.Serialization;

namespace YobaConf.Core;

// The full spec §4 pipeline as a pure function:
//
//     requestedPath + IConfigStore → ResolveResult { Json, ETag }
//
// Stages:
//   1. Fallthrough: find nearest existing non-deleted ancestor (NodeResolver). Request path
//      itself is tried first; walk up on miss; null → NodeNotFoundException.
//   2. Scope walk for Variables (secrets deferred to Phase C — spec §14 "Фаза A — dog-food
//      ready: без секретов, без истории"). VariableScopeResolver does nearest-wins dedup.
//   3. Render Variables as a HOCON-quoted-fragment prefix ("defaults" layer).
//   4. Expand `include`-directives in best-match RawContent (IncludePreprocessor) — DFS with
//      cycle detection + scope validation, produces flat include-free text.
//   5. Concatenate: variables-fragment + flat content. Variables come first so that HOCON's
//      later-wins rule lets the content's explicit values override any accidentally-named
//      Variables. Substitutions in content reference the variables-fragment entries.
//   6. Single HoconConfigurationFactory.ParseString on the combined text. Substitutions
//      resolve here (not later — Hocon 2.0.4 at-parse-time semantics, decision-log 2026-04-21).
//   7. HoconJsonSerializer.SerializeToJson — canonical, ordinal-sorted keys → determinism.
//   8. ETag: first 16 hex chars of sha256(UTF-8 JSON bytes), lowercase. Strong ETag
//      (no W/ prefix) — byte-equality of canonical JSON guarantees content equality.
//
// Secrets (Phase C): a future overload will take ISecretDecryptor and merge decrypted secrets
// into the variables-fragment at step 3. Variables-only pipeline is what Phase A ships.
//
// Phase C.5 tracing: each stage is wrapped in an ActivitySource.StartActivity span — when
// no listener is attached (unit tests, OpenTelemetry:Enabled=false) StartActivity returns
// null and `using (null)` is a no-op, so zero overhead. With the OTLP exporter wired these
// spans land in yobalog's waterfall UI with tag `yobaconf.path` on the root and per-stage
// duration on children. See doc/decision-log.md 2026-04-21 Phase C.5.
public static class ResolvePipeline
{
	public static ResolveResult Resolve(NodePath requestedPath, IConfigStore store)
	{
		ArgumentNullException.ThrowIfNull(store);

		using var rootActivity = ActivitySources.Resolve.StartActivity("yobaconf.resolve");
		rootActivity?.SetTag("yobaconf.path", requestedPath.ToDbPath());

		HoconNode? bestMatch;
		using (var a = ActivitySources.Resolve.StartActivity("yobaconf.fallthrough-lookup"))
		{
			bestMatch = NodeResolver.FindBestMatch(store, requestedPath);
			a?.SetTag("yobaconf.resolved", bestMatch?.Path.ToDbPath());
		}
		if (bestMatch is null)
			throw new NodeNotFoundException(requestedPath);

		VariableSet variableSet;
		using (var a = ActivitySources.Resolve.StartActivity("yobaconf.variables-resolve"))
		{
			variableSet = VariableScopeResolver.Resolve(requestedPath, store);
			a?.SetTag("yobaconf.variables.count", variableSet.Variables.Length);
		}

		var variablesHocon = HoconVariableRenderer.Render(variableSet.Variables);

		string flatHocon;
		using (ActivitySources.Resolve.StartActivity("yobaconf.include-preprocess"))
		{
			flatHocon = IncludePreprocessor.Resolve(bestMatch.Path, store);
		}

		var combined = variablesHocon + flatHocon;

		// If the combined text is whitespace-only (empty node, no variables) Hocon throws —
		// default to "{}" so an empty config round-trips as an empty JSON object.
		var parseInput = string.IsNullOrWhiteSpace(combined) ? "{}" : combined;

		Config parsed;
		using (ActivitySources.Resolve.StartActivity("yobaconf.hocon-parse"))
		{
			parsed = HoconConfigurationFactory.ParseString(parseInput);
		}

		string json;
		using (ActivitySources.Resolve.StartActivity("yobaconf.json-serialize"))
		{
			json = HoconJsonSerializer.SerializeToJson(parsed);
		}

		string etag;
		using (ActivitySources.Resolve.StartActivity("yobaconf.etag-compute"))
		{
			etag = ComputeETag(json);
		}

		return new ResolveResult(json, etag);
	}

	static string ComputeETag(string json)
	{
		var bytes = Encoding.UTF8.GetBytes(json);
		var hash = SHA256.HashData(bytes);
		// First 16 hex chars = 64 bits of entropy. Plenty for cache-key disambiguation —
		// collision probability 2^-32 after 2^32 distinct configs, which we'll never approach.
#pragma warning disable CA1308 // ETag hex is cosmetic, case is irrelevant
		return Convert.ToHexString(hash).AsSpan(0, 16).ToString().ToLowerInvariant();
#pragma warning restore CA1308
	}
}
