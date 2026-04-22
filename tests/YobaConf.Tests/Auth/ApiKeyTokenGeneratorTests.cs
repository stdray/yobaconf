using YobaConf.Core.Auth;

namespace YobaConf.Tests.Auth;

public sealed class ApiKeyTokenGeneratorTests
{
	[Fact]
	public void New_Produces_TwentyTwoCharTokens() =>
		ApiKeyTokenGenerator.New().Should().HaveLength(22);

	[Fact]
	public void New_IsUrlSafe_NoPlusSlashPadding()
	{
		for (var i = 0; i < 20; i++)
		{
			var token = ApiKeyTokenGenerator.New();
			token.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
		}
	}

	[Fact]
	public void New_IsUnique_AcrossCalls()
	{
		var tokens = Enumerable.Range(0, 100).Select(_ => ApiKeyTokenGenerator.New()).ToHashSet();
		tokens.Should().HaveCount(100);
	}

	[Fact]
	public void HashHex_IsDeterministic() =>
		ApiKeyTokenGenerator.HashHex("abc").Should().Be(ApiKeyTokenGenerator.HashHex("abc"));

	[Fact]
	public void HashHex_Differs_ForDifferentInputs() =>
		ApiKeyTokenGenerator.HashHex("abc").Should().NotBe(ApiKeyTokenGenerator.HashHex("abd"));

	[Fact]
	public void Prefix_Is_FirstSixChars() =>
		ApiKeyTokenGenerator.Prefix("abcdefghij").Should().Be("abcdef");
}
