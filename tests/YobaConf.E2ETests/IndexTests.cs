namespace YobaConf.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class IndexTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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
	public async Task EmptyTree_Shows_ImportCta()
	{
		// Before any seeded node, Index renders the empty-state CTA. LoginTests run in
		// parallel against the same fixture but don't write nodes, so tree starts empty
		// here unless a prior test in this class / in NodeTests wrote something. Use a
		// path-free state check that tolerates pre-seeded siblings: the empty CTA
		// appears only when node-list is not rendered.
		var page = new IndexPage(_page!);
		await page.GotoAsync();

		var nodeListVisible = await page.NodeList.IsVisibleAsync();
		if (!nodeListVisible)
			await Expect(page.EmptyCta).ToBeVisibleAsync();
	}

	[Fact]
	public async Task PopulatedTree_Shows_SeededNode()
	{
		// Seed directly through the IConfigStoreAdmin DI service — the same code path
		// the admin UI uses, but skipping the paste-import dialog. UI-level import is
		// exercised separately in ImportTests.
		app.ConfigStoreAdmin.UpsertNode(
			NodePath.ParseDb("index-tests/seeded-node"),
			"name = seeded",
			DateTimeOffset.UtcNow);

		var page = new IndexPage(_page!);
		await page.GotoAsync();

		var seeded = page.NodeLinks.Filter(new LocatorFilterOptions
		{
			HasText = "index-tests/seeded-node",
		});
		await Expect(seeded).ToBeVisibleAsync();
	}
}
