namespace YobaConf.E2ETests;

// Phase B.6 — /History audit-log page + single-entry rollback. Exercises the full
// user-visible round-trip: seed state → mutate via admin API → open /History → click
// Rollback → verify store is reverted.
[Collection(nameof(UiCollection))]
public sealed class HistoryTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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
		NodePath.ParseDb("b6-" + stem + "-" + Guid.NewGuid().ToString("N")[..6]);

	[Fact]
	public async Task History_Shows_Timeline_Entries_For_Path()
	{
		var path = FreshPath("timeline");
		app.ConfigStoreAdmin.UpsertNode(path, "x = 1", DateTimeOffset.UtcNow, actor: "alice");
		app.ConfigStoreAdmin.UpsertNode(path, "x = 2", DateTimeOffset.UtcNow, actor: "bob");
		app.ConfigStoreAdmin.UpsertVariable(path, "db_host", "prod", DateTimeOffset.UtcNow, actor: "carol");

		await _page!.GotoAsync($"/History?path={path.ToUrlPath()}");

		var entries = _page.GetByTestId("history-entry");
		await Expect(entries).ToHaveCountAsync(3);
		await Expect(_page.GetByTestId("history-timeline")).ToContainTextAsync("alice");
		await Expect(_page.GetByTestId("history-timeline")).ToContainTextAsync("bob");
		await Expect(_page.GetByTestId("history-timeline")).ToContainTextAsync("carol");
	}

	[Fact]
	public async Task History_Origin_Entry_Shows_Cannot_Rollback_Hint()
	{
		var path = FreshPath("origin");
		app.ConfigStoreAdmin.UpsertNode(path, "x = 1", DateTimeOffset.UtcNow);

		await _page!.GotoAsync($"/History?path={path.ToUrlPath()}");
		await Expect(_page.GetByTestId("history-timeline")).ToContainTextAsync("cannot roll back");
	}

	[Fact]
	public async Task Rollback_NodeUpdate_RestoresPriorContent()
	{
		var path = FreshPath("rb-node");
		app.ConfigStoreAdmin.UpsertNode(path, "version = 1", DateTimeOffset.UtcNow, actor: "alice");
		app.ConfigStoreAdmin.UpsertNode(path, "version = 2", DateTimeOffset.UtcNow, actor: "bob");

		await _page!.GotoAsync($"/History?path={path.ToUrlPath()}");
		// Newest-first — first entry is bob's update; rollback that.
		await _page.GetByTestId("history-rollback").First.ClickAsync();

		await Expect(_page.GetByTestId("history-success")).ToBeVisibleAsync();
		var stored = ((IConfigStore)app.ConfigStoreAdmin).FindNode(path);
		stored!.RawContent.Should().Be("version = 1");

		// The rollback itself appends a new audit entry with actor `restore:*`.
		var audit = ((IAuditLogStore)app.ConfigStoreAdmin).FindByPath(path, includeDescendants: false, skip: 0, take: 50);
		audit.Should().Contain(e => e.Actor.StartsWith("restore:", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Rollback_VariableUpdate_RestoresPriorValue()
	{
		var path = FreshPath("rb-var");
		app.ConfigStoreAdmin.UpsertNode(path, "x = 1", DateTimeOffset.UtcNow);
		app.ConfigStoreAdmin.UpsertVariable(path, "db_host", "old", DateTimeOffset.UtcNow);
		app.ConfigStoreAdmin.UpsertVariable(path, "db_host", "new", DateTimeOffset.UtcNow);

		await _page!.GotoAsync($"/History?path={path.ToUrlPath()}");
		await _page.GetByTestId("history-rollback").First.ClickAsync();

		await Expect(_page.GetByTestId("history-success")).ToBeVisibleAsync();
		var v = ((IConfigStore)app.ConfigStoreAdmin).FindVariables(path).Single(x => x.Key == "db_host" && !x.IsDeleted);
		v.Value.Should().Be("old");
	}

	[Fact]
	public async Task Rollback_VariableDelete_RevivesVariable()
	{
		var path = FreshPath("rb-undel");
		app.ConfigStoreAdmin.UpsertNode(path, "x = 1", DateTimeOffset.UtcNow);
		app.ConfigStoreAdmin.UpsertVariable(path, "flag", "was-here", DateTimeOffset.UtcNow);
		app.ConfigStoreAdmin.SoftDeleteVariable(path, "flag");

		await _page!.GotoAsync($"/History?path={path.ToUrlPath()}");
		// Newest = Deleted; rollback revives.
		await _page.GetByTestId("history-rollback").First.ClickAsync();

		await Expect(_page.GetByTestId("history-success")).ToBeVisibleAsync();
		var v = ((IConfigStore)app.ConfigStoreAdmin).FindVariables(path).Single(x => x.Key == "flag" && !x.IsDeleted);
		v.Value.Should().Be("was-here");
	}
}
