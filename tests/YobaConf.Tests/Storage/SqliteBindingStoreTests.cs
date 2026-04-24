using YobaConf.Core.Bindings;

namespace YobaConf.Tests.Storage;

public sealed class SqliteBindingStoreTests
{
    static Binding Plain(TagSet tags, string keyPath, string valuePlain) => new()
    {
        Id = 0,
        TagSet = tags,
        KeyPath = keyPath,
        Kind = BindingKind.Plain,
        ValuePlain = valuePlain,
        ContentHash = string.Empty,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    static Binding Secret(TagSet tags, string keyPath, byte[] ciphertext, byte[] iv, byte[] authTag) => new()
    {
        Id = 0,
        TagSet = tags,
        KeyPath = keyPath,
        Kind = BindingKind.Secret,
        Ciphertext = ciphertext,
        Iv = iv,
        AuthTag = authTag,
        KeyVersion = "v1",
        ContentHash = string.Empty,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Upsert_Insert_Roundtrips_PlainBinding()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        var tags = TagSet.From([new("env", "prod")]);

        var outcome = store.Upsert(Plain(tags, "db.host", "\"prod-db\"") with
        {
            UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
        });

        outcome.OldHash.Should().BeNull();
        outcome.Binding.Id.Should().BeGreaterThan(0);
        outcome.Binding.ContentHash.Should().NotBeEmpty();

        var fetched = store.FindById(outcome.Binding.Id);
        fetched.Should().NotBeNull();
        fetched!.TagSet.CanonicalJson.Should().Be(tags.CanonicalJson);
        fetched.KeyPath.Should().Be("db.host");
        fetched.Kind.Should().Be(BindingKind.Plain);
        fetched.ValuePlain.Should().Be("\"prod-db\"");
        fetched.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Upsert_Insert_Roundtrips_SecretBinding()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        var tags = TagSet.From([new("env", "prod")]);
        var ciphertext = new byte[] { 1, 2, 3, 4, 5 };
        var iv = new byte[] { 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 };
        var authTag = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0x00 };

        var outcome = store.Upsert(Secret(tags, "db.password", ciphertext, iv, authTag));

        var fetched = store.FindById(outcome.Binding.Id);
        fetched!.Kind.Should().Be(BindingKind.Secret);
        fetched.Ciphertext.Should().Equal(ciphertext);
        fetched.Iv.Should().Equal(iv);
        fetched.AuthTag.Should().Equal(authTag);
        fetched.KeyVersion.Should().Be("v1");
        fetched.ValuePlain.Should().BeNull();
    }

    [Fact]
    public void Upsert_Twice_SameCoordinate_UpdatesInPlace_And_ReturnsOldHash()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        var tags = TagSet.From([new("env", "prod")]);

        var first = store.Upsert(Plain(tags, "log.level", "\"Info\""));
        var second = store.Upsert(Plain(tags, "log.level", "\"Debug\""));

        second.Binding.Id.Should().Be(first.Binding.Id, "same coordinate → same row Id preserved");
        second.OldHash.Should().Be(first.Binding.ContentHash);
        second.Binding.ContentHash.Should().NotBe(first.Binding.ContentHash);

