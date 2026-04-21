using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hocon;

namespace YobaConf.Core.Serialization;

// Converts a parsed HOCON tree (post-substitution) to canonical JSON. Canonical means
// object keys are sorted ordinally (`StringComparer.Ordinal`) — same input bytes produce
// the same output bytes regardless of HoconObject iteration order. Determinism is load-
// bearing for the ETag formula in spec §4.6: same JSON → same sha256 → same ETag →
// clients get 304 on unchanged configs.
//
// Scope: pure function (HoconRoot -> JsonNode/string). No store access, no pipeline logic.
// Called at the end of ResolvePipeline after include-preprocessing + ParseString have
// produced a fully resolved HoconRoot (substitutions already inlined at parse time — see
// decision-log 2026-04-21 "Hocon 2.0.4 резолвит substitutions at parse-time").
public static class HoconJsonSerializer
{
	// Pretty-printed (indented) canonical JSON. For HTTP responses consumed by clients,
	// callers typically want compact (pretty=false); for the admin-UI preview panel, pretty=true.
	public static string SerializeToJson(HoconRoot config, bool pretty = false)
	{
		ArgumentNullException.ThrowIfNull(config);
		var node = ConvertValue(config.Value);
		return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = pretty }) ?? "null";
	}

	// Lower-level: returns a mutable `JsonNode` tree. Useful when the caller wants to tweak
	// (add trace metadata, wrap in envelope) before serializing. Returns null when the
	// whole config is empty.
	public static JsonNode? SerializeToNode(HoconRoot config)
	{
		ArgumentNullException.ThrowIfNull(config);
		return ConvertValue(config.Value);
	}

	static JsonNode? ConvertValue(HoconValue value) => value.Type switch
	{
		HoconType.Empty => null,
		HoconType.Object => ConvertObject(value.GetObject()),
		HoconType.Array => ConvertArray(value.GetArray()),
		HoconType.Number => ConvertNumber(value),
		HoconType.Boolean => JsonValue.Create(value.GetBoolean()),
		HoconType.String => ConvertString(value),
		_ => throw new NotSupportedException($"Unsupported HoconType: {value.Type}"),
	};

	static JsonObject ConvertObject(HoconObject obj)
	{
		var result = new JsonObject();
		// Sort keys ordinally — this is what makes the serializer canonical. Without this,
		// HoconObject (a Dictionary<string, HoconField>) iterates in insertion order and
		// two equivalent parses can produce byte-different JSON → unstable ETag.
		foreach (var entry in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
		{
			result[entry.Key] = ConvertValue(entry.Value.Value);
		}
		return result;
	}

	static JsonArray ConvertArray(IList<HoconValue> array)
	{
		// Arrays preserve HOCON order — it's meaningful (unlike object keys where order
		// carries no semantics post-merge).
		var result = new JsonArray();
		foreach (var item in array)
			result.Add(ConvertValue(item));
		return result;
	}

	static JsonValue ConvertNumber(HoconValue value)
	{
		// Prefer long to preserve integer precision (JSON number is IEEE 754 double by
		// default, but System.Text.Json emits integer literals without `.0` when the
		// backing CLR type is integral). HOCON supports hex and octal too; GetString
		// returns the canonical decimal representation.
		var raw = value.GetString();
		if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
			return JsonValue.Create(l);
		if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
			return JsonValue.Create(d);
		// Shouldn't be reachable if HoconType == Number. Fall back to string — better a
		// visible value than a NotSupportedException on exotic numeric forms.
		return JsonValue.Create(raw)!;
	}

	static JsonValue? ConvertString(HoconValue value)
	{
		// Null literal: Raw="null" unquoted. Quoted `"null"` has Raw=`"null"` (with quotes),
		// so the equality below distinguishes them.
		var raw = value.Raw;
		if (raw == "null")
			return null;

		// Hocon 2.0.4 quirk: unquoted double literals like `ratio = 0.75` report
		// Type=String instead of Type=Number (unquoted ints correctly report Number).
		// Re-type unquoted numeric forms here. Raw still carries quotes for quoted strings,
		// so `port = "8080"` (Raw=`"8080"`) doesn't get re-typed — user's explicit string stays.
		if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
			return JsonValue.Create(l);
		if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
			return JsonValue.Create(d);

		return JsonValue.Create(value.GetString());
	}
}
