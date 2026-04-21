using System.Text.Json;

namespace YobaConf.Core.Converters;

// JSON → HOCON conversion: near no-op. JSON is a syntactic subset of HOCON (HOCON-2.0.4
// parses any valid JSON as-is). The converter's job is therefore:
//   1. Validate the input parses as JSON — fail fast with ImportException if not.
//   2. Pretty-print with 2-space indent so the result is readable in the HOCON editor.
//
// User gets valid HOCON text they can save as a new node's RawContent. Refactoring into
// idiomatic HOCON (unquoted keys, substitutions, comments) is manual follow-up — we don't
// guess at the user's intent for those.
public static class JsonToHoconConverter
{
	static readonly JsonSerializerOptions PrettyOptions = new()
	{
		WriteIndented = true,
		IndentSize = 2,
	};

	public static string Convert(string jsonText)
	{
		ArgumentNullException.ThrowIfNull(jsonText);
		try
		{
			using var doc = JsonDocument.Parse(jsonText);
			return JsonSerializer.Serialize(doc.RootElement, PrettyOptions);
		}
		catch (JsonException ex)
		{
			throw new ImportException($"Invalid JSON: {ex.Message}", ex);
		}
	}
}
