using YobaConf.E2ETests.Infrastructure;
using YobaConf.E2ETests.Pages;

namespace YobaConf.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class LoginTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
    IBrowserContext? _ctx;
    IPage? _page;

    public async Task InitializeAsync()
    {
        // Unauthenticated — login is what's under test.
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
    public async Task WrongPassword_ShowsError()
    {
        var login = new LoginPage(_page!);
        await login.GotoAsync();
        await login.SubmitAsync(WebAppFixture.AdminUsername, "definitely-wrong");
        await login.AssertErrorVisibleAsync();
        await login.AssertStillOnLoginAsync();
    }

    [Fact]
    public async Task ConfigAdmin_Works_When_DB_Is_Empty()
    {
        // Empty-DB bootstrap path: store has no users, login falls back to AdminOptions
        // (set by WebAppFixture via appsettings). This is the fixture's seed-login path
        // repeated — verifies the fallback holds across the lifetime.
        app.UserStore.HasAny().Should().BeFalse("fixture does not seed DB users");

        var login = new LoginPage(_page!);
        await login.GotoAsync();
        await login.SubmitAsync(WebAppFixture.AdminUsername, WebAppFixture.AdminPassword);

        await Expect(_page!.GetByTestId("index-home")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Login_Preserves_ReturnUrl()
    {
        var login = new LoginPage(_page!);
        await login.GotoAsync("/Somewhere");
        await login.SubmitAsync(WebAppFixture.AdminUsername, WebAppFixture.AdminPassword);

        // /Somewhere is nonexistent → ASP.NET returns 404 after redirect. We don't assert
        // 200 — only that we weren't bounced back to /Login (the cookie auth'd us).
        await Expect(_page!).Not.ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Login"));
    }

    [Fact]
    public async Task Missing_Antiforgery_Token_Returns_400()
    {
        // Antiforgery is enforced globally. A POST without the token must 400. Drive
        // via HttpClient (no browser) so the token-absent call is unambiguous.
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = WebAppFixture.AdminUsername,
            ["password"] = WebAppFixture.AdminPassword,
        });
        var res = await http.PostAsync(new Uri("/Login", UriKind.Relative), content);
        ((int)res.StatusCode).Should().Be(400);
    }
}
