using YobaConf.Tests.Fakes;

namespace YobaConf.Tests;

public class FallthroughTests
{
	[Fact]
	public void FindBestMatch_ExactHit_ReturnsNode()
	{
		var store = InMemoryConfigStore.With(
			("app", "a = 1"),
			("app/dev", "a = 2"));

		var hit = NodeResolver.FindBestMatch(store, NodePath.ParseDb("app/dev"));

		hit!.Path.Should().Be(NodePath.ParseDb("app/dev"));
	}

	[Fact]
	public void FindBestMatch_MissingLeaf_FallsThroughToNearestAncestor()
	{
		var store = InMemoryConfigStore.With(
			("app", "a = 1"),
			("app/dev", "a = 2"));

		var hit = NodeResolver.FindBestMatch(store, NodePath.ParseDb("app/dev/feature"));

		hit!.Path.Should().Be(NodePath.ParseDb("app/dev"));
	}

	[Fact]
	public void FindBestMatch_MissingWholeSubtree_FallsBackToRootAncestor()
	{
		var store = InMemoryConfigStore.With(("app", "a = 1"));

		var hit = NodeResolver.FindBestMatch(store, NodePath.ParseDb("app/dev/feature"));

		hit!.Path.Should().Be(NodePath.ParseDb("app"));
	}

	[Fact]
	public void FindBestMatch_NoMatchingAncestor_ReturnsNull()
	{
		var store = InMemoryConfigStore.With(("other", "x = 1"));

		var hit = NodeResolver.FindBestMatch(store, NodePath.ParseDb("app/dev"));

		hit.Should().BeNull();
	}

	[Fact]
	public void CollectAncestorChain_ReturnsExistingNodesInRootToLeafOrder()
	{
		var store = InMemoryConfigStore.With(
			("app", "a = 1"),
			("app/dev", "a = 2"));

		var chain = NodeResolver.CollectAncestorChain(store, NodePath.ParseDb("app/dev"));

		chain.Select(n => n.Path.ToDbPath()).Should().Equal("app", "app/dev");
	}

	[Fact]
	public void CollectAncestorChain_SkipsMissingMiddleNodes()
	{
		// `app/dev` is missing; chain contains only what exists.
		var store = InMemoryConfigStore.With(
			("app", "a = 1"),
			("app/dev/feature", "a = 3"));

		var chain = NodeResolver.CollectAncestorChain(store, NodePath.ParseDb("app/dev/feature"));

		chain.Select(n => n.Path.ToDbPath()).Should().Equal("app", "app/dev/feature");
	}
}
