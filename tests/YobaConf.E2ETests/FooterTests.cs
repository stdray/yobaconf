using YobaConf.E2ETests.Infrastructure;

namespace YobaConf.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class FooterTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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
    public async Task Footer_Renders_Version_String()
    {
        await _page!.GotoAsync("/Bindings");
        var footer = _page.GetByTestId("footer-version");
        await Expect(footer).ToBeVisibleAsync();
        await Expect(footer).ToContainTextAsync("v");
    }
}
