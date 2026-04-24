namespace YobaConf.Core.Storage;

// Bound from the `Storage` section of appsettings.json:
//   "Storage": { "DataDirectory": "/app/data", "FileName": "yobaconf.db" }
// Dev default resolves to `%TEMP%/yobaconf` via appsettings.Development.json; tests
// point at a per-test tmp path via a manual Options instance.
public sealed record SqliteBindingStoreOptions
{
    public string DataDirectory { get; init; } = string.Empty;
    public string FileName { get; init; } = "yobaconf.db";
}
