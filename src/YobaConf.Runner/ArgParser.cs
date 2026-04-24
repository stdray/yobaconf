using System.Collections;

namespace YobaConf.Runner;

// CLI layout (spec §9.2):
//   yobaconf-run [--endpoint URL] [--api-key T] [--tag k=v]... [--template NAME] -- CHILD_CMD CHILD_ARG...
// Env-var fallbacks kick in when the corresponding flag is absent:
//   YOBACONF_ENDPOINT / YOBACONF_API_KEY / YOBACONF_TEMPLATE
// Every `--tag k=v` accumulates; duplicates use the last value (matches HTTP semantics).
// Positional args after `--` are the child command + args; missing `--` is an error.
public static class ArgParser
{
    public const string UsageText = """
		Usage: yobaconf-run [OPTIONS] -- CHILD_CMD [CHILD_ARG...]

		Options:
		  --endpoint URL       Resolve endpoint (env: YOBACONF_ENDPOINT)
		  --api-key TOKEN      API key plaintext (env: YOBACONF_API_KEY)
		  --tag KEY=VALUE      Tag component (repeatable)
		  --template NAME      Response template: dotnet | envvar | envvar-deep (env: YOBACONF_TEMPLATE)
		                       Default: envvar (flat is not suitable as env vars)

		Exit codes: 0=child-mirror, 2=conflict, 3=scope, 4=connection, 5=bad-args.
		""";

    public abstract record Result;
    public sealed record Ok(RunnerOptions Options) : Result;
    public sealed record Invalid(string Message) : Result;

    public static Result Parse(string[] args, IDictionary envVars)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(envVars);

        string? endpoint = EnvOrNull(envVars, "YOBACONF_ENDPOINT");
        string? apiKey = EnvOrNull(envVars, "YOBACONF_API_KEY");
        string? template = EnvOrNull(envVars, "YOBACONF_TEMPLATE") ?? "envvar";
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        var childArgs = new List<string>();
        var seenDashDash = false;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (seenDashDash)
            {
                childArgs.Add(a);
                continue;
            }

            if (a == "--") { seenDashDash = true; continue; }

            if (TryConsumeValue(args, ref i, "--endpoint", out var v)) { endpoint = v; continue; }
            if (TryConsumeValue(args, ref i, "--api-key", out v)) { apiKey = v; continue; }
            if (TryConsumeValue(args, ref i, "--template", out v)) { template = v; continue; }
            if (TryConsumeValue(args, ref i, "--tag", out v))
            {
                var eq = v.IndexOf('=');
                if (eq <= 0 || eq == v.Length - 1)
                    return new Invalid($"--tag value '{v}' is not in KEY=VALUE form");
                tags[v[..eq]] = v[(eq + 1)..];
                continue;
            }

            return new Invalid($"unknown argument '{a}'");
        }

        if (!seenDashDash || childArgs.Count == 0)
            return new Invalid("missing child command — supply it after `--`");
        if (string.IsNullOrEmpty(endpoint))
            return new Invalid("--endpoint (or YOBACONF_ENDPOINT) is required");
        if (string.IsNullOrEmpty(apiKey))
            return new Invalid("--api-key (or YOBACONF_API_KEY) is required");

        return new Ok(new RunnerOptions(endpoint, apiKey, tags, template, childArgs));
    }

    static bool TryConsumeValue(string[] args, ref int i, string name, out string value)
    {
        var a = args[i];
        if (a == name)
        {
            if (i + 1 >= args.Length) { value = ""; return false; }
            value = args[++i];
            return true;
        }
        if (a.StartsWith(name + "=", StringComparison.Ordinal))
        {
            value = a[(name.Length + 1)..];
            return true;
        }
        value = "";
        return false;
    }

    static string? EnvOrNull(IDictionary env, string key)
    {
        if (!env.Contains(key)) return null;
        var raw = env[key]?.ToString();
        return string.IsNullOrEmpty(raw) ? null : raw;
    }
}
