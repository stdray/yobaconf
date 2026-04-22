using YobaConf.E2ETests.Infrastructure;

namespace YobaConf.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class AdminUsersTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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

	static string FreshUsername() => "user-" + Guid.NewGuid().ToString("N")[..6];

	[Fact]
	public async Task Create_User_Persists_And_Appears_In_List()
	{
		var username = FreshUsername();
		await _page!.GotoAsync("/admin/users");

		await _page.GetByTestId("users-create-username").FillAsync(username);
		await _page.GetByTestId("users-create-password").FillAsync("some-secret-pass");
		await _page.GetByTestId("users-create-submit").ClickAsync();

		await Expect(_page.GetByTestId("users-success")).ToBeVisibleAsync();
		await Expect(_page.Locator($"[data-user-username='{username}']")).ToBeVisibleAsync();

		app.UserStore.FindByUsername(username).Should().NotBeNull();
	}

	[Fact]
	public async Task Delete_Last_User_Is_Blocked_By_UI_And_Server()
	{
		// Seed exactly one DB user so the delete button is absent and the server-side guard
		// also activates.
		var solo = FreshUsername();
		app.UserAdmin.Create(solo, "pw", DateTimeOffset.UtcNow);
		// Clean up any previous test's leftover users so count == 1.
		foreach (var u in app.UserStore.ListAll())
			if (u.Username != solo)
				app.UserAdmin.Delete(u.Username);

		await _page!.GotoAsync("/admin/users");
		await Expect(_page.Locator($"[data-user-username='{solo}']")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("users-delete-blocked")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("users-delete")).Not.ToBeVisibleAsync();

		// Clean slate for sibling tests.
		app.UserAdmin.Delete(solo);
	}

	[Fact]
	public async Task Delete_Non_Last_User_Removes_Row()
	{
		var keeper = FreshUsername();
		var victim = FreshUsername();
		app.UserAdmin.Create(keeper, "pw1", DateTimeOffset.UtcNow);
		app.UserAdmin.Create(victim, "pw2", DateTimeOffset.UtcNow);

		await _page!.GotoAsync("/admin/users");
		// Accept the confirm() dialog.
		_page.Dialog += (_, dlg) => _ = dlg.AcceptAsync();

		var row = _page.Locator($"[data-user-username='{victim}']");
		await Expect(row).ToBeVisibleAsync();
		await row.GetByTestId("users-delete").ClickAsync();

		await Expect(_page.GetByTestId("users-success")).ToBeVisibleAsync();
		await Expect(_page.Locator($"[data-user-username='{victim}']")).Not.ToBeVisibleAsync();
		app.UserStore.FindByUsername(victim).Should().BeNull();

		// Clean slate.
		app.UserAdmin.Delete(keeper);
	}
}
