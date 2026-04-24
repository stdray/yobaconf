using System.Diagnostics;

namespace YobaConf.Core.Observability;

// Two named ActivitySources per Phase C.5 scope. Web-side registration picks both up
// via `t.AddSource(ActivitySources.ResolveSourceName, ActivitySources.StorageSqliteSourceName)`.
//
// Philosophy mirrors yobalog's Observability/ActivitySources — const names exposed for
// the AddSource call-site, ActivitySource instances used at instrumentation points.
// Two sources (not five as in yobalog) because yobaconf's architecture is flatter:
//   * Resolve — the whole pipeline inside ResolvePipeline.Resolve
//   * Storage.Sqlite — SqliteConfigStore read/write boundaries
//
// HTTP and the ASP.NET Core root span are emitted by OpenTelemetry.Instrumentation.AspNetCore
// automatically (source "Microsoft.AspNetCore.Hosting.HttpRequestIn"); we don't manually
// start spans for /v1/conf — the child Resolve spans nest into whatever that auto-span
// creates per request.
public static class ActivitySources
{
    public const string ResolveSourceName = "YobaConf.Resolve";
    public const string StorageSqliteSourceName = "YobaConf.Storage.Sqlite";

    public static readonly ActivitySource Resolve = new(ResolveSourceName);
    public static readonly ActivitySource StorageSqlite = new(StorageSqliteSourceName);
}
