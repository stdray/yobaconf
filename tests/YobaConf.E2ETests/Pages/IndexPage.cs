namespace YobaConf.E2ETests.Pages;

public sealed class IndexPage(IPage page)
{
	public Task GotoAsync() => page.GotoAsync("/");

	public ILocator EmptyCta => page.GetByTestId("import-empty-cta");
	public ILocator NodeList => page.GetByTestId("node-list");
	public ILocator NodeLinks => page.GetByTestId("node-link");
	public ILocator ImportNavLink => page.GetByTestId("nav-import");
}
