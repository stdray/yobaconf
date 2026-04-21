namespace YobaConf.E2ETests.Pages;

public sealed class LoginPage(IPage page)
{
	public Task GotoAsync() => page.GotoAsync("/Login");

	public async Task SubmitAsync(string username, string password)
	{
		await page.GetByTestId("login-username").FillAsync(username);
		await page.GetByTestId("login-password").FillAsync(password);
		await page.GetByTestId("login-submit").ClickAsync();
	}

	public Task AssertErrorVisibleAsync() =>
		Expect(page.GetByTestId("login-error")).ToBeVisibleAsync();

	public Task AssertStillOnLoginAsync() =>
		Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Login"));
}
