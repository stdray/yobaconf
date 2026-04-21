namespace YobaConf.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class ImportTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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
	public async Task JsonPaste_Convert_Shows_Preview()
	{
		var import = new ImportPage(_page!);
		await import.GotoAsync();

		await import.FillAsync(
			path: "import-tests.preview",
			format: "json",
			source: "{\"name\": \"yoba\", \"port\": 8080}");
		await import.ClickConvertAsync();

		// Preview-only mode: no node gets saved; the converted HOCON appears in the
		// preview pane.
		await Expect(import.Preview).ToContainTextAsync("name");
		await Expect(import.Preview).ToContainTextAsync("port");
	}

	[Fact]
	public async Task JsonPaste_Save_Persists_Node()
	{
		var import = new ImportPage(_page!);
		await import.GotoAsync();

		const string targetPath = "import-tests.persisted";
		await import.FillAsync(
			path: targetPath,
			format: "json",
			source: "{\"greeting\": \"hello-from-import\"}");
		await import.ClickSaveAsync();

		await Expect(import.SuccessAlert).ToBeVisibleAsync();

		// Confirm via Node page: navigating shows the imported content.
		var node = new NodePage(_page!);
		await node.GotoAsync(targetPath);
		await Expect(node.ResolvedJson).ToContainTextAsync("hello-from-import");
	}
}
