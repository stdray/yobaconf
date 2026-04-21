using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaConf.Core;

namespace YobaConf.Web.Pages;

public sealed class IndexModel : PageModel
{
	readonly IConfigStore _store;

	public IndexModel(IConfigStore store) => _store = store;

	public IReadOnlyList<NodePath> Paths { get; private set; } = [];

	public void OnGet() => Paths = _store.ListNodePaths();
}
