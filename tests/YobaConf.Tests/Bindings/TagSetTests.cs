using YobaConf.Core.Bindings;

namespace YobaConf.Tests.Bindings;

public sealed class TagSetTests
{
	[Fact]
	public void CanonicalJson_IsByteIdentical_AcrossKeyOrder()
	{
		// Load-bearing property: UNIQUE (TagSetJson, KeyPath) dedupe relies on this.
		var a = TagSet.From([new("project", "yobapub"), new("env", "prod")]);
		var b = TagSet.From([new("env", "prod"), new("project", "yobapub")]);
		a.CanonicalJson.Should().Be(b.CanonicalJson);
		a.CanonicalJson.Should().Be("""{"env":"prod","project":"yobapub"}""");
	}

	[Fact]
	public void Empty_Serializes_As_EmptyObject() =>
		TagSet.Empty.CanonicalJson.Should().Be("{}");

	[Fact]
	public void Equality_Is_ValueBased()
	{
		var a = TagSet.From([new("env", "prod")]);
		var b = TagSet.From([new("env", "prod")]);
		a.Equals(b).Should().BeTrue();
		a.GetHashCode().Should().Be(b.GetHashCode());
	}

	[Theory]
	[InlineData("Uppercase")]
	[InlineData("has space")]
	[InlineData("dot.in.key")]
	[InlineData("")]
	[InlineData("_leading-underscore")]
	public void Invalid_Slug_Key_Throws(string badKey) =>
		FluentActions.Invoking(() => TagSet.From([new(badKey, "prod")]))
			.Should().Throw<ArgumentException>();

	[Fact]
	public void Duplicate_Key_Throws() =>
		FluentActions.Invoking(() => TagSet.From([new("env", "prod"), new("env", "staging")]))
			.Should().Throw<ArgumentException>()
			.WithMessage("*Duplicate tag-key 'env'*");

	[Fact]
	public void IsSubsetOf_True_When_AllPairsMatch()
	{
		var self = TagSet.From([new("env", "prod"), new("project", "yobapub")]);
		var superset = new Dictionary<string, string>
		{
			["env"] = "prod",
			["project"] = "yobapub",
			["region"] = "eu-west",
		};
		self.IsSubsetOf(superset).Should().BeTrue();
	}

	[Fact]
	public void IsSubsetOf_False_When_Value_Differs()
	{
		var self = TagSet.From([new("env", "prod")]);
		var superset = new Dictionary<string, string> { ["env"] = "staging" };
		self.IsSubsetOf(superset).Should().BeFalse();
	}

	[Fact]
	public void IsSubsetOf_EmptySet_Always_True()
	{
		TagSet.Empty.IsSubsetOf(new Dictionary<string, string>()).Should().BeTrue();
		TagSet.Empty.IsSubsetOf(new Dictionary<string, string> { ["env"] = "prod" }).Should().BeTrue();
	}

	[Fact]
	public void FromCanonicalJson_Roundtrips()
	{
		const string json = """{"env":"prod","project":"yobapub"}""";
		var parsed = TagSet.FromCanonicalJson(json);
		parsed.CanonicalJson.Should().Be(json);
		parsed.Count.Should().Be(2);
	}

	[Fact]
	public void Count_Matches_DistinctPairs() =>
		TagSet.From([new("env", "prod"), new("project", "yobapub"), new("region", "eu-west")])
			.Count.Should().Be(3);
}
