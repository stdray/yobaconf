namespace YobaConf.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class NodeTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	IBrowserContext? _ctx;
	IPage? _page;

	public async Task InitializeAsync()
	{
		_ctx = await app.NewContextAsync();
		_page = await _ctx.NewPageAsync();
	}

	public async Task DisposeAsync()
	{
		if (_ctx is not null)
		{
			await TraceArtifact.StopAndSaveAsync(_ctx, output);
			await _ctx.CloseAsync();
		}
	}

	[Fact]
	public async Task ExistingNode_Displays_Raw_And_ResolvedJson_With_ETag()
	{
		app.ConfigStoreAdmin.UpsertNode(
			NodePath.ParseDb("node-tests/exact-hit"),
			"name = yoba\nport = 8080",
			DateTimeOffset.UtcNow);

		var node = new NodePage(_page!);
		await node.GotoAsync("node-tests.exact-hit");

		await Expect(node.Title).ToHaveTextAsync("node-tests/exact-hit");
		// Raw HOCON pre-block preserves the upserted content.
		await Expect(node.RawContent).ToContainTextAsync("name = yoba");
		await Expect(node.RawContent).ToContainTextAsync("port = 8080");
		// Resolved JSON exists + ETag badge renders.
		await Expect(node.ResolvedJson).ToContainTextAsync("\"yoba\"");
		await Expect(node.ETag).ToBeVisibleAsync();
		await Expect(node.FallthroughNotice).Not.ToBeVisibleAsync();
	}

	[Fact]
	public async Task MissingNode_With_Ancestor_Shows_Fallthrough_Notice()
	{
		app.ConfigStoreAdmin.UpsertNode(
			NodePath.ParseDb("node-tests-fall/parent"),
			"fallback = true",
			DateTimeOffset.UtcNow);

		var node = new NodePage(_page!);
		// Descendant that doesn't exist — pipeline resolves to `node-tests-fall/parent`.
		await node.GotoAsync("node-tests-fall.parent.missing");

		await Expect(node.FallthroughNotice).ToBeVisibleAsync();
		await Expect(node.FallthroughTarget).ToHaveTextAsync("node-tests-fall/parent");
		// Resolved JSON still produced (from ancestor).
		await Expect(node.ResolvedJson).ToContainTextAsync("\"fallback\"");
	}
}
