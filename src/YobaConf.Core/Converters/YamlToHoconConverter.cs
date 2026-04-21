using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace YobaConf.Core.Converters;

// YAML → HOCON conversion via YamlDotNet's YamlStream. Walks the representation-model
// tree and emits equivalent HOCON text.
//
// Type-inference for plain (unquoted) scalars: tries bool / null / long / double in order,
// falling back to a quoted string. Explicitly-quoted YAML scalars (`key: "1"`) stay strings
// regardless of numeric-looking content — the source told us it's a string.
//
// Anchors/aliases (`&name`, `*name`) are expanded by YamlDotNet at parse time (we don't
// see them separately in the tree), so the output is a plain value even if the source
// referenced an anchor. Acknowledged loss of information: the user intent "share this
// block" doesn't carry into HOCON; they can rewrite using HOCON substitutions after import.
//
// Multi-document YAML streams: first document only. Multi-doc is rare for config;
// merge semantics would be ambiguous (merge vs sequence-of-docs).
public static class YamlToHoconConverter
{
	public static string Convert(string yamlText)
	{
		ArgumentNullException.ThrowIfNull(yamlText);

		YamlStream stream;
		try
		{
			stream = new YamlStream();
			stream.Load(new StringReader(yamlText));
		}
		catch (YamlException ex)
		{
			throw new ImportException($"Invalid YAML: {ex.Message}", ex);
		}

		if (stream.Documents.Count == 0)
			return "{}";

		var root = stream.Documents[0].RootNode;
		var sb = new StringBuilder();
		Render(root, sb, indent: 0);
		return sb.ToString();
	}

	static void Render(YamlNode node, StringBuilder sb, int indent)
	{
		switch (node)
		{
			case YamlMappingNode map:
				RenderMapping(map, sb, indent);
				break;
			case YamlSequenceNode seq:
				RenderSequence(seq, sb, indent);
				break;
			case YamlScalarNode scalar:
				sb.Append(FormatScalar(scalar));
				break;
			default:
				throw new ImportException($"Unsupported YAML node type: {node.GetType().Name}");
		}
	}

	static void RenderMapping(YamlMappingNode map, StringBuilder sb, int indent)
	{
		if (map.Children.Count == 0)
		{
			sb.Append("{}");
			return;
		}

		sb.Append('{').Append('\n');
		var inner = indent + 1;
		foreach (var pair in map.Children)
		{
			if (pair.Key is not YamlScalarNode keyNode)
				throw new ImportException($"Unsupported YAML mapping key type: {pair.Key.GetType().Name} (only scalar keys supported)");

			AppendIndent(sb, inner);
			// Always quote the key — sidesteps HOCON's unquoted-string ambiguity
			// (dots in keys would otherwise create nested paths).
			sb.Append('"');
			AppendHoconEscaped(sb, keyNode.Value ?? string.Empty);
			sb.Append("\" = ");
			Render(pair.Value, sb, inner);
			sb.Append('\n');
		}
		AppendIndent(sb, indent);
		sb.Append('}');
	}

	static void RenderSequence(YamlSequenceNode seq, StringBuilder sb, int indent)
	{
		if (seq.Children.Count == 0)
		{
			sb.Append("[]");
			return;
		}

		sb.Append('[').Append('\n');
		var inner = indent + 1;
		foreach (var item in seq.Children)
		{
			AppendIndent(sb, inner);
			Render(item, sb, inner);
			sb.Append('\n');
		}
		AppendIndent(sb, indent);
		sb.Append(']');
	}

	static string FormatScalar(YamlScalarNode scalar)
	{
		var value = scalar.Value ?? string.Empty;

		// Explicitly quoted in source → preserve as string regardless of content.
		if (scalar.Style == ScalarStyle.DoubleQuoted || scalar.Style == ScalarStyle.SingleQuoted)
			return QuoteHocon(value);

		// Plain scalar: try to infer type.
		if (string.Equals(value, "true", StringComparison.Ordinal) || string.Equals(value, "false", StringComparison.Ordinal))
			return value;

		// YAML null representations: empty string, `null`, `~`.
		if (value.Length == 0 || string.Equals(value, "null", StringComparison.Ordinal) || value == "~")
			return "null";

		if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
			return value;
		if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
			return value;

		return QuoteHocon(value);
	}

	static string QuoteHocon(string value)
	{
		var sb = new StringBuilder(value.Length + 2);
		sb.Append('"');
		AppendHoconEscaped(sb, value);
		sb.Append('"');
		return sb.ToString();
	}

	static void AppendIndent(StringBuilder sb, int level)
	{
		for (var i = 0; i < level; i++)
			sb.Append("  ");
	}

	static void AppendHoconEscaped(StringBuilder sb, string s)
	{
		foreach (var c in s)
		{
			switch (c)
			{
				case '\\': sb.Append("\\\\"); break;
				case '"': sb.Append("\\\""); break;
				case '\n': sb.Append("\\n"); break;
				case '\r': sb.Append("\\r"); break;
				case '\t': sb.Append("\\t"); break;
				default: sb.Append(c); break;
			}
		}
	}
}
