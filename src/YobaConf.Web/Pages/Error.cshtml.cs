using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace YobaConf.Web.Pages;

// [AllowAnonymous] is defence-in-depth. With the global fallback policy requiring
// auth, any unauthenticated request to /Error (including the one triggered by
// UseExceptionHandler when DI itself blew up) would challenge back to /Login. If /Login
// also hits the same upstream failure, the two pages redirect-loop each other — exactly
// what happened to prod on 2026-04-22 when a v1-volume schema mismatch crashed
// SqliteUserStore's ctor before any request could authenticate. Let /Error render
// regardless; an unauth user who stumbled here has already earned a 500 page.
[AllowAnonymous]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
	public string? RequestId { get; set; }

	public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

	public void OnGet()
	{
		RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
	}
}
