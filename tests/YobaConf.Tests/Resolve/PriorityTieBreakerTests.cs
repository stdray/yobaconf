using YobaConf.Core.Bindings;
using YobaConf.Core.Resolve;
using YobaConf.Core.Tags;

namespace YobaConf.Tests.Resolve;

public sealed class PriorityTieBreakerTests
{
    static Binding Plain(TagSet tags, string keyPath, string valueJson) => new()
    {
        Id = 0,
        TagSet = tags,
        KeyPath = keyPath,
        Kind = BindingKind.Plain,
        ValuePlain = valueJson,
        ContentHash = string.Empty,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    sealed class TestBindingStore : IBindingStore
    {
        readonly IReadOnlyList<Binding> _bindings;
        public TestBindingStore(IReadOnlyList<Binding> bindings) => _bindings = bindings;
        public IReadOnlyList<Binding> FindMatching(IReadOnlyDictionary<string, string> tagVector) => _bindings;
        public IReadOnlyList<Binding> ListActive() => _bindings;
#pragma warning disable CA1822 // Mark as static (interface member)
        public long Upsert(Binding binding) => _bindings.Count;
#pragma warning disable CA1859 // Interface returns IReadOnlyList (improved perf from List)
        public IReadOnlyList<string> DistinctKeyPaths() => _bindings.Select(b => b.KeyPath).ToList();
#pragma warning restore CA1859
#pragma warning restore CA1822
        public Binding? FindById(long id) => _bindings.FirstOrDefault(b => b.Id == id);
    }

    sealed class TestVocabStore : ITagVocabularyStore
    {
        readonly IReadOnlyList<TagVocabularyEntry> _entries;
        public TestVocabStore(IReadOnlyList<TagVocabularyEntry> entries) => _entries = entries;
        public IReadOnlyList<TagVocabularyEntry> ListActive() => _entries;
        public IReadOnlyList<string> DistinctKeys() => _entries.Select(e => e.Key).ToList();
    }

    [Fact]
    public void TieBreaker_Off_Still_Returns_409_On_Incomparable_Diverging()
    {
        var store = new TestBindingStore([
            Plain(TagSet.From([new("env", "prod")]), "log-level", "\"Info\""),
            Plain(TagSet.From([new("project", "yobapub")]), "log-level", "\"Debug\"")]);

        var vocab = new TestVocabStore([
            new(1, "env", null, "deployment env", 10, DateTimeOffset.UnixEpoch),
            new(2, "project", null, "project name", 5, DateTimeOffset.UnixEpoch)]);

        var pipeline = new ResolvePipeline(store, vocabulary: vocab,
            options: new ResolveOptions { UsePriorityTieBreaker = false });

        var outcome = pipeline.Resolve(new Dictionary<string, string>
        {
            ["env"] = "prod",
            ["project"] = "yobapub",
        });

        outcome.Should().BeOfType<ResolveConflict>();
    }

    [Fact]
    public void TieBreaker_On_Unique_Max_Score_Wins()
    {
        var store = new TestBindingStore([
            Plain(TagSet.From([new("env", "prod")]), "log-level", "\"Info\""),
            Plain(TagSet.From([new("project", "yobapub")]), "log-level", "\"Debug\"")]);

        var vocab = new TestVocabStore([
            new(1, "env", null, "deployment env", 10, DateTimeOffset.UnixEpoch),
            new(2, "project", null, "project name", 3, DateTimeOffset.UnixEpoch)]);

        var pipeline = new ResolvePipeline(store, vocabulary: vocab,
            options: new ResolveOptions { UsePriorityTieBreaker = true });

        var outcome = pipeline.Resolve(new Dictionary<string, string>
        {
            ["env"] = "prod",
            ["project"] = "yobapub",
        });

        var success = outcome.Should().BeOfType<ResolveSuccess>().Subject;
        success.Json.Should().Be("""{"log-level":"Info"}""");
    }

    [Fact]
    public void TieBreaker_On_Tied_Max_Score_Falls_Back_To_409()
    {
        var store = new TestBindingStore([
            Plain(TagSet.From([new("env", "prod")]), "log-level", "\"Info\""),
            Plain(TagSet.From([new("env", "staging")]), "log-level", "\"Debug\"")]);

        var vocab = new TestVocabStore([
            new(1, "env", null, "deployment env", 5, DateTimeOffset.UnixEpoch)]);

        var pipeline = new ResolvePipeline(store, vocabulary: vocab,
            options: new ResolveOptions { UsePriorityTieBreaker = true });

        var outcome = pipeline.Resolve(new Dictionary<string, string>
        {
            ["env"] = "prod",
        });

        outcome.Should().BeOfType<ResolveConflict>();
    }

    [Fact]
    public void TieBreaker_On_Empty_Vocabulary_Falls_Back_To_409()
    {
        var store = new TestBindingStore([
            Plain(TagSet.From([new("env", "prod")]), "log-level", "\"Info\""),
            Plain(TagSet.From([new("project", "yobapub")]), "log-level", "\"Debug\"")]);

        var vocab = new TestVocabStore([]);

        var pipeline = new ResolvePipeline(store, vocabulary: vocab,
            options: new ResolveOptions { UsePriorityTieBreaker = true });

        var outcome = pipeline.Resolve(new Dictionary<string, string>
        {
            ["env"] = "prod",
            ["project"] = "yobapub",
        });

        outcome.Should().BeOfType<ResolveConflict>();
    }
}
