using YobaConf.E2ETests.Infrastructure;

namespace YobaConf.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class TagVocabularyTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	IBrowserContext? _ctx;
	IPage? _page;

	public async Task InitializeAsync()
	{
		// Per-test cleanup: vocabulary is append-only in the shared fixture DB, so other
		// tests' leftovers would tint the "empty → non-empty warning" transitions. Wipe live
		// rows before each test.
		foreach (var entry in app.VocabularyStore.ListActive())
			app.VocabularyAdmin.SoftDelete(entry.Id, DateTimeOffset.UtcNow);

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
	public async Task Create_And_Delete_Tag_Entry_Roundtrips_Through_UI()
	{
		var key = "env-" + Guid.NewGuid().ToString("N")[..6];

		await _page!.GotoAsync("/Tags");
		await _page.GetByTestId("tags-create-key").FillAsync(key);
		await _page.GetByTestId("tags-create-value").FillAsync("prod");
		await _page.GetByTestId("tags-create-description").FillAsync("deployment env");
		await _page.GetByTestId("tags-create-submit").ClickAsync();

		await Expect(_page.GetByTestId("tags-success")).ToBeVisibleAsync();
		var group = _page.Locator($"[data-tag-key='{key}']");
		await Expect(group).ToBeVisibleAsync();
		await Expect(group.GetByTestId("tags-row-value")).ToHaveTextAsync("prod");

		// Delete via confirm-dialog accept.
		_page.Dialog += (_, dlg) => _ = dlg.AcceptAsync();
		await group.GetByTestId("tags-delete").ClickAsync();

		await Expect(_page.Locator($"[data-tag-key='{key}']")).Not.ToBeVisibleAsync();
		app.VocabularyStore.ListActive().Should().NotContain(e => e.Key == key);
	}

	[Fact]
	public async Task Bindings_Editor_Warns_On_Unknown_Tag_Key_When_Vocabulary_Is_Non_Empty()
	{
		// Seed a single declaration so vocabulary becomes non-empty + "env" is the only
		// known key.
		app.VocabularyAdmin.Create("env", value: null, description: null, priority: 0, at: DateTimeOffset.UtcNow);

		var keyPath = "k-" + Guid.NewGuid().ToString("N")[..6];
		await _page!.GotoAsync("/Bindings/Edit");
		// Tag with an unknown key — editor should surface the warning post-save.
		await _page.GetByTestId("edit-tags").FillAsync("env=prod\nprojact=yobapub");
		await _page.GetByTestId("edit-key").FillAsync(keyPath);
		await _page.GetByTestId("edit-value").FillAsync("\"v\"");
		await _page.GetByTestId("edit-save").ClickAsync();

		var warning = _page.GetByTestId("edit-unknown-tags");
		await Expect(warning).ToBeVisibleAsync();
		await Expect(warning).ToContainTextAsync("projact");
		// "env" is declared → must not appear in the warning payload.
		await Expect(warning).Not.ToContainTextAsync("env,");

		// Binding did save (warning is advisory, not blocking).
		app.BindingStore.ListActive().Should().Contain(b => b.KeyPath == keyPath);
	}
}
