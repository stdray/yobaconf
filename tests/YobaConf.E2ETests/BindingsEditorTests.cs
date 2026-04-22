using Microsoft.Extensions.DependencyInjection;
using YobaConf.Core.Bindings;
using YobaConf.Core.Security;
using YobaConf.E2ETests.Infrastructure;

namespace YobaConf.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class BindingsEditorTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	IBrowserContext? _ctx;
	IPage? _page;

	public async Task InitializeAsync()
	{
		foreach (var b in app.Services.GetRequiredService<Core.Storage.SqliteBindingStore>().ListActive())
			app.BindingAdmin.SoftDelete(b.Id, DateTimeOffset.UtcNow);

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
	public async Task Create_Plain_Binding_Persists_And_Redirects()
	{
		await _page!.GotoAsync("/Bindings/Edit");

		await _page.GetByTestId("edit-tags").FillAsync("env=prod");
		await _page.GetByTestId("edit-key").FillAsync("db.host");
		await _page.GetByTestId("edit-value").FillAsync("\"prod-db\"");
		await _page.GetByTestId("edit-save").ClickAsync();

		await Expect(_page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Bindings$"));
		await Expect(_page.Locator("[data-testid='bindings-key']").GetByText("db.host")).ToBeVisibleAsync();

		var stored = app.Services.GetRequiredService<Core.Storage.SqliteBindingStore>().ListActive();
		stored.Should().ContainSingle(b => b.KeyPath == "db.host" && b.Kind == BindingKind.Plain);
	}

	[Fact]
	public async Task Create_Secret_Binding_Encrypts_Value()
	{
		await _page!.GotoAsync("/Bindings/Edit");

		await _page.GetByTestId("edit-tags").FillAsync("env=prod");
		await _page.GetByTestId("edit-key").FillAsync("db.password");
		await _page.GetByTestId("edit-kind-secret").ClickAsync();
		await _page.GetByTestId("edit-value").FillAsync("correct-horse-battery");
		await _page.GetByTestId("edit-save").ClickAsync();

		await Expect(_page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Bindings$"));

		var store = app.Services.GetRequiredService<Core.Storage.SqliteBindingStore>();
		var stored = store.ListActive().Single(b => b.KeyPath == "db.password");
		stored.Kind.Should().Be(BindingKind.Secret);
		stored.Ciphertext.Should().NotBeNull().And.NotBeEmpty();
		stored.ValuePlain.Should().BeNull();

		// Plaintext survives encrypt/decrypt.
		var enc = app.Services.GetRequiredService<ISecretEncryptor>();
		enc.Decrypt(stored.Ciphertext!, stored.Iv!, stored.AuthTag!, stored.KeyVersion!)
			.Should().Be("correct-horse-battery");
	}

	[Fact]
	public async Task Create_Incomparable_Sibling_Surfaces_Conflict_Warning()
	{
		// Seed a binding that will share KeyPath+TagCount but incomparable TagSet.
		app.BindingAdmin.Upsert(new Binding
		{
			Id = 0,
			TagSet = TagSet.From([new("env", "prod")]),
			KeyPath = "log-level",
			Kind = BindingKind.Plain,
			ValuePlain = "\"Info\"",
			ContentHash = string.Empty,
			UpdatedAt = DateTimeOffset.UtcNow,
		});

		await _page!.GotoAsync("/Bindings/Edit");
		await _page.GetByTestId("edit-tags").FillAsync("project=yobapub");
		await _page.GetByTestId("edit-key").FillAsync("log-level");
		await _page.GetByTestId("edit-value").FillAsync("\"Debug\"");
		await _page.GetByTestId("edit-save").ClickAsync();

		// Save succeeds (binding stored), but the editor stays open with a conflict
		// advisory instead of redirecting — gives the admin a chance to add an overlay.
		await Expect(_page.GetByTestId("edit-conflict")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("edit-conflict")).ToContainTextAsync("same KeyPath and specificity");
	}
}
