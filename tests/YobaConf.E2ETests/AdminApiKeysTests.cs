using Microsoft.Extensions.DependencyInjection;
using YobaConf.Core.Auth;
using YobaConf.Core.Bindings;
using YobaConf.E2ETests.Infrastructure;

namespace YobaConf.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class AdminApiKeysTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	IBrowserContext? _ctx;
	IPage? _page;

	public async Task InitializeAsync()
	{
		// Pre-test cleanup — fixture is shared, so stale keys from a previous test (or a
		// failed earlier assertion) would break independence of this one.
		foreach (var k in app.ApiKeyAdmin.ListActive())
			app.ApiKeyAdmin.SoftDelete(k.Id, DateTimeOffset.UtcNow);

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
	public async Task Create_Key_Shows_Plaintext_Once_And_Validates()
	{
		await _page!.GotoAsync("/admin/api-keys");

		var desc = "e2e-" + Guid.NewGuid().ToString("N")[..6];
		await _page.GetByTestId("api-keys-create-description").FillAsync(desc);
		await _page.GetByTestId("api-keys-create-required-tags").FillAsync("env=prod");
		await _page.GetByTestId("api-keys-create-submit").ClickAsync();

		// Plaintext shown exactly once.
		var plaintextEl = _page.GetByTestId("api-keys-plaintext");
		await Expect(plaintextEl).ToBeVisibleAsync();
		var plaintext = (await plaintextEl.TextContentAsync())?.Trim() ?? "";
		plaintext.Should().HaveLength(22);

		// Server-side: listing has the new row; Validate(plaintext) succeeds for the scope.
		var keys = app.ApiKeyAdmin.ListActive();
		keys.Should().Contain(k => k.Description == desc);

		var store = app.Services.GetRequiredService<IApiKeyStore>();
		var validation = store.Validate(plaintext);
		validation.Should().BeOfType<ApiKeyValidation.Valid>();

		// Plaintext alert is a one-shot — navigating back to the page (GET) drops it.
		// (ReloadAsync resubmits the POST and would re-render the alert with a fresh key.)
		await _page.GotoAsync("/admin/api-keys");
		await Expect(_page.GetByTestId("api-keys-plaintext-alert")).Not.ToBeVisibleAsync();
	}

	[Fact]
	public async Task Invalid_Required_Tags_Are_Rejected_With_Error()
	{
		await _page!.GotoAsync("/admin/api-keys");

		await _page.GetByTestId("api-keys-create-description").FillAsync("invalid");
		// "nope" without an '=' is malformed.
		await _page.GetByTestId("api-keys-create-required-tags").FillAsync("nope");
		await _page.GetByTestId("api-keys-create-submit").ClickAsync();

		await Expect(_page.GetByTestId("api-keys-error")).ToBeVisibleAsync();
		app.ApiKeyAdmin.ListActive().Should().BeEmpty();
	}

	[Fact]
	public async Task Delete_Removes_Row_And_Invalidates_Token()
	{
		var created = app.ApiKeyAdmin.Create(
			TagSet.Empty, null, "to-be-deleted", DateTimeOffset.UtcNow);

		await _page!.GotoAsync("/admin/api-keys");
		_page.Dialog += (_, dlg) => _ = dlg.AcceptAsync();

		var row = _page.Locator($"[data-key-id='{created.Info.Id}']");
		await Expect(row).ToBeVisibleAsync();
		await row.GetByTestId("api-keys-delete").ClickAsync();

		await Expect(_page.Locator($"[data-key-id='{created.Info.Id}']")).Not.ToBeVisibleAsync();

		var store = app.Services.GetRequiredService<IApiKeyStore>();
		store.Validate(created.Plaintext).Should().BeOfType<ApiKeyValidation.Invalid>();
	}
}
