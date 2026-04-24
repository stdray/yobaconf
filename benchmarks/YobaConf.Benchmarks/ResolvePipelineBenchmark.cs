using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Options;
using YobaConf.Core.Bindings;
using YobaConf.Core.Resolve;
using YobaConf.Core.Storage;

namespace YobaConf.Benchmarks;

[MemoryDiagnoser]
public sealed class ResolvePipelineBenchmark
{
    SqliteBindingStore _store = default!;
    ResolvePipeline _pipeline = default!;
    Dictionary<string, string> _tagVector = default!;
    string _tmpDir = default!;

    [GlobalSetup]
    public void Setup()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "yobaconf-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);

        _store = new SqliteBindingStore(Options.Create(new SqliteBindingStoreOptions
        {
            DataDirectory = _tmpDir,
            FileName = "bench.db",
        }));

        _pipeline = new ResolvePipeline(_store);

        SeedBindings(_store);

        _tagVector = new Dictionary<string, string>
        {
            ["env"] = "prod",
            ["project"] = "yobapub",
            ["host"] = "web01",
            ["region"] = "eu",
            ["role"] = "api",
        };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    [Benchmark]
    public ResolveOutcome Resolve() => _pipeline.Resolve(_tagVector);

    // "200" is a ceiling; actual inserted count may be slightly under 200 due to
    // deterministic dedup collisions against the UNIQUE(TagSetJson, KeyPath) index.
    static void SeedBindings(IBindingStoreAdmin store)
    {
        var rng = new Random(42);
        var envs = new[] { "prod", "staging", "dev", "local" };
        var projects = new[] { "yobapub", "yobalog", "yobaconf", "yobaspeach" };
        var hosts = new[] { "web01", "web02", "web03", "web04" };
        var regions = new[] { "eu", "us", "ap", "sa" };
        var roles = new[] { "api", "worker", "scheduler", "db" };
        var dims = new[] { envs, projects, hosts, regions, roles };
        var keys = new[] {
            "db.host", "db.port", "cache.ttl", "log.level", "rate.limit",
            "feature.flag1", "feature.flag2", "timeout.read", "timeout.write",
            "retry.count",
        };

        for (var i = 0; i < 200; i++)
        {
            var dimsCount = rng.Next(1, 6);
            var picked = Enumerable.Range(0, 5)
                .OrderBy(_ => rng.Next())
                .Take(dimsCount)
                .ToList();

            var dimKeys = new[] { "env", "project", "host", "region", "role" };
            var pairs = picked.Select(dimIdx => new KeyValuePair<string, string>(
                dimKeys[dimIdx],
                dims[dimIdx][rng.Next(dims[dimIdx].Length)]))
                .ToList();

            var tagSet = TagSet.From(pairs);
            var keyPath = keys[rng.Next(keys.Length)];

            try
            {
                store.Upsert(new Binding
                {
                    Id = 0,
                    TagSet = tagSet,
                    KeyPath = keyPath,
                    Kind = BindingKind.Plain,
                    ValuePlain = $"\"value-{i}\"",
                    ContentHash = string.Empty,
                    UpdatedAt = DateTimeOffset.UnixEpoch,
                }, actor: "bench");
            }
            catch { /* UNIQUE index collision — skip */ }
        }
    }
}
