using Microsoft.Extensions.DependencyInjection;
using YobaConf.Core.Auth;
using YobaConf.E2ETests.Infrastructure;

namespace YobaConf.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class AdminProfileTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
    IBrowserContext? _ctx;
    IPage? _page;

    public async Task InitializeAsync()
    {
        // Pre-test cleanup — the WebAppFixture is shared across the [Collection], so any
        // tokens left by a previous test in this class would inflate the listing.
        foreach (var t in app.AdminTokenAdmin.ListByUsername(WebAppFixture.AdminUsername))
            app.AdminTokenAdmin.SoftDelete(t.Id, DateTimeOffset.UtcNow);

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
    public async Task Create_Token_Shows_Plaintext_Once_AndValidatesViaServer()
    {
        await _page!.GotoAsync("/admin/profile");

        await Expect(_page.GetByTestId("profile-username")).ToContainTextAsync(WebAppFixture.AdminUsername);

        var desc = "e2e-laptop-" + Guid.NewGuid().ToString("N")[..6];
        await _page.GetByTestId("profile-create-description").FillAsync(desc);
        await _page.GetByTestId("profile-create-submit").ClickAsync();

        var plaintextEl = _page.GetByTestId("profile-plaintext");
        await Expect(plaintextEl).ToBeVisibleAsync();
        var plaintext = (await plaintextEl.TextContentAsync())?.Trim() ?? "";
        plaintext.Should().HaveLength(22);

        // Server-side: token is listed for the admin user, and Validate(plaintext) succeeds.
        var listed = app.AdminTokenAdmin.ListByUsername(WebAppFixture.AdminUsername);
        listed.Should().Contain(t => t.Description == desc);

        var store = app.Services.GetRequiredService<IAdminTokenStore>();
        var validation = store.Validate(plaintext);
        validation.Should().BeOfType<AdminTokenValidation.Valid>();

        // Plaintext is one-shot: re-navigating to GET drops the alert.
        await _page.GotoAsync("/admin/profile");
        await Expect(_page.GetByTestId("profile-plaintext-alert")).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task Copy_Button_Clips_Plaintext_To_Clipboard()
    {
        await _page!.GotoAsync("/admin/profile");

        await _page.GetByTestId("profile-create-description").FillAsync("copy-test");
        await _page.GetByTestId("profile-create-submit").ClickAsync();

        var plaintextEl = _page.GetByTestId("profile-plaintext");
        await Expect(plaintextEl).ToBeVisibleAsync();
        var plaintext = (await plaintextEl.TextContentAsync())?.Trim() ?? "";

        await _page.GetByTestId("profile-copy-token").ClickAsync();
        await Expect(_page.GetByTestId("profile-copy-token")).ToHaveTextAsync("Copied");

        var clipboard = await _page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
        clipboard.Should().Be(plaintext);
    }

    [Fact]
    public async Task Revoke_Removes_Row_AndInvalidatesToken()
    {
        var created = app.AdminTokenAdmin.Create(
            WebAppFixture.AdminUsername, "revoke-target", DateTimeOffset.UtcNow);

        await _page!.GotoAsync("/admin/profile");
        _page.Dialog += (_, dlg) => _ = dlg.AcceptAsync();

        var row = _page.Locator($"[data-token-id='{created.Info.Id}']");
        await Expect(row).ToBeVisibleAsync();
        await row.GetByTestId("profile-token-revoke").ClickAsync();

        await Expect(_page.Locator($"[data-token-id='{created.Info.Id}']")).Not.ToBeVisibleAsync();

        // Validate fails on the revoked token.
        var store = app.Services.GetRequiredService<IAdminTokenStore>();
        store.Validate(created.Plaintext).Should().BeOfType<AdminTokenValidation.Invalid>();
    }
}
