using YobaConf.Core.Include;
using YobaConf.Tests.Fakes;

namespace YobaConf.Tests.Include;

public class IncludePreprocessorTests
{
	[Fact]
	public void NoIncludes_ReturnsRawContentUnchanged()
	{
		var store = InMemoryConfigStore.With(
			("app", "a = 1\nb = \"hello\""));

		var result = IncludePreprocessor.Resolve(NodePath.ParseDb("app"), store);

		result.Should().Be("a = 1\nb = \"hello\"");
	}

	[Fact]
	public void AncestorInclude_IsExpandedInline()
	{
		var store = InMemoryConfigStore.With(
			("logger-base", "log-level = info"),
			("project-a/service1", "include \"logger-base\"\nname = s1"));

		var result = IncludePreprocessor.Resolve(NodePath.ParseDb("project-a/service1"), store);

		result.Should().Be("log-level = info\nname = s1");
	}

	[Fact]
	public void SiblingInclude_InSameDirectory_IsAllowed()
	{
		// service1 and service2 both live under project-a/test — sibling include permitted
		// because dir(target) == dir(including) (spec §1).
		var store = InMemoryConfigStore.With(
			("project-a/test/service2", "port = 8080"),
			("project-a/test/service1", "include \"project-a/test/service2\"\nname = s1"));

		var result = IncludePreprocessor.Resolve(NodePath.ParseDb("project-a/test/service1"), store);

		result.Should().Be("port = 8080\nname = s1");
	}

	[Fact]
	public void NestedIncludes_AreFullyFlattened()
	{
		// service1 -> base (ancestor) -> logger-base (root-level ancestor)
		var store = InMemoryConfigStore.With(
			("logger-base", "log-level = info"),
			("project-a/base", "include \"logger-base\"\nshared = yes"),
			("project-a/service1", "include \"project-a/base\"\nname = s1"));

		var result = IncludePreprocessor.Resolve(NodePath.ParseDb("project-a/service1"), store);

		result.Should().Be("log-level = info\nshared = yes\nname = s1");
	}

	[Fact]
	public void SameTargetIncludedViaTwoPaths_AppearsTwiceInOutput()
	{
		// HOCON's later-wins merge handles duplicates; preprocessor doesn't dedupe.
		var store = InMemoryConfigStore.With(
			("shared", "x = 1"),
			("project-a/base", "include \"shared\""),
			("project-a/service1", "include \"shared\"\ninclude \"project-a/base\"\nname = s1"));

		var result = IncludePreprocessor.Resolve(NodePath.ParseDb("project-a/service1"), store);

		result.Should().Be("x = 1\nx = 1\nname = s1");
	}

	[Fact]
	public void MutualSiblingInclude_ThrowsCyclicIncludeException()
	{
		// Cycle: service1 -> service2 -> service1
		var store = InMemoryConfigStore.With(
			("project-a/test/service1", "include \"project-a/test/service2\""),
			("project-a/test/service2", "include \"project-a/test/service1\""));

		var act = () => IncludePreprocessor.Resolve(NodePath.ParseDb("project-a/test/service1"), store);

		var ex = act.Should().Throw<CyclicIncludeException>().Which;
		ex.Chain.Select(p => p.ToDbPath()).Should().Equal(
			"project-a/test/service1",
			"project-a/test/service2",
			"project-a/test/service1");
	}

	[Fact]
	public void ThreeNodeCycle_IsDetected()
	{
		var store = InMemoryConfigStore.With(
			("project-a/alpha", "include \"project-a/beta\""),
			("project-a/beta", "include \"project-a/gamma\""),
			("project-a/gamma", "include \"project-a/alpha\""));

		var act = () => IncludePreprocessor.Resolve(NodePath.ParseDb("project-a/alpha"), store);

		act.Should().Throw<CyclicIncludeException>()
			.Which.Chain.Should().HaveCount(4); // alpha -> beta -> gamma -> alpha
	}

