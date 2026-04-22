using YobaConf.Core.Security;

namespace YobaConf.E2ETests;

// Phase B.3 — Variables + Secrets inline CRUD on /Node. Each test seeds a fresh path
// via IConfigStoreAdmin (same backend the UI hits), exercises one flow through the
// browser, and asserts against the server-side store state, not just the rendered DOM —
// that way snapshot drift in the UI doesn't hide a real backend regression.
[Collection(nameof(UiCollection))]
public sealed class NodeCrudTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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
		NodePath.ParseDb("b3-" + stem + "-" + Guid.NewGuid().ToString("N")[..6]);

	[Fact]
	public async Task AddVariable_Persists_And_Appears_In_List()
	{
		var path = FreshPath("addvar");
		app.ConfigStoreAdmin.UpsertNode(path, "x = 1", DateTimeOffset.UtcNow);

		await _page!.GotoAsync($"/Node?path={path.ToUrlPath()}");
		await _page.GetByTestId("add-variable-key").FillAsync("db_host");
		await _page.GetByTestId("add-variable-value").FillAsync("prod-db.internal");
		await _page.GetByTestId("add-variable-submit").ClickAsync();

		// Post-redirect page shows the new row.
		await Expect(_page.GetByTestId("variables-list")).ToContainTextAsync("db_host");
		// Backend check.
		var v = ((IConfigStore)app.ConfigStoreAdmin).FindVariables(path).Single(x => x.Key == "db_host" && !x.IsDeleted);
		v.Value.Should().Be("prod-db.internal");
	}

	[Fact]
	public async Task UpdateVariable_Writes_NewValue_And_Appends_Audit()
	{
		var path = FreshPath("updvar");
		app.ConfigStoreAdmin.UpsertNode(path, "x = 1", DateTimeOffset.UtcNow);
		app.ConfigStoreAdmin.UpsertVariable(path, "db_host", "old-value", DateTimeOffset.UtcNow);

		await _page!.GotoAsync($"/Node?path={path.ToUrlPath()}");
		var row = _page.Locator("[data-variable-key='db_host']");
		await row.GetByTestId("variable-value").FillAsync("new-value");
		await row.GetByTestId("variable-save").ClickAsync();

		await Expect(_page.GetByTestId("node-success")).ToBeVisibleAsync();
		var v = ((IConfigStore)app.ConfigStoreAdmin).FindVariables(path).Single(x => x.Key == "db_host" && !x.IsDeleted);
		v.Value.Should().Be("new-value");

		var audit = ((IAuditLogStore)app.ConfigStoreAdmin).FindByPath(path, includeDescendants: false, skip: 0, take: 50);
		audit.Should().Contain(e => e.EntityType == AuditEntityType.Variable && e.Action == AuditAction.Updated && e.NewValue == "new-value");
	}

	[Fact]
	public async Task DeleteVariable_Removes_Row()
	{
		var path = FreshPath("delvar");
		app.ConfigStoreAdmin.UpsertNode(path, "x = 1", DateTimeOffset.UtcNow);
		app.ConfigStoreAdmin.UpsertVariable(path, "to_delete", "bye", DateTimeOffset.UtcNow);

		await _page!.GotoAsync($"/Node?path={path.ToUrlPath()}");
		await _page.Locator("[data-variable-key='to_delete']").GetByTestId("variable-delete").ClickAsync();

		await Expect(_page.GetByTestId("node-success")).ToBeVisibleAsync();
		((IConfigStore)app.ConfigStoreAdmin).FindVariables(path).Where(v => !v.IsDeleted).Should().BeEmpty();
	}

	[Fact]
	public async Task AddSecret_Encrypts_And_Masks_In_UI()
	{
		var path = FreshPath("addsec");
		app.ConfigStoreAdmin.UpsertNode(path, "x = 1", DateTimeOffset.UtcNow);

		await _page!.GotoAsync($"/Node?path={path.ToUrlPath()}");
		await _page.GetByTestId("add-secret-key").FillAsync("api_token");
		await _page.GetByTestId("add-secret-value").FillAsync("sk_live_deadbeef");
		await _page.GetByTestId("add-secret-submit").ClickAsync();

		var row = _page.Locator("[data-secret-key='api_token']");
		await Expect(row).ToBeVisibleAsync();
		// Masked by default — plaintext must NOT appear in the DOM on initial render.
		await Expect(row.GetByTestId("secret-value-masked")).ToBeVisibleAsync();
		var dom = await _page.ContentAsync();
		dom.Should().NotContain("sk_live_deadbeef", "plaintext must never be in initial HTML");

		// Backend: secret is stored encrypted; ciphertext bytes are NOT the plaintext.
		var s = ((IConfigStore)app.ConfigStoreAdmin).FindSecrets(path).Single(x => x.Key == "api_token" && !x.IsDeleted);
		System.Text.Encoding.UTF8.GetString(s.EncryptedValue).Should().NotBe("sk_live_deadbeef");
	}

	[Fact]
	public async Task RevealSecret_ShowsPlaintext_Inline()
	{
		var path = FreshPath("revsec");
		app.ConfigStoreAdmin.UpsertNode(path, "x = 1", DateTimeOffset.UtcNow);
		var enc = app.Services.GetService(typeof(ISecretEncryptor)) as ISecretEncryptor;
		enc.Should().NotBeNull("fixture registers an encryptor with the deterministic test key");
		var bundle = enc!.Encrypt("hunter2-plaintext");
		app.ConfigStoreAdmin.UpsertSecret(path, "db_password", bundle.Ciphertext, bundle.Iv, bundle.AuthTag, bundle.KeyVersion, DateTimeOffset.UtcNow);

		await _page!.GotoAsync($"/Node?path={path.ToUrlPath()}");
		var row = _page.Locator("[data-secret-key='db_password']");
		await row.GetByTestId("secret-reveal").ClickAsync();

		// Revealed input visible with plaintext; masked one not.
		await Expect(row.GetByTestId("secret-value-revealed")).ToBeVisibleAsync();
		var revealedValue = await row.GetByTestId("secret-value-revealed").InputValueAsync();
		revealedValue.Should().Be("hunter2-plaintext");
	}

	[Fact]
	public async Task DeleteSecret_Removes_Row()
	{
		var path = FreshPath("delsec");
		app.ConfigStoreAdmin.UpsertNode(path, "x = 1", DateTimeOffset.UtcNow);
		var enc = (ISecretEncryptor)app.Services.GetService(typeof(ISecretEncryptor))!;
		var bundle = enc.Encrypt("x");
		app.ConfigStoreAdmin.UpsertSecret(path, "gone_soon", bundle.Ciphertext, bundle.Iv, bundle.AuthTag, bundle.KeyVersion, DateTimeOffset.UtcNow);

		await _page!.GotoAsync($"/Node?path={path.ToUrlPath()}");
		await _page.Locator("[data-secret-key='gone_soon']").GetByTestId("secret-delete").ClickAsync();

		await Expect(_page.GetByTestId("node-success")).ToBeVisibleAsync();
		((IConfigStore)app.ConfigStoreAdmin).FindSecrets(path).Where(s => !s.IsDeleted).Should().BeEmpty();
	}

	[Fact]
	public async Task ResolvedJson_Redacts_TopLevel_Secret_Value()
	{
		var path = FreshPath("redact");
		// HOCON references db_password, which is also registered as a Secret on this scope.
		// Variables-fragment renders it top-level in JSON; redaction replaces the value.
		app.ConfigStoreAdmin.UpsertNode(path, "db_password = ${db_password}", DateTimeOffset.UtcNow);
		var enc = (ISecretEncryptor)app.Services.GetService(typeof(ISecretEncryptor))!;
		var bundle = enc.Encrypt("super-secret-plaintext");
		app.ConfigStoreAdmin.UpsertSecret(path, "db_password", bundle.Ciphertext, bundle.Iv, bundle.AuthTag, bundle.KeyVersion, DateTimeOffset.UtcNow);

		await _page!.GotoAsync($"/Node?path={path.ToUrlPath()}");
		var json = await _page.GetByTestId("node-json").TextContentAsync();
		json.Should().NotBeNull();
		json!.Should().Contain("(secret)");
		json.Should().NotContain("super-secret-plaintext", "redaction must hide the decrypted value from preview");
	}
}
