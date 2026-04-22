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
	public void ParseUrl_DotInSegment_ErrorMessage_MentionsSeparator()
	{
		// Filename-with-extension ("logger.hocon") is a common user mistake — they expect
		// node paths to model folders+files. The error should point them at the cause.
		var act = () => NodePath.ParseUrl("xxx.logger.hocon");
		// ParseUrl splits on dots so the offending raw segment here is 'hocon' — which is
		// actually a valid slug. Test via ParseDb where the dot is in-segment:
		FluentActions.Invoking(() => NodePath.ParseDb("xxx/logger.hocon"))
			.Should().Throw<ArgumentException>()
			.WithMessage("*cannot contain '.'*separator*");
	}

	[Fact]
	public void ParseDb_Uppercase_ErrorMessage_MentionsLowercase() =>
		FluentActions.Invoking(() => NodePath.ParseDb("xxx/LoggerConfig"))
			.Should().Throw<ArgumentException>()
			.WithMessage("*lowercase*");

	[Fact]
	public void ParseDb_Underscore_ErrorMessage_PointsToDash() =>
		FluentActions.Invoking(() => NodePath.ParseDb("xxx/logger_config"))
			.Should().Throw<ArgumentException>()
			.WithMessage("*cannot contain '_'*use '-'*");

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

	[Fact]
	public void IsAncestorOf_ProperPrefixWithSlashBoundary_IsTrue() =>
		NodePath.ParseDb("yobapub").IsAncestorOf(NodePath.ParseDb("yobapub/test")).Should().BeTrue();

	[Fact]
	public void IsAncestorOf_DeepDescendant_IsTrue() =>
		NodePath.ParseDb("yobapub").IsAncestorOf(NodePath.ParseDb("yobapub/test/feature")).Should().BeTrue();

	[Fact]
	public void IsAncestorOf_Self_IsFalse() =>
		NodePath.ParseDb("yobapub/test").IsAncestorOf(NodePath.ParseDb("yobapub/test")).Should().BeFalse();

	[Fact]
	public void IsAncestorOf_Sibling_IsFalse() =>
		NodePath.ParseDb("yobapub/test").IsAncestorOf(NodePath.ParseDb("yobapub/dev")).Should().BeFalse();

	// Prevents the "yobaproj/yobaapp" key from granting access to "yobaproj/yobaapplication" — spec §8.
	[Fact]
	public void IsAncestorOf_PrefixWithoutSegmentBoundary_IsFalse() =>
		NodePath.ParseDb("yobaproj/yobaapp").IsAncestorOf(NodePath.ParseDb("yobaproj/yobaapplication")).Should().BeFalse();

	[Fact]
	public void IsAncestorOf_Ancestor_IsFalseOnReverseDirection() =>
		NodePath.ParseDb("yobapub/test").IsAncestorOf(NodePath.ParseDb("yobapub")).Should().BeFalse();

	[Fact]
	public void Root_IsAncestorOfEveryNonRootPath() =>
		NodePath.Root.IsAncestorOf(NodePath.ParseDb("anything")).Should().BeTrue();

	[Fact]
	public void Root_IsNotAncestorOfItself() =>
		NodePath.Root.IsAncestorOf(NodePath.Root).Should().BeFalse();

	[Fact]
	public void IsDescendantOf_IsInverseOfIsAncestorOf() =>
		NodePath.ParseDb("yobapub/test").IsDescendantOf(NodePath.ParseDb("yobapub")).Should().BeTrue();
}
