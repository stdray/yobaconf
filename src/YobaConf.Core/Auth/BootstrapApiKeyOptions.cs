namespace YobaConf.Core.Auth;

// Bootstrap api-keys declared in appsettings / env-vars. Read-only — managed alongside
// the rest of the service config, not via the admin UI. Intended for self-host bundles
// (docker-compose) so the container starts with a working key before any UI step.
//
// Section path: BootstrapApiKeys:Keys[i]. Env-var override e.g.:
//   BootstrapApiKeys__Keys__0__Token=...
//   BootstrapApiKeys__Keys__0__Description=yobapub-server
//   BootstrapApiKeys__Keys__0__RequiredTags__env=prod
//   BootstrapApiKeys__Keys__0__RequiredTags__project=yobapub
//   BootstrapApiKeys__Keys__0__AllowedKeyPrefixes__0=db.
public sealed record BootstrapApiKeyOptions
{
    public IReadOnlyList<BootstrapApiKeyEntry> Keys { get; init; } = [];
}

public sealed record BootstrapApiKeyEntry
{
    public string Token { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyDictionary<string, string> RequiredTags { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyList<string>? AllowedKeyPrefixes { get; init; }
}
