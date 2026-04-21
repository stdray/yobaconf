namespace YobaConf.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class LoginTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	IBrowserContext? _ctx;
	IPage? _page;

	public async Task InitializeAsync()
	{
		// Unauthenticated context — login flow itself is what's under test.
		_ctx = await app.NewContextAsync(authenticated: false);
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
	public async Task WrongCreds_ShowAlert()
	{
		var login = new LoginPage(_page!);
		await login.GotoAsync();

		await login.SubmitAsync(WebAppFixture.AdminUsername, "definitely-wrong");

		await login.AssertErrorVisibleAsync();
		await login.AssertStillOnLoginAsync();
	}

	[Fact]
	public async Task CorrectCreds_RedirectToIndex()
	{
		var login = new LoginPage(_page!);
		await login.GotoAsync();

		await login.SubmitAsync(WebAppFixture.AdminUsername, WebAppFixture.AdminPassword);

		// Authenticated layout's nav appears only on post-login pages — element-based
		// sentinel is more robust than URL matching.
		await Expect(_page!.GetByTestId("nav-configs")).ToBeVisibleAsync();
	}
}
