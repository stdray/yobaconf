namespace YobaConf.E2ETests.Pages;

public sealed class NodePage(IPage page)
{
	public Task GotoAsync(string dotPath) =>
		page.GotoAsync($"/Node?path={Uri.EscapeDataString(dotPath)}");

	public ILocator Title => page.GetByTestId("node-title");
	public ILocator RawContent => page.GetByTestId("node-raw");
	public ILocator ResolvedJson => page.GetByTestId("node-json");
	public ILocator ETag => page.GetByTestId("node-etag");
	public ILocator FallthroughNotice => page.GetByTestId("node-fallthrough-notice");
	public ILocator FallthroughTarget => page.GetByTestId("fallthrough-target");
	public ILocator ResolveError => page.GetByTestId("resolve-error");
}
