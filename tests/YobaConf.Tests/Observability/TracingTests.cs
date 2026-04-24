using System.Collections.Concurrent;
using System.Diagnostics;
using YobaConf.Core.Bindings;
using YobaConf.Core.Resolve;
using YobaConf.Tests.Storage;

namespace YobaConf.Tests.Observability;

// ActivitySource is process-wide — once a listener subscribes to "YobaConf.Resolve" it
// captures spans from every parallel test class that happens to call Resolve. To isolate
// per-test, we pre-start a probe activity on a test-scoped source so every span emitted
// by this test's Resolve call nests under the probe's TraceId, then filter captured spans
// by that TraceId. Sibling test classes running concurrently emit under their own probe
// (or no ambient activity) and are trimmed out.
public sealed class TracingTests : IDisposable
{
    static readonly ActivitySource ProbeSource = new("YobaConf.Tests.Probe");

    readonly ActivityListener _listener;
    // ConcurrentBag — parallel test classes (from SqliteBindingStoreTests, ResolvePipelineTests)
    // also emit to YobaConf.Storage.Sqlite / YobaConf.Resolve sources; their spans flow into
    // THIS listener (it's process-wide) from other threads. List<T>.Add isn't thread-safe.
    readonly ConcurrentBag<Activity> _captured = [];

    public TracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src =>
                src.Name is "YobaConf.Resolve"
                    or "YobaConf.Storage.Sqlite"
                    or "YobaConf.Tests.Probe",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => _captured.Add(a),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    static Binding Plain(TagSet t, string k, string v) => new()
    {
        Id = 0,
        TagSet = t,
        KeyPath = k,
        Kind = BindingKind.Plain,
        ValuePlain = v,
        ContentHash = string.Empty,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    IReadOnlyList<Activity> SpansForProbe(string probeTraceId) =>
        [.. _captured.Where(a => a.TraceId.ToString() == probeTraceId)];

    [Fact]
    public void Resolve_Emits_Root_And_All_Stage_Spans()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(TagSet.Empty, "k", "\"v\""));

        using var probe = ProbeSource.StartActivity("probe");
        new ResolvePipeline(store).Resolve(new Dictionary<string, string>());
        var traceId = probe!.TraceId.ToString();
        probe.Dispose();

        var mine = SpansForProbe(traceId);
        mine.Select(a => a.DisplayName).Should().Contain("yobaconf.resolve");
        mine.Select(a => a.DisplayName).Should().Contain("candidate-lookup");
        mine.Select(a => a.DisplayName).Should().Contain("group-by-key");
        mine.Select(a => a.DisplayName).Should().Contain("expand-dotted");
        mine.Select(a => a.DisplayName).Should().Contain("canonical-json");
        mine.Select(a => a.DisplayName).Should().Contain("etag-compute");
    }

    [Fact]
    public void Resolve_Root_TagsVectorCount()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();

        using var probe = ProbeSource.StartActivity("probe");
        new ResolvePipeline(store).Resolve(new Dictionary<string, string>
        {
            ["env"] = "prod",
            ["project"] = "yobapub",
        });
        var traceId = probe!.TraceId.ToString();
        probe.Dispose();

        var resolveRoot = SpansForProbe(traceId).FirstOrDefault(a => a.DisplayName == "yobaconf.resolve");
        resolveRoot.Should().NotBeNull();
        resolveRoot!.GetTagItem("yobaconf.tag-vector.count").Should().Be(2);
    }

    [Fact]
    public void Conflict_Marks_Root_Span_Error()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(TagSet.From([new("env", "prod")]), "k", "\"a\""));
        store.Upsert(Plain(TagSet.From([new("project", "yobapub")]), "k", "\"b\""));

        using var probe = ProbeSource.StartActivity("probe");
        new ResolvePipeline(store).Resolve(new Dictionary<string, string>
        {
            ["env"] = "prod",
            ["project"] = "yobapub",
        });
        var traceId = probe!.TraceId.ToString();
        probe.Dispose();

        var root = SpansForProbe(traceId).First(a => a.DisplayName == "yobaconf.resolve");
        root.Status.Should().Be(ActivityStatusCode.Error);
        root.GetTagItem("yobaconf.conflict.key").Should().Be("k");
    }

    [Fact]
    public void SqliteStore_Emits_Spans_For_Upsert_And_ListActive()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();

        using var probe = ProbeSource.StartActivity("probe");
        store.Upsert(Plain(TagSet.From([new("env", "prod")]), "k", "\"v\""));
        store.ListActive();
        var traceId = probe!.TraceId.ToString();
        probe.Dispose();

        var names = SpansForProbe(traceId).Select(a => a.DisplayName).ToArray();
        names.Should().Contain("sqlite.upsert-binding");
        names.Should().Contain("sqlite.list-bindings");
    }

    [Fact]
    public void ChildSpans_Nest_Under_Root()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        store.Upsert(Plain(TagSet.Empty, "k", "\"v\""));

        using var probe = ProbeSource.StartActivity("probe");
        new ResolvePipeline(store).Resolve(new Dictionary<string, string>());
        var traceId = probe!.TraceId.ToString();
        probe.Dispose();

        var mine = SpansForProbe(traceId);
        var root = mine.First(a => a.DisplayName == "yobaconf.resolve");
        var candidateLookup = mine.First(a => a.DisplayName == "candidate-lookup");
        candidateLookup.ParentSpanId.Should().Be(root.SpanId);
    }
}
