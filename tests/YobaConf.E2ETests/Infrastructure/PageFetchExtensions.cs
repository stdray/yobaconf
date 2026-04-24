using Microsoft.Playwright;

namespace YobaConf.E2ETests.Infrastructure;

// Helpers for E2E tests that trigger a JS fetch via a UI interaction (click / submit / keypress)
// and then assert on DOM state. Problem these solve: when the fetch returns non-2xx the page-side
// JS usually swallows the status into an error-branch (e.g. sets textContent = "Error"), and the
// downstream DOM assertion fails with "expected X got Error" — masking the real server-side
// failure (antiforgery / auth / routing / 500).
//
// Use ExpectFetchAsync(urlContains, action) to wrap the interaction. Any non-2xx surfaces
// immediately with method + URL + status + response body in the exception message — actionable
// diagnostic instead of a UI-level wild goose chase.
public static class PageFetchExtensions
{
    // Waits for the next response whose URL contains `urlContains` while `action` runs, then
    // asserts status == expectedStatus. Throws with full context on mismatch.
    //
    // Returns the IResponse so callers can inspect body / headers / timing if needed.
    public static async Task<IResponse> ExpectFetchAsync(
        this IPage page,
        string urlContains,
        Func<Task> action,
        int expectedStatus = 200)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(urlContains);
        ArgumentNullException.ThrowIfNull(action);

        var waitTask = page.WaitForResponseAsync(r => r.Url.Contains(urlContains, StringComparison.Ordinal));
        await action();
        var response = await waitTask;

        if (response.Status != expectedStatus)
        {
            string body;
            try { body = await response.TextAsync(); }
            catch (Exception ex) { body = $"<response body unreadable: {ex.Message}>"; }
            throw new InvalidOperationException(
                $"Expected {response.Request.Method} {response.Url} → {expectedStatus}, got {response.Status}.\n" +
                $"Response body:\n{body}");
        }
        return response;
    }
}
