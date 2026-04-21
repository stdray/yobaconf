using System.Globalization;
using System.Text.Json;

namespace YobaConf.Client;

// Flattens a JSON tree into `Microsoft.Extensions.Configuration`'s flat-key form:
//
//   {"db":{"host":"localhost","port":5432},"features":["a","b"]}
//     =>
//   { "db:host" = "localhost", "db:port" = "5432", "features:0" = "a", "features:1" = "b" }
//
// Matches the algorithm used by `JsonConfigurationProvider` in the BCL so consumers with
// pre-existing `IConfiguration` knowledge don't have to re-learn anything. Same `:`
// separator, same array-index key pattern, same ordinal key comparison.
static class JsonFlattener
{
	public static Dictionary<string, string?> Flatten(JsonDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
		Walk(document.RootElement, string.Empty, result);
		return result;
	}

	static void Walk(JsonElement element, string prefix, Dictionary<string, string?> result)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (var prop in element.EnumerateObject())
				{
					var key = prefix.Length == 0 ? prop.Name : prefix + ":" + prop.Name;
					Walk(prop.Value, key, result);
				}
				break;
			case JsonValueKind.Array:
				var i = 0;
				foreach (var item in element.EnumerateArray())
				{
					var key = prefix + ":" + i.ToString(CultureInfo.InvariantCulture);
					Walk(item, key, result);
					i++;
				}
				break;
			case JsonValueKind.String:
				result[prefix] = element.GetString();
				break;
			case JsonValueKind.Number:
				// Preserve the original numeric representation. GetRawText returns the JSON
				// literal verbatim — lets downstream `IConfiguration.GetValue<int>(...)` /
				// `.GetValue<double>(...)` do its own type parsing without double-rounding
				// through a .NET numeric type here.
				result[prefix] = element.GetRawText();
				break;
			case JsonValueKind.True:
				result[prefix] = "true";
				break;
			case JsonValueKind.False:
				result[prefix] = "false";
				break;
			case JsonValueKind.Null:
				result[prefix] = null;
				break;
			case JsonValueKind.Undefined:
			default:
				// Skipping Undefined; it shouldn't appear in a parsed JsonDocument root.
				break;
		}
	}
}
