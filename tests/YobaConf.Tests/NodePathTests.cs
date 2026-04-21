namespace YobaConf.Tests;

public class NodePathTests
{
	[Fact]
	public void ParseDb_AllowsValidSegments() =>
		NodePath.ParseDb("yobaproj/yobaapp/prod").ToDbPath().Should().Be("yobaproj/yobaapp/prod");

	[Fact]
	public void ParseUrl_ConvertsDotsToSegments() =>
		NodePath.ParseUrl("yobaproj.yobaapp.prod").ToDbPath().Should().Be("yobaproj/yobaapp/prod");

	[Fact]
	public void ToUrlPath_RoundTripsThroughDotNotation() =>
		NodePath.ParseDb("yobaproj/yobaapp").ToUrlPath().Should().Be("yobaproj.yobaapp");

	[Theory]
	[InlineData("Uppercase")]
	[InlineData("_underscore")]
	[InlineData("-leading-dash")]
	[InlineData("a")]
	[InlineData("has space")]
	[InlineData("dot.in.segment")]
	public void ParseDb_RejectsInvalidSegments(string bad) =>
		FluentActions.Invoking(() => NodePath.ParseDb(bad))
			.Should().Throw<ArgumentException>();

	[Fact]
	public void ParseDb_AllowsSystemPrefix() =>
		NodePath.ParseDb("$system/yobaconf").ToDbPath().Should().Be("$system/yobaconf");

	[Fact]
	public void Parent_WalksUpOneSegment() =>
		NodePath.ParseDb("a1/b2/c3").Parent.Should().Be(NodePath.ParseDb("a1/b2"));

	[Fact]
	public void Parent_OfTopLevel_IsRoot() =>
		NodePath.ParseDb("a1").Parent.Should().Be(NodePath.Root);

	[Fact]
	public void Parent_OfRoot_IsNull() =>
		NodePath.Root.Parent.Should().BeNull();

	[Fact]
	public void Root_IsEqualToDefault() =>
		default(NodePath).Should().Be(NodePath.Root);

	[Fact]
	public void Equality_IsValueBased() =>
		NodePath.ParseDb("a1/b2").Should().Be(NodePath.ParseDb("a1/b2"));
}
