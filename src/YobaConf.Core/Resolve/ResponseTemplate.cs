using System.Text;

namespace YobaConf.Core.Resolve;

// Response-shape templates (spec §9.1). `Flat` preserves the dotted-key hierarchy and
// produces nested JSON — the default; Spring-style consumers read it directly. The three
// env-flavour templates produce a flat JSON object where each key is transformed per the
// platform convention; consumer runner (`yobaconf-run`, C.2) then sets each as an env var
// before exec'ing the child.
//
//   dotnet       "db.host"      → "db__host"  (Microsoft.Extensions.Configuration nests
//                                              on `__`; dashes stay literal)
//   envvar       "db.host"      → "DB_HOST"   (POSIX uppercase; `.` and `-` → `_`)
//   envvar-deep  "db.host"      → "DB__HOST"  (Helm / K8s flavour: `.` → `__`, `-` → `_`,
//                                              uppercase)
public enum ResponseTemplate
{
    Flat = 0,
    Dotnet,
    Envvar,
    EnvvarDeep,
}

public static class ResponseTemplateParser
{
    public static ResponseTemplate Parse(string? raw) => raw?.ToLowerInvariant() switch
    {
        null or "" or "flat" => ResponseTemplate.Flat,
        "dotnet" => ResponseTemplate.Dotnet,
        "envvar" => ResponseTemplate.Envvar,
        "envvar-deep" or "envvar_deep" => ResponseTemplate.EnvvarDeep,
        _ => throw new ArgumentException($"Unknown template '{raw}'. Expected: flat, dotnet, envvar, envvar-deep."),
    };

    public static string Derive(string keyPath, ResponseTemplate template)
    {
        ArgumentNullException.ThrowIfNull(keyPath);
        return template switch
        {
            ResponseTemplate.Flat => keyPath,
            ResponseTemplate.Dotnet => ReplaceSep(keyPath, dot: "__", dash: "-", toUpper: false),
            ResponseTemplate.Envvar => ReplaceSep(keyPath, dot: "_", dash: "_", toUpper: true),
            ResponseTemplate.EnvvarDeep => ReplaceSep(keyPath, dot: "__", dash: "_", toUpper: true),
            _ => throw new ArgumentOutOfRangeException(nameof(template)),
        };
    }

    static string ReplaceSep(string input, string dot, string dash, bool toUpper)
    {
        var sb = new StringBuilder(input.Length + 8);
        foreach (var c in input)
        {
            if (c == '.') sb.Append(dot);
            else if (c == '-') sb.Append(dash);
            else sb.Append(toUpper ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }
}
