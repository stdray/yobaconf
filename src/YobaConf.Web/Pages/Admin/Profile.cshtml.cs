using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaConf.Core.Auth;

namespace YobaConf.Web.Pages.Admin;

// Per-user admin-token CRUD page (Phase G.5). Lists / creates / revokes the personal
// access tokens belonging to the currently-logged-in user. Other users' tokens are
// invisible — listing is filtered server-side by `User.Identity?.Name`. Plaintext is
// shown exactly once on create (same flow as /admin/api-keys). Soft-delete acts as
// "revoke" — row stays for audit.
public sealed class ProfileModel : PageModel
{
    readonly IAdminTokenAdmin _admin;
    readonly TimeProvider _clock;

    public ProfileModel(IAdminTokenAdmin admin, TimeProvider clock)
    {
        _admin = admin;
        _clock = clock;
    }

    public string Username { get; private set; } = string.Empty;
    public IReadOnlyList<AdminTokenInfo> Tokens { get; private set; } = [];
    public string? ErrorMessage { get; set; }
    public string? NewlyCreatedToken { get; set; }
    public string? NewlyCreatedPrefix { get; set; }

    public void OnGet() => Load();

    public IActionResult OnPostCreate(string? description)
    {
        Load();
        if (string.IsNullOrEmpty(Username))
        {
            ErrorMessage = "Cannot create token: no authenticated user.";
            return Page();
        }
        if (string.IsNullOrWhiteSpace(description))
        {
            ErrorMessage = "Description is required.";
            return Page();
        }

        var created = _admin.Create(Username, description.Trim(), _clock.GetUtcNow(), Username);
        NewlyCreatedToken = created.Plaintext;
        NewlyCreatedPrefix = created.Info.TokenPrefix;
        Load();
        return Page();
    }

    public IActionResult OnPostRevoke(long? id)
    {
        Load();
        if (id is null)
        {
            ErrorMessage = "Missing token id.";
            return Page();
        }

        // Per-user isolation: only revoke a token if it belongs to the logged-in user.
        // Without this, /Admin/Profile would let any admin revoke any other admin's
        // token by guessing the Id. The store's SoftDelete is by-id, so the page does
        // the ownership check before delegating.
        var owned = Tokens.Any(t => t.Id == id.Value);
        if (!owned)
        {
            ErrorMessage = $"Token {id.Value} not found among your tokens.";
            return Page();
        }

        if (!_admin.SoftDelete(id.Value, _clock.GetUtcNow(), Username))
            ErrorMessage = $"Token {id.Value} not found.";

        Load();
        return Page();
    }

    void Load()
    {
        Username = User.Identity?.Name ?? string.Empty;
        Tokens = string.IsNullOrEmpty(Username) ? [] : _admin.ListByUsername(Username);
    }
}
