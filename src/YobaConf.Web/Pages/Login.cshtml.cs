using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using YobaConf.Core;

namespace YobaConf.Web.Pages;

// Antiforgery disabled for Phase A — login is pre-auth (no session cookie to protect).
// Import page (the only other mutating Razor form in Phase A) has it off too for symmetry
// and to keep the paste-import flow simple. Phase B's admin CRUD will wire antiforgery in
// when the form surface grows (mirrors yobalog's trajectory — they also started with
// IgnoreAntiforgeryToken on Login and re-enabled when admin CRUD expanded).
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class LoginModel : PageModel
{
	readonly AdminOptions _admin;

	public LoginModel(IOptions<AdminOptions> options) => _admin = options.Value;

	[BindProperty(SupportsGet = true)]
	public string? ReturnUrl { get; set; }

	public string? Username { get; set; }
	public string? ErrorMessage { get; set; }

	public void OnGet()
	{
	}

	public async Task<IActionResult> OnPostAsync(string? username, string? password, string? returnUrl)
	{
		Username = username;
		ReturnUrl = returnUrl;

		if (string.IsNullOrEmpty(_admin.Username) || string.IsNullOrEmpty(_admin.PasswordHash))
		{
			ErrorMessage = "Admin credentials are not configured. Set Admin:Username and Admin:PasswordHash in appsettings.";
			return Page();
		}

		// Username compare is plain ordinal — usernames aren't considered secret.
		// Password verify is constant-time inside AdminPasswordHasher.Verify.
		var usernameMatches = string.Equals(_admin.Username, username, StringComparison.Ordinal);
		var passwordMatches = !string.IsNullOrEmpty(password)
			&& AdminPasswordHasher.Verify(password, _admin.PasswordHash);

		if (!usernameMatches || !passwordMatches)
		{
			ErrorMessage = "Invalid username or password.";
			return Page();
		}

		var identity = new ClaimsIdentity(
			[new Claim(ClaimTypes.Name, _admin.Username)],
			CookieAuthenticationDefaults.AuthenticationScheme);
		await HttpContext.SignInAsync(
			CookieAuthenticationDefaults.AuthenticationScheme,
			new ClaimsPrincipal(identity));

		return LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
	}
}