	[Fact]
	public void SelfInclude_IsScopeViolation()
	{
		var store = InMemoryConfigStore.With(
			("app", "include \"app\""));

		var act = () => IncludePreprocessor.Resolve(NodePath.ParseDb("app"), store);

		act.Should().Throw<IncludeScopeViolationException>()
			.WithMessage("*self-include*");
	}

	[Fact]
	public void DescendantInclude_IsScopeViolation()
	{
		// parent including child — forbidden: target's dir is descendant, not ancestor-or-equal
		var store = InMemoryConfigStore.With(
			("project-a/child", "x = 1"),
			("project-a", "include \"project-a/child\""));

		var act = () => IncludePreprocessor.Resolve(NodePath.ParseDb("project-a"), store);

		act.Should().Throw<IncludeScopeViolationException>();
	}

	[Fact]
	public void SiblingSubtreeInclude_IsScopeViolation()
	{
		// project-a/test/service1 tries to include project-a/dev/something — dev/ is not
		// ancestor-or-equal of test/
		var store = InMemoryConfigStore.With(
			("project-a/dev/thing", "x = 1"),
			("project-a/test/service1", "include \"project-a/dev/thing\""));

		var act = () => IncludePreprocessor.Resolve(NodePath.ParseDb("project-a/test/service1"), store);

		act.Should().Throw<IncludeScopeViolationException>();
	}

	[Fact]
	public void TargetNotFound_Throws()
	{
		var store = InMemoryConfigStore.With(
			("app", "include \"nonexistent\""));

		var act = () => IncludePreprocessor.Resolve(NodePath.ParseDb("app"), store);

		act.Should().Throw<IncludeTargetNotFoundException>()
			.Which.Target.Should().Be(NodePath.ParseDb("nonexistent"));
	}

	[Fact]
	public void RootPathNotFound_Throws()
	{
		var store = InMemoryConfigStore.With(("other", "x = 1"));

		var act = () => IncludePreprocessor.Resolve(NodePath.ParseDb("app"), store);

		act.Should().Throw<IncludeTargetNotFoundException>();
	}

	[Theory]
	[InlineData("include file(\"x\")")]
	[InlineData("include classpath(\"x\")")]
	[InlineData("include url(\"http://example.com/x\")")]
	[InlineData("include required(\"x\")")]
	public void UnsupportedHoconForms_Throw(string directive)
	{
		var store = InMemoryConfigStore.With(
			("app", directive));

		var act = () => IncludePreprocessor.Resolve(NodePath.ParseDb("app"), store);

		act.Should().Throw<UnsupportedIncludeSyntaxException>();
	}

	[Fact]
	public void RelativePath_IsUnsupported()
	{
		var store = InMemoryConfigStore.With(
			("project-a/test/service1", "include \"../service2\""));

		var act = () => IncludePreprocessor.Resolve(NodePath.ParseDb("project-a/test/service1"), store);

		act.Should().Throw<UnsupportedIncludeSyntaxException>()
			.WithMessage("*relative*");
	}

	[Fact]
	public void IncludeWithTrailingComment_IsParsed()
	{
		var store = InMemoryConfigStore.With(
			("shared", "x = 1"),
			("app", "include \"shared\"  # pulls in the shared block"));

		var result = IncludePreprocessor.Resolve(NodePath.ParseDb("app"), store);

		result.Should().Be("x = 1");
	}

	[Fact]
	public void IncludeDirectiveCaseSensitive_LowercaseOnly()
	{
		// HOCON spec uses lowercase `include`. Uppercase `INCLUDE` is NOT a directive.
		var store = InMemoryConfigStore.With(
			("app", "INCLUDE \"shared\"\nname = x"));

		var result = IncludePreprocessor.Resolve(NodePath.ParseDb("app"), store);

		result.Should().Be("INCLUDE \"shared\"\nname = x"); // unchanged
	}
}
