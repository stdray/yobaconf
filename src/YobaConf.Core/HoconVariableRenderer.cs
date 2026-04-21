using System.Text;

namespace YobaConf.Core;

// Renders a set of Variables as a HOCON-fragment suitable for prepending to a node's
// RawContent before parsing (spec §4.4 pipeline step). Output shape:
//
//     "key1" = "value1"
//     "key2" = "value2"
//
// Every key and value is quoted to sidestep HOCON's unquoted-string ambiguity:
// - Unquoted keys with dots create nested objects (`a.b = 1` → `{a: {b: 1}}`). Quoting
//   forces literal `a.b` as a single key. Variable Keys in the domain are flat.
// - Unquoted values matching number syntax are parsed as numbers (`port = 5432`). For
//   Variables we don't know the intended type — all Values are stored as strings. Quoting
//   emits them as strings in the resulting JSON. If users need numeric types, they write
//   explicit casts in HOCON (`port = ${db_port}` + downstream parse).
//
// Escape set matches HOCON quoted-string rules: backslash, double-quote, LF, CR, Tab.
public static class HoconVariableRenderer
{
	public static string Render(IEnumerable<Variable> variables)
	{
		ArgumentNullException.ThrowIfNull(variables);

		var sb = new StringBuilder();
		// Ordinal sort — stable order matters: same input set -> same fragment bytes -> stable
		// downstream HOCON parse order -> stable canonical JSON -> stable ETag.
		foreach (var v in variables.OrderBy(x => x.Key, StringComparer.Ordinal))
		{
			sb.Append('"');
			AppendEscaped(sb, v.Key);
			sb.Append("\" = \"");
			AppendEscaped(sb, v.Value);
			sb.Append("\"\n");
		}
		return sb.ToString();
	}

	static void AppendEscaped(StringBuilder sb, string s)
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
