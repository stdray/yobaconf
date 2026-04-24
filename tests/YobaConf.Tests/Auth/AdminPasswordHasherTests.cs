namespace YobaConf.Tests.Auth;

public class AdminPasswordHasherTests
{
    [Fact]
    public void Hash_Then_Verify_Succeeds()
    {
        var encoded = AdminPasswordHasher.Hash("correct-horse-battery-staple");
        AdminPasswordHasher.Verify("correct-horse-battery-staple", encoded).Should().BeTrue();
    }

    [Fact]
    public void Verify_WithWrongPassword_Fails()
    {
        var encoded = AdminPasswordHasher.Hash("secret");
        AdminPasswordHasher.Verify("not-secret", encoded).Should().BeFalse();
    }

    [Fact]
    public void Hash_IsNonDeterministic_DueToRandomSalt()
    {
        // Same input twice → different encoded strings (different salt each call), but both verify.
        var a = AdminPasswordHasher.Hash("same");
        var b = AdminPasswordHasher.Hash("same");
        a.Should().NotBe(b);
        AdminPasswordHasher.Verify("same", a).Should().BeTrue();
        AdminPasswordHasher.Verify("same", b).Should().BeTrue();
    }

    [Fact]
    public void Verify_WithMalformedHash_Fails_SafelyInsteadOfThrowing()
    {
        AdminPasswordHasher.Verify("any", "not-a-valid-hash").Should().BeFalse();
        AdminPasswordHasher.Verify("any", string.Empty).Should().BeFalse();
        AdminPasswordHasher.Verify("any", "pbkdf2$abc$!!!$~~~").Should().BeFalse();
    }

    [Fact]
    public void Verify_WithOtherAlgorithmPrefix_Fails()
    {
        AdminPasswordHasher.Verify("any", "argon2id$100$salt$hash").Should().BeFalse();
    }

    [Fact]
    public void Hash_EncodedFormat_IsPbkdf2_PrefixedAndDollarSeparated()
    {
        var encoded = AdminPasswordHasher.Hash("x");
        var parts = encoded.Split('$');
        parts.Should().HaveCount(4);
        parts[0].Should().Be("pbkdf2");
        int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThan(0);
    }
}
