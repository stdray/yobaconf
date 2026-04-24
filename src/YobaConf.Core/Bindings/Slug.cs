using System.Text.RegularExpressions;

namespace YobaConf.Core.Bindings;

// Shared slug validator for tag-keys, tag-values, and KeyPath segments. Per spec §3:
//   [a-z][a-z0-9-]{0,39} — lowercase alnum + dash, min 1 char, max 40.
//   Optional leading `$` reserves the system prefix (`$schema`, `$meta`, …). System tags
//   are parser-allowed but UI discourages them — no privileged semantics in MVP.
//
// KeyPath is a dotted list of segments each matching the slug; the dot itself is a
// separator, never a segment character. Resolve-time expansion splits on '.' to nest
// into JSON. Tag-keys and tag-values don't get dots.
public static partial class Slug
{
    [GeneratedRegex(@"^\$?[a-z][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)]
    public static partial Regex Pattern { get; }

    public static bool IsValid(string? s) => s is not null && Pattern.IsMatch(s);

    public static void Require(string? s, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!Pattern.IsMatch(s))
            throw new ArgumentException(
                $"{fieldName} '{s}' is not a valid slug — expected lowercase letter followed by up to 39 " +
                $"lowercase-alnum-or-dash characters (optional leading '$' for system names).",
                fieldName);
    }

    // KeyPath: dotted form ("db.host", "cache.policy.lru"). Every segment is a slug.
    // Empty string is rejected, as are leading/trailing/double dots.
    public static void RequireKeyPath(string? keyPath)
    {
        ArgumentNullException.ThrowIfNull(keyPath);
        if (keyPath.Length == 0)
            throw new ArgumentException("KeyPath cannot be empty.", nameof(keyPath));

        foreach (var segment in keyPath.Split('.'))
            if (!Pattern.IsMatch(segment))
                throw new ArgumentException(
                    $"KeyPath '{keyPath}' contains invalid segment '{segment}'. " +
                    "Each dot-separated segment must match [a-z][a-z0-9-]{0,39} (optional leading '$').",
                    nameof(keyPath));
    }
}
