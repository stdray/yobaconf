using System.Text;

namespace YobaConf.Core.Converters;

// `.env` (dotenv) → HOCON conversion. Hand parser — the format is informal but simple.
//
// Supported syntax:
//   KEY=value              — literal value, trimmed of trailing whitespace
//   KEY="quoted value"     — double-quoted, escape sequences \n \r \t \\ \" decoded
//   KEY='literal'          — single-quoted, no escapes (literal)
//   export KEY=value       — optional `export ` prefix stripped
//   # full-line comments   — skipped
//   (empty lines)          — skipped
//
// Not supported (produce ImportException or pass through as literal):
//   - Variable expansion (`${OTHER}`) — left as literal; user can rewrite to HOCON
//     substitutions after import.
//   - Multi-line quoted values — treated as single line (not standard in most .env
//     parsers anyway).
//   - Inline comments after values (`KEY=value # note`) — `#` is part of the value;
//     avoids ambiguity with `#` inside values.
//
// Output: each pair as `"KEY" = "value"\n` (HoconVariableRenderer-compatible form).
public static class DotenvToHoconConverter
{
    public static string Convert(string dotenvText)
    {
        ArgumentNullException.ThrowIfNull(dotenvText);

        var sb = new StringBuilder();
        var lineNumber = 0;
        foreach (var rawLine in dotenvText.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r').Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // Strip optional `export ` prefix (common in shell-sourced .env files).
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();

            var eqIdx = line.IndexOf('=');
            if (eqIdx <= 0)
                throw new ImportException($"Line {lineNumber}: expected `KEY=value`, got '{rawLine}'");

            var key = line[..eqIdx].TrimEnd();
            var valuePart = line[(eqIdx + 1)..];

            if (!IsValidKey(key))
                throw new ImportException($"Line {lineNumber}: invalid key '{key}' (expected [A-Za-z_][A-Za-z0-9_]*)");

            var value = ParseValue(valuePart, lineNumber);

            sb.Append('"');
            AppendHoconEscaped(sb, key);
            sb.Append("\" = \"");
            AppendHoconEscaped(sb, value);
            sb.Append("\"\n");
        }

        return sb.ToString();
    }

    static bool IsValidKey(string key)
    {
        if (key.Length == 0)
            return false;
        var first = key[0];
        if (!(char.IsAsciiLetter(first) || first == '_'))
            return false;
        for (var i = 1; i < key.Length; i++)
        {
            var c = key[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        }
        return true;
    }

    static string ParseValue(string raw, int lineNumber)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        // Double-quoted: decode escape sequences.
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            return DecodeDoubleQuoted(trimmed[1..^1], lineNumber);
        }

        // Single-quoted: literal content, no escapes.
        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            return trimmed[1..^1];
        }

        // Unquoted: take as-is (whitespace-trimmed already).
        return trimmed;
    }

    static string DecodeDoubleQuoted(string content, int lineNumber)
    {
        var sb = new StringBuilder(content.Length);
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            if (i + 1 >= content.Length)
                throw new ImportException($"Line {lineNumber}: trailing backslash in double-quoted value");
            var next = content[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                default:
                    // Unknown escape — pass through as literal `\x` for forgiving parse.
                    sb.Append('\\').Append(next);
                    break;
            }
        }
        return sb.ToString();
    }

    // HOCON quoted-string escape: the render target is a HOCON `"..."` string, so same
    // rules as HoconVariableRenderer — backslash / double-quote / newline / CR / tab.
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
