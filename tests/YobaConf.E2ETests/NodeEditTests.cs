namespace YobaConf.E2ETests;

// Phase B.4 — Edit mode + optimistic locking via ContentHash. Textarea-based editor
// (CodeMirror 6 upgrade is a documented follow-up in plan.md). Conflict detection uses
// the same expectedHash contract exercised in AuditLogTests at storage level.
[Collection(nameof(UiCollection))]
public sealed class NodeEditTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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

	static NodePath FreshPath(string stem) =>
		NodePath.ParseDb("b4-" + stem + "-" + Guid.NewGuid().ToString("N")[..6]);

	[Fact]
	public async Task Edit_Save_HappyPath_Persists_NewContent()
	{
		var path = FreshPath("edit");
		app.ConfigStoreAdmin.UpsertNode(path, "x = 1", DateTimeOffset.UtcNow);

		await _page!.GotoAsync($"/Node?path={path.ToUrlPath()}");
		await _page.GetByTestId("node-edit").ClickAsync();
		await _page.GetByTestId("node-edit-textarea").FillAsync("x = 2\ny = 3");
		await _page.GetByTestId("node-edit-save").ClickAsync();

		await Expect(_page.GetByTestId("node-success")).ToBeVisibleAsync();
		// Raw content pane (non-edit mode) reflects the new content.
		await Expect(_page.GetByTestId("node-raw")).ToContainTextAsync("x = 2");
		await Expect(_page.GetByTestId("node-raw")).ToContainTextAsync("y = 3");

		var stored = ((IConfigStore)app.ConfigStoreAdmin).FindNode(path);
		// Form-encoded POST normalizes newlines to CRLF; compare with LF-normalized value.
		stored!.RawContent.ReplaceLineEndings("\n").Should().Be("x = 2\ny = 3");
	}

	[Fact]
	public async Task Edit_Cancel_LeavesContent_Unchanged()
	{
		var path = FreshPath("cancel");
		app.ConfigStoreAdmin.UpsertNode(path, "original = true", DateTimeOffset.UtcNow);

		await _page!.GotoAsync($"/Node?path={path.ToUrlPath()}");
		await _page.GetByTestId("node-edit").ClickAsync();
		await _page.GetByTestId("node-edit-textarea").FillAsync("abandoned-edit");
		await _page.GetByTestId("node-edit-cancel").ClickAsync();

		await Expect(_page.GetByTestId("node-raw")).ToContainTextAsync("original = true");
		((IConfigStore)app.ConfigStoreAdmin).FindNode(path)!.RawContent.Should().Be("original = true");
	}

	[Fact]
	public async Task Edit_ConflictDetected_When_OtherSession_SavedFirst()
	{
		var path = FreshPath("conflict");
		app.ConfigStoreAdmin.UpsertNode(path, "starting = 1", DateTimeOffset.UtcNow);

		await _page!.GotoAsync($"/Node?path={path.ToUrlPath()}");
		await _page.GetByTestId("node-edit").ClickAsync();
		await _page.GetByTestId("node-edit-textarea").FillAsync("my-edit = 2");

		// Simulate another session saving before we submit.
		app.ConfigStoreAdmin.UpsertNode(path, "other-session = 99", DateTimeOffset.UtcNow, actor: "another");

		await _page.GetByTestId("node-edit-save").ClickAsync();

		// Conflict alert visible; edit mode still active; in-flight text preserved.
		await Expect(_page.GetByTestId("node-conflict")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("node-edit-textarea")).ToHaveValueAsync("my-edit = 2");

		// Store was NOT overwritten by our save — other-session's value is still there.
		((IConfigStore)app.ConfigStoreAdmin).FindNode(path)!.RawContent.Should().Be("other-session = 99");
	}
}
