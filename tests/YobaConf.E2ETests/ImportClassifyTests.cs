namespace YobaConf.E2ETests;

// Phase B.7 — /Import classify step. Flat source → per-leaf Keep/Variable/Secret
// classification → Save applies the split (Node RawContent keeps "keep" leaves,
// Variables + Secrets tables get the extracted ones).
[Collection(nameof(UiCollection))]
public sealed class ImportClassifyTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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

	static string FreshUrlPath(string stem) =>
		"b7-" + stem + "-" + Guid.NewGuid().ToString("N")[..6];

	[Fact]
	public async Task Flat_Env_Input_Offers_Classify_Step()
	{
		var target = FreshUrlPath("classify-offer");
		await _page!.GotoAsync("/Import");
		await _page.GetByTestId("import-path").FillAsync(target);
		await _page.GetByTestId("import-format").SelectOptionAsync("env");
		await _page.GetByTestId("import-source").FillAsync("DB_HOST=prod-db\nDB_PASSWORD=hunter2\nMAX_CONN=200");
		await _page.GetByTestId("import-convert").ClickAsync();

		await Expect(_page.GetByTestId("import-classify")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Classify_Keep_All_Preserves_HOCON_AsSingleNode()
	{
		var target = FreshUrlPath("keep-all");
		await _page!.GotoAsync("/Import");
		await _page.GetByTestId("import-path").FillAsync(target);
		await _page.GetByTestId("import-format").SelectOptionAsync("env");
		await _page.GetByTestId("import-source").FillAsync("FOO=bar\nBAZ=qux");
		await _page.GetByTestId("import-classify").ClickAsync();

		// All rows default to Keep — just submit.
		await _page.GetByTestId("classify-save").ClickAsync();

		await Expect(_page.GetByTestId("import-success")).ToBeVisibleAsync();
		var stored = ((IConfigStore)app.ConfigStoreAdmin).FindNode(NodePath.ParseUrl(target));
		stored.Should().NotBeNull();
		// Dotenv converter emits quoted-key quoted-value pairs; keep-only save preserves them verbatim.
		stored!.RawContent.Should().Contain("FOO");
		stored.RawContent.Should().Contain("bar");
		((IConfigStore)app.ConfigStoreAdmin).FindVariables(NodePath.ParseUrl(target)).Should().BeEmpty();
	}

	[Fact]
	public async Task Classify_Mixed_Splits_Into_Node_Variable_Secret()
	{
		var target = FreshUrlPath("mixed");
		await _page!.GotoAsync("/Import");
		await _page.GetByTestId("import-path").FillAsync(target);
		await _page.GetByTestId("import-format").SelectOptionAsync("env");
		await _page.GetByTestId("import-source").FillAsync("FEATURE_FLAG=enabled\nDB_HOST=prod-db\nAPI_TOKEN=sk_live_abc");
		await _page.GetByTestId("import-classify").ClickAsync();

		// FEATURE_FLAG → Keep (default)
		await _page.Locator("[data-classify-key='DB_HOST']").GetByTestId("classify-variable").ClickAsync();
		await _page.Locator("[data-classify-key='API_TOKEN']").GetByTestId("classify-secret").ClickAsync();
		await _page.GetByTestId("classify-save").ClickAsync();

		await Expect(_page.GetByTestId("import-success")).ToBeVisibleAsync();

		var path = NodePath.ParseUrl(target);
		var node = ((IConfigStore)app.ConfigStoreAdmin).FindNode(path);
		node.Should().NotBeNull();
		node!.RawContent.Should().Contain("FEATURE_FLAG");
		node.RawContent.Should().NotContain("DB_HOST");
		node.RawContent.Should().NotContain("API_TOKEN");

		var v = ((IConfigStore)app.ConfigStoreAdmin).FindVariables(path).Single(x => x.Key == "DB_HOST" && !x.IsDeleted);
		v.Value.Should().Be("prod-db");

		var s = ((IConfigStore)app.ConfigStoreAdmin).FindSecrets(path).Single(x => x.Key == "API_TOKEN" && !x.IsDeleted);
		System.Text.Encoding.UTF8.GetString(s.EncryptedValue).Should().NotContain("sk_live_abc", "secret must be encrypted at rest");
	}

	[Fact]
	public async Task Classify_Reveal_Temporarily_Shows_Plaintext()
	{
		var target = FreshUrlPath("reveal");
		await _page!.GotoAsync("/Import");
		await _page.GetByTestId("import-path").FillAsync(target);
		await _page.GetByTestId("import-format").SelectOptionAsync("env");
		await _page.GetByTestId("import-source").FillAsync("SECRET_VAL=visible-once");
		await _page.GetByTestId("import-classify").ClickAsync();

		var row = _page.Locator("[data-classify-key='SECRET_VAL']");
		await Expect(row.GetByTestId("classify-value")).ToHaveTextAsync("•••••••");
		await row.GetByTestId("classify-reveal").ClickAsync();
		// Reveal surfaces the raw HOCON right-hand side — for strings the dotenv converter
		// emits quoted values; unquoting happens at Save time (UnquoteHoconValue).
		var revealed = await row.GetByTestId("classify-value").TextContentAsync();
		revealed.Should().Contain("visible-once");
	}
}
