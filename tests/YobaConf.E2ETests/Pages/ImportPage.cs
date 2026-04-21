namespace YobaConf.E2ETests.Pages;

public sealed class ImportPage(IPage page)
{
	public Task GotoAsync() => page.GotoAsync("/Import");

	public async Task FillAsync(string path, string format, string source)
	{
		await page.GetByTestId("import-path").FillAsync(path);
		await page.GetByTestId("import-format").SelectOptionAsync(format);
		await page.GetByTestId("import-source").FillAsync(source);
	}

	public Task ClickConvertAsync() => page.GetByTestId("import-convert").ClickAsync();
	public Task ClickSaveAsync() => page.GetByTestId("import-save").ClickAsync();

	public ILocator Preview => page.GetByTestId("import-preview");
	public ILocator SuccessAlert => page.GetByTestId("import-success");
	public ILocator ErrorAlert => page.GetByTestId("import-error");
}
