using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using YobaConf.Core;
using YobaConf.Core.Auth;

namespace YobaConf.Web.Pages;

// DB-users-first, config-admin fallback (bootstrap + recovery path). Once the first DB
// user exists the config credentials stop being honored; delete the last DB user to
// revert. Antiforgery is ON from B.1 now that admin CRUD brings mutating forms on-shore.
[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    readonly AdminOptions _admin;
    readonly IUserStore? _users;

    // Optional IUserStore — integration tests that don't configure `Storage:DataDirectory`
    // skip user-store DI and rely on the config path. Runtime with Storage configured always
    // has it wired.
    public LoginModel(IOptions<AdminOptions> options, IUserStore? users = null)
    {
        _admin = options.Value;
        _users = users;
    }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Username { get; set; }
    public string? ErrorMessage { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(string? username, string? password, string? returnUrl)
    {
        Username = username;
        ReturnUrl = returnUrl;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            ErrorMessage = "Enter a username and password.";
            return Page();
        }

        if (!Authenticate(username, password))
        {
            ErrorMessage = "Invalid username or password.";
            return Page();
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, username)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : LocalRedirect("/");
    }

    bool Authenticate(string username, string password)
    {
        if (_users is not null && _users.HasAny())
            return _users.VerifyPassword(username, password);

        // DB empty → config-admin path. Both username + hash must be set.
        if (string.IsNullOrEmpty(_admin.Username) || string.IsNullOrEmpty(_admin.PasswordHash))
            return false;
        return string.Equals(_admin.Username, username, StringComparison.Ordinal)
            && AdminPasswordHasher.Verify(password, _admin.PasswordHash);
    }
}
