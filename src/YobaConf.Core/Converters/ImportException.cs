namespace YobaConf.Core.Converters;

// Unified error type for all three paste-import converters (JSON / YAML / .env).
// Callers (paste-import UI handler) catch once and surface the message to the user.
// Wraps native parser exceptions (System.Text.Json.JsonException, YamlDotNet.YamlException,
// etc.) to keep the public surface stable if we ever swap parser libraries.
public sealed class ImportException : Exception
{
    public ImportException(string message) : base(message) { }
    public ImportException(string message, Exception inner) : base(message, inner) { }
}
