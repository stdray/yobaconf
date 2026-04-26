using Microsoft.Extensions.Options;
using YobaConf.Core.Audit;
using YobaConf.Core.Auth;
using YobaConf.Core.Storage;
using YobaConf.Tests.Storage;

namespace YobaConf.Tests.Auth;

public sealed class SqliteAdminTokenStoreTests
{
    static (SqliteAdminTokenStore tokens, SqliteUserStore users, SqliteAuditLogStore audit) Wire(TempDb tmp)
    {
        var opts = Options.Create(new SqliteBindingStoreOptions
        {
            DataDirectory = tmp.Directory,
            FileName = tmp.FileName,
        });
        return (new SqliteAdminTokenStore(opts), new SqliteUserStore(opts), new SqliteAuditLogStore(opts));
    }

    [Fact]
    public void Create_ReturnsPlaintext_Once_AndStoresOnlyHash()
    {
        using var tmp = new TempDb();
        var (tokens, _, _) = Wire(tmp);

        var created = tokens.Create("alice", "laptop dev", DateTimeOffset.UnixEpoch, "alice");

        created.Plaintext.Should().HaveLength(22);
        created.Info.TokenPrefix.Should().Be(created.Plaintext[..6]);
        created.Info.Username.Should().Be("alice");
        created.Info.Description.Should().Be("laptop dev");

        // Listed snapshot doesn't surface plaintext; only prefix is exposed.
        var listed = tokens.ListByUsername("alice").Single();
        listed.Id.Should().Be(created.Info.Id);
        listed.TokenPrefix.Should().Be(created.Info.TokenPrefix);
    }

    [Fact]
    public void Validate_Success_On_KnownToken_Carries_Username()
    {
        using var tmp = new TempDb();
        var (tokens, _, _) = Wire(tmp);
        var created = tokens.Create("alice", "ci", DateTimeOffset.UnixEpoch, "alice");

        var outcome = tokens.Validate(created.Plaintext);

        var valid = outcome.Should().BeOfType<AdminTokenValidation.Valid>().Subject;
        valid.Token.Id.Should().Be(created.Info.Id);
        valid.Token.Username.Should().Be("alice");
        valid.Token.TokenPrefix.Should().Be(created.Info.TokenPrefix);
    }

    [Fact]
    public void Validate_Fails_On_Null_Empty_OrWrongToken()
    {
        using var tmp = new TempDb();
        var (tokens, _, _) = Wire(tmp);
        tokens.Create("alice", "k", DateTimeOffset.UnixEpoch, "alice");

        tokens.Validate(null).Should().BeOfType<AdminTokenValidation.Invalid>();
        tokens.Validate(string.Empty).Should().BeOfType<AdminTokenValidation.Invalid>();
        tokens.Validate("NOTaV4lIDT0kenXXXXXX22").Should().BeOfType<AdminTokenValidation.Invalid>();
    }

    [Fact]
    public void Validate_Fails_On_SoftDeleted_Token()
    {
        using var tmp = new TempDb();
        var (tokens, _, audit) = Wire(tmp);
        var created = tokens.Create("alice", "k", DateTimeOffset.UnixEpoch, "alice");

        tokens.SoftDelete(created.Info.Id, DateTimeOffset.UtcNow, "alice").Should().BeTrue();
        tokens.Validate(created.Plaintext).Should().BeOfType<AdminTokenValidation.Invalid>();

        // Audit log records the self-revoke under the token's prefix; the cascade marker
        // is absent (this is a self-revoke, not a user-delete cascade).
        var entries = audit.Query(AuditEntityType.AdminToken, null, null, 10);
        var deleted = entries.Single(e => e.Action == AuditAction.Deleted);
        deleted.KeyPath.Should().Be(created.Info.TokenPrefix);
        deleted.OldValue.Should().NotContain("cascade=");
    }

    [Fact]
    public void ListByUsername_Returns_Only_Live_Tokens_Of_That_User()
    {
        using var tmp = new TempDb();
        var (tokens, _, _) = Wire(tmp);

        var aliceLaptop = tokens.Create("alice", "laptop", DateTimeOffset.UnixEpoch, "alice");
        var aliceCi = tokens.Create("alice", "ci", DateTimeOffset.UnixEpoch, "alice");
        tokens.Create("bob", "bob's", DateTimeOffset.UnixEpoch, "bob");
        tokens.SoftDelete(aliceLaptop.Info.Id, DateTimeOffset.UtcNow, "alice");

        var aliceTokens = tokens.ListByUsername("alice");
        aliceTokens.Should().ContainSingle(t => t.Id == aliceCi.Info.Id);

        tokens.ListByUsername("bob").Should().HaveCount(1);
        tokens.ListByUsername("nobody").Should().BeEmpty();
    }

    [Fact]
    public void HardDelete_Cascade_When_User_Is_Deleted()
    {
        using var tmp = new TempDb();
        var (tokens, users, audit) = Wire(tmp);

        users.Create("alice", "pw", DateTimeOffset.UnixEpoch, "root");
        var t1 = tokens.Create("alice", "laptop", DateTimeOffset.UnixEpoch, "alice");
        var t2 = tokens.Create("alice", "ci", DateTimeOffset.UnixEpoch, "alice");

        users.Delete("alice", DateTimeOffset.UnixEpoch, "root").Should().BeTrue();

        // Both tokens are gone from the live + archived view (hard-delete).
        tokens.ListByUsername("alice").Should().BeEmpty();
        tokens.Validate(t1.Plaintext).Should().BeOfType<AdminTokenValidation.Invalid>();
        tokens.Validate(t2.Plaintext).Should().BeOfType<AdminTokenValidation.Invalid>();

        // Each cascade-deleted token has its own audit row tagged with cascade=user-delete.
        var cascadeEntries = audit.Query(AuditEntityType.AdminToken, "root", null, 100)
            .Where(e => e.Action == AuditAction.Deleted)
            .ToArray();
        cascadeEntries.Should().HaveCount(2);
        cascadeEntries.All(e => e.OldValue!.Contains("cascade=user-delete")).Should().BeTrue();
        cascadeEntries.Select(e => e.KeyPath).Should().BeEquivalentTo([t1.Info.TokenPrefix, t2.Info.TokenPrefix]);
    }

    [Fact]
    public void SoftDelete_Returns_False_On_Missing_Or_Repeated()
    {
        using var tmp = new TempDb();
        var (tokens, _, _) = Wire(tmp);
        tokens.SoftDelete(9999, DateTimeOffset.UtcNow, "alice").Should().BeFalse();

        var created = tokens.Create("alice", "k", DateTimeOffset.UnixEpoch, "alice");
        tokens.SoftDelete(created.Info.Id, DateTimeOffset.UtcNow, "alice").Should().BeTrue();
        tokens.SoftDelete(created.Info.Id, DateTimeOffset.UtcNow, "alice").Should().BeFalse();
    }
}
