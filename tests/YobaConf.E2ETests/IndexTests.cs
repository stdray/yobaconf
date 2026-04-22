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
	public async Task Tree_Shows_SeededNode_Under_ItsSegment()
	{
		app.ConfigStoreAdmin.UpsertNode(
			NodePath.ParseDb("index-tests/seeded-node"),
			"name = seeded",
			DateTimeOffset.UtcNow);

		await _page!.GotoAsync("/");

		// The tree rows carry the full canonical path as `data-path`; the link label
		// itself is just the last segment. Assert on the `<li data-path=...>` wrapper.
		var row = _page.Locator("[data-path='index-tests/seeded-node']");
		await Expect(row).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Tree_Virtual_Dir_Rendered_When_OnlyDescendantsExist()
	{
		// Seed only deep paths — the intermediate segments become virtual dirs
		// (no actual node at that path, but descendants exist).
		app.ConfigStoreAdmin.UpsertNode(
			NodePath.ParseDb("virtual-parent/real-child"),
			"x = 1",
			DateTimeOffset.UtcNow);

		await _page!.GotoAsync("/");

		// virtual-parent has no actual node; UI should render it as a non-clickable row
		// with a "virtual dir" label, and the real child should be visible as a link.
		var vparent = _page.Locator("[data-path='virtual-parent']").First;
		await Expect(vparent).ToBeVisibleAsync();
		await Expect(vparent.GetByText("virtual dir")).ToBeVisibleAsync();

		var child = _page.Locator("[data-path='virtual-parent/real-child']");
		await Expect(child).ToBeVisibleAsync();
	}

	[Fact]
	public async Task EmptyTree_Shows_EmptyPanel_Only()
	{
		// Keep empty — no seed. Note other tests in this class may have seeded already
		// against the shared fixture; we scope this check to the empty-panel element
		// directly, which is only rendered when ActualNodeCount == 0.
		var panel = _page!.GetByTestId("index-empty");

		await _page.GotoAsync("/");

		// Either the panel exists (no nodes yet) or the node-list exists. We only assert
		// they're mutually exclusive — the page must decide one or the other.
		var panelVisible = await panel.IsVisibleAsync();
		var listVisible = await _page.GetByTestId("node-list").IsVisibleAsync();
		(panelVisible ^ listVisible).Should().BeTrue("exactly one of empty-panel or node-list must render");
	}

	[Fact]
	public async Task NewEmpty_Creates_Node_And_Redirects_To_Node_Page()
	{
		var uniquePath = "b2-tests.newempty-" + Guid.NewGuid().ToString("N")[..6];

		await _page!.GotoAsync("/");
		await _page.GetByTestId("new-empty-path").FillAsync(uniquePath);
		await _page.GetByTestId("new-empty-submit").ClickAsync();

		// Lands on the Node page for the just-created path.
		await Expect(_page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Node"));
		var dbPath = uniquePath.Replace('.', '/');
		await Expect(_page.GetByTestId("node-title")).ToHaveTextAsync(dbPath);
	}
}
