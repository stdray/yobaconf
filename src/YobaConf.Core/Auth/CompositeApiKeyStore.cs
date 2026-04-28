namespace YobaConf.Core.Auth;

// Stacks IApiKeyStore implementations, first-match-wins on validation. Default wiring:
//   ConfigApiKeyStore (appsettings, read-only) → SqliteApiKeyStore (admin-managed).
//
// Order matters: the config store goes first so a self-host container starts with usable
// keys before any UI interaction. Sqlite second so admin-minted keys still validate.
// Mirrors yobalog/CompositeApiKeyStore in shape; signature is synchronous to match the
// yobaconf IApiKeyStore contract (hot-path on every /v1/conf request, no async IO at
// this layer — both inner stores complete in microseconds).
public sealed class CompositeApiKeyStore : IApiKeyStore
{
    readonly IReadOnlyList<IApiKeyStore> stores;

    public CompositeApiKeyStore(params IApiKeyStore[] stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        this.stores = stores;
    }

    public ApiKeyValidation Validate(string? plaintextToken)
    {
        if (string.IsNullOrEmpty(plaintextToken))
            return new ApiKeyValidation.Invalid("missing api-key");

        foreach (var store in stores)
        {
            var result = store.Validate(plaintextToken);
            if (result is ApiKeyValidation.Valid)
                return result;
        }
        return new ApiKeyValidation.Invalid("unknown api-key");
    }
}
