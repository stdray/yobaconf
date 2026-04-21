using System.Diagnostics;
using YobaConf.Core;
using YobaConf.Core.Observability;
using YobaConf.Tests.Fakes;

namespace YobaConf.Tests.Observability;

// ActivityListener-based unit tests — no WebApplicationFactory, no AddOpenTelemetry.
// The listener is process-wide: parallel xUnit classes (ResolvePipelineTests,
// IncludePreprocessorTests) that also call ResolvePipeline.Resolve would pollute the
// capture. To isolate, each assertion runs under a pre-started probe Activity, and we
// filter the captured collection by `TraceId == probe.TraceId`. Probe runs on a test-
// private ActivitySource so cross-class calls — which don't start a probe — originate
// with a different (null-parent, fresh) TraceId and are filtered out.
public sealed class TracingTests : IDisposable
{
	const string ProbeSourceName = "YobaConf.Tests.TracingProbe";

	readonly ActivitySource probeSource = new(ProbeSourceName);
	readonly List<Activity> captured = [];
	readonly ActivityListener listener;

	public TracingTests()
	{
		listener = new ActivityListener
		{
			ShouldListenTo = src =>
				src.Name == ActivitySources.ResolveSourceName
				|| src.Name == ActivitySources.StorageSqliteSourceName
				|| src.Name == ProbeSourceName,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
			ActivityStopped = captured.Add,
		};
		ActivitySource.AddActivityListener(listener);
	}

	public void Dispose()
	{
		listener.Dispose();
		probeSource.Dispose();
	}

	// Runs `action` under a probe-scoped root activity so captured spans can be filtered
	// to ones that belong to this test's async flow. Returns the captured activities that
	// share the probe's TraceId, excluding the probe itself.
	Activity[] Trace(Action action)
	{
		using var probe = probeSource.StartActivity("probe");
		action();
		probe.Should().NotBeNull("the probe source has a listener registered — StartActivity must not return null");
		return [.. captured.Where(a => a.TraceId == probe!.TraceId && a.OperationName != "probe")];
	}

	[Fact]
	public void Resolve_Emits_RootAndAllStageSpans()
	{
		var store = new InMemoryConfigStore();
		store.UpsertNode(NodePath.ParseDb("app"), "port = 8080", DateTimeOffset.UnixEpoch);

		var spans = Trace(() => _ = ResolvePipeline.Resolve(NodePath.ParseDb("app"), store));

		var names = spans.Select(a => a.OperationName).ToArray();
		names.Should().Contain("yobaconf.resolve");
		names.Should().Contain("yobaconf.fallthrough-lookup");
		names.Should().Contain("yobaconf.variables-resolve");
		names.Should().Contain("yobaconf.include-preprocess");
		names.Should().Contain("yobaconf.hocon-parse");
		names.Should().Contain("yobaconf.json-serialize");
		names.Should().Contain("yobaconf.etag-compute");
	}

	[Fact]
	public void ResolveRoot_Span_Tags_RequestedPath()
	{
		var store = new InMemoryConfigStore();
		store.UpsertNode(NodePath.ParseDb("project-a/prod"), "db = localhost", DateTimeOffset.UnixEpoch);

		var spans = Trace(() => _ = ResolvePipeline.Resolve(NodePath.ParseDb("project-a/prod"), store));

		var root = spans.Single(a => a.OperationName == "yobaconf.resolve");
		root.GetTagItem("yobaconf.path").Should().Be("project-a/prod");
	}

	[Fact]
	public void FallthroughLookup_Tag_Resolved_ReflectsBestMatchPath()
	{
		var store = new InMemoryConfigStore();
		store.UpsertNode(NodePath.ParseDb("app"), "fallback = true", DateTimeOffset.UnixEpoch);

		// Request a descendant that doesn't exist — fallthrough walks up to `app`.
		var spans = Trace(() => _ = ResolvePipeline.Resolve(NodePath.ParseDb("app/missing/feature"), store));

		var fallthrough = spans.Single(a => a.OperationName == "yobaconf.fallthrough-lookup");
		fallthrough.GetTagItem("yobaconf.resolved").Should().Be("app");
	}

	[Fact]
	public void VariablesResolve_Tag_Count_ReflectsScopeWalk()
	{
		var store = new InMemoryConfigStore();
		store.UpsertNode(NodePath.ParseDb("project-a/prod"), "db = ${host}", DateTimeOffset.UnixEpoch);
		store.UpsertVariable(NodePath.ParseDb("project-a"), "host", "parent-db", DateTimeOffset.UnixEpoch);
		store.UpsertVariable(NodePath.Root, "global", "g", DateTimeOffset.UnixEpoch);

		var spans = Trace(() => _ = ResolvePipeline.Resolve(NodePath.ParseDb("project-a/prod"), store));

		var vars = spans.Single(a => a.OperationName == "yobaconf.variables-resolve");
		// Nearest-wins scope-walk flattens to 2 variables (root `global` + project-a `host`).
		vars.GetTagItem("yobaconf.variables.count").Should().Be(2);
	}

	[Fact]
	public void Spans_Nest_Under_Root_Resolve()
	{
		var store = new InMemoryConfigStore();
		store.UpsertNode(NodePath.ParseDb("app"), "k = v", DateTimeOffset.UnixEpoch);

		var spans = Trace(() => _ = ResolvePipeline.Resolve(NodePath.ParseDb("app"), store));

		var root = spans.Single(a => a.OperationName == "yobaconf.resolve");
		var children = spans.Where(a => a.OperationName != "yobaconf.resolve").ToArray();

		foreach (var c in children)
		{
			c.ParentSpanId.Should().Be(
				root.SpanId,
				$"{c.OperationName} must nest under yobaconf.resolve so the waterfall UI groups the pipeline under one clickable root");
		}
	}
}
