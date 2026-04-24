using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaConf.Core.Auth;

namespace YobaConf.Web.Pages.Admin;

public sealed class UsersModel : PageModel
{
    readonly IUserStore _store;
    readonly IUserAdmin _admin;
    readonly TimeProvider _clock;

    public UsersModel(IUserStore store, IUserAdmin admin, TimeProvider clock)
    {
        _store = store;
        _admin = admin;
        _clock = clock;
    }

    public IReadOnlyList<User> Users { get; private set; } = [];
    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    public void OnGet() => Load();

    public IActionResult OnPostCreate(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            ErrorMessage = "Username and password are required.";
            Load();
            return Page();
        }

        try
        {
            _admin.Create(username.Trim(), password, _clock.GetUtcNow(), User.Identity?.Name ?? "system");
            SuccessMessage = $"Created user '{username.Trim()}'.";
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }

        Load();
        return Page();
    }

    public IActionResult OnPostDelete(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            ErrorMessage = "Username is required.";
            Load();
            return Page();
        }

        // Server-side guard: never let the last row disappear. UI hides the button when
        // count == 1 but we double-check here since forms can be replayed out-of-band.
        if (_store.ListAll().Count <= 1)
        {
            ErrorMessage = "Cannot delete the last user — config-admin would take over. Add another user first.";
            Load();
            return Page();
        }

        if (_admin.Delete(username, _clock.GetUtcNow(), User.Identity?.Name ?? "system"))
            SuccessMessage = $"Deleted user '{username}'.";
        else
            ErrorMessage = $"User '{username}' not found.";

        Load();
        return Page();
    }

    void Load() => Users = _store.ListAll();
}