        store.ListActive().Should().ContainSingle();
        store.FindById(first.Binding.Id)!.ValuePlain.Should().Be("\"Debug\"");
    }

    [Fact]
    public void Upsert_TagSet_IsCanonical_DuplicatesCollapse()
    {
        // Inserting with one key-order then "inserting" with reversed order must UPDATE, not
        // INSERT — canonical JSON collapses them to the same row.
        using var tmp = new TempDb();
        var store = tmp.CreateStore();

        var tagsAB = TagSet.From([new("env", "prod"), new("project", "yobapub")]);
        var tagsBA = TagSet.From([new("project", "yobapub"), new("env", "prod")]);

        var first = store.Upsert(Plain(tagsAB, "db.host", "\"a\""));
        var second = store.Upsert(Plain(tagsBA, "db.host", "\"b\""));

        second.Binding.Id.Should().Be(first.Binding.Id);
        store.ListActive().Should().ContainSingle();
    }

    [Fact]
    public void SoftDelete_Then_Resurrect_AssignsNewId_WithoutConflict()
    {
        // Partial UNIQUE index on (TagSetJson, KeyPath) WHERE IsDeleted=0 must let a new
        // active row sit at the same coordinate as a soft-deleted row. Resurrect path is
        // "insert fresh", not "un-delete" — audit trail stays append-only.
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        var tags = TagSet.From([new("env", "prod")]);

        var first = store.Upsert(Plain(tags, "db.host", "\"v1\""));
        var deleted = store.SoftDelete(first.Binding.Id, DateTimeOffset.UtcNow);
        deleted.Should().BeTrue();

        var second = store.Upsert(Plain(tags, "db.host", "\"v2\""));
        second.Binding.Id.Should().NotBe(first.Binding.Id, "new Id after resurrect — tombstone stays for audit");
        second.OldHash.Should().BeNull();

        store.ListActive().Should().ContainSingle(b => b.Id == second.Binding.Id);
        store.FindById(first.Binding.Id)!.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public void SoftDelete_ReturnsFalse_On_Missing_Or_AlreadyDeleted()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();
        var tags = TagSet.From([new("env", "prod")]);

        store.SoftDelete(9999, DateTimeOffset.UtcNow).Should().BeFalse();

        var inserted = store.Upsert(Plain(tags, "k", "\"v\""));
        store.SoftDelete(inserted.Binding.Id, DateTimeOffset.UtcNow).Should().BeTrue();
        store.SoftDelete(inserted.Binding.Id, DateTimeOffset.UtcNow).Should().BeFalse("already tombstoned");
    }

    [Fact]
    public void FindMatching_ReturnsOnly_SubsetTagSets()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();

        store.Upsert(Plain(TagSet.Empty, "log.format", "\"json\""));
        store.Upsert(Plain(TagSet.From([new("env", "prod")]), "db.host", "\"prod-db\""));
        store.Upsert(Plain(TagSet.From([new("env", "staging")]), "db.host", "\"staging-db\""));
        store.Upsert(Plain(TagSet.From([new("env", "prod"), new("project", "yobapub")]), "log.level", "\"Info\""));
        store.Upsert(Plain(TagSet.From([new("role", "worker")]), "threads", "16"));

        var vector = new Dictionary<string, string>
        {
            ["env"] = "prod",
            ["project"] = "yobapub",
            ["region"] = "eu-west",
        };
        var matched = store.FindMatching(vector);

        matched.Should().HaveCount(3, "empty + env=prod + env=prod+project=yobapub all subset; staging and role=worker excluded");
        matched.Select(b => b.KeyPath).Should().BeEquivalentTo(["log.format", "db.host", "log.level"]);
    }

    [Fact]
    public void ListActive_Excludes_SoftDeleted()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();

        var a = store.Upsert(Plain(TagSet.From([new("env", "prod")]), "a", "\"1\""));
        var b = store.Upsert(Plain(TagSet.From([new("env", "prod")]), "b", "\"2\""));
        store.SoftDelete(a.Binding.Id, DateTimeOffset.UtcNow);

        store.ListActive().Should().ContainSingle(x => x.Id == b.Binding.Id);
    }

    [Fact]
    public void Schema_Bootstrap_Is_Idempotent_Across_Instances()
    {
        // Second SqliteBindingStore on the same dbPath must not choke on existing tables —
        // AllStatements uses CREATE IF NOT EXISTS. Mirrors yobalog's schema-replay pattern.
        using var tmp = new TempDb();
        var first = tmp.CreateStore();
        first.Upsert(Plain(TagSet.From([new("env", "prod")]), "k", "\"v\""));

        var second = tmp.CreateStore();
        second.ListActive().Should().ContainSingle();
    }

    [Fact]
    public void Upsert_RejectsInvalidKeyPath()
    {
        using var tmp = new TempDb();
        var store = tmp.CreateStore();

        FluentActions.Invoking(() => store.Upsert(Plain(TagSet.Empty, "Uppercase.key", "\"v\"")))
            .Should().Throw<ArgumentException>().WithMessage("*invalid segment*");

        FluentActions.Invoking(() => store.Upsert(Plain(TagSet.Empty, "", "\"v\"")))
            .Should().Throw<ArgumentException>();
    }
}
