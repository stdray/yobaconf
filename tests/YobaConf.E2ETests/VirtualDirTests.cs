namespace YobaConf.E2ETests;

// Fixes from user feedback:
// 1) Virtual-dir rows in the tree are now clickable and lead to /Node page for that
//    intermediate path; user can create an empty node there.
// 2) /Node page in fallthrough state (no node at exact path) surfaces a
//    "Create empty node here" button.
// 3) Invalid-path error messages give concrete hints about the offending character
//    (dot inside segment / uppercase / underscore) — asserted at unit level in
//    NodePathTests follow-up.
[Collection(nameof(UiCollection))]
public sealed class VirtualDirTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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

	static string FreshSegment() => "vd-" + Guid.NewGuid().ToString("N")[..6];

	[Fact]
	public async Task VirtualDir_IsClickable_And_Leads_To_Node_With_Create_Button()
	{
		var parent = FreshSegment();
		var child = "child-" + Guid.NewGuid().ToString("N")[..4];
		app.ConfigStoreAdmin.UpsertNode(NodePath.ParseDb($"{parent}/{child}"), "x = 1", DateTimeOffset.UtcNow);

		await _page!.GotoAsync("/");

		// Click the virtual-dir row — it's the one with data-path="{parent}" (no children
		// rendered inside its `a` tag); `.First` picks the link, not the nested child row.
		var virtualRow = _page.Locator($"[data-path='{parent}'] > a").First;
		await Expect(virtualRow).ToBeVisibleAsync();
		await Expect(virtualRow).ToContainTextAsync("virtual dir");
		await virtualRow.ClickAsync();

		// Landed on /Node — fallthrough notice with Create button.
		await Expect(_page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Node"));
		await Expect(_page.GetByTestId("node-fallthrough-notice")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("node-create-empty")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task CreateEmpty_From_Fallthrough_Persists_And_Redirects()
	{
		var parent = FreshSegment();
		var child = "child-" + Guid.NewGuid().ToString("N")[..4];
		app.ConfigStoreAdmin.UpsertNode(NodePath.ParseDb($"{parent}/{child}"), "x = 1", DateTimeOffset.UtcNow);

		await _page!.GotoAsync($"/Node?path={parent}");
		await _page.GetByTestId("node-create-empty").ClickAsync();

		// After create: fallthrough-notice gone, node-raw visible (empty content).
		await Expect(_page.GetByTestId("node-fallthrough-notice")).Not.ToBeVisibleAsync();
		await Expect(_page.GetByTestId("node-success")).ToBeVisibleAsync();
		((IConfigStore)app.ConfigStoreAdmin).FindNode(NodePath.ParseDb(parent)).Should().NotBeNull();
	}
}
