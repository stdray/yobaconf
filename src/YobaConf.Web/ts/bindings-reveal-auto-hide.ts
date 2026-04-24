// 10-second auto-hide for revealed secrets: if the URL carries ?revealId=...,
// rewrite it out after the timeout so a browser reload doesn't resurrect the
// plaintext. The server-side render has already inlined the plaintext into a
// `data-testid="bindings-secret-revealed"` span; this module just fades it
// after the window expires.
//
// Attached globally via admin.ts bundle entry. Pages without `?revealId=` on
// the URL early-return without side effects, so the module is cheap to include
// site-wide rather than gated per-page.

const params = new URLSearchParams(window.location.search);
if (params.has("revealId")) {
	setTimeout(() => {
		params.delete("revealId");
		const qs = params.toString();
		const url = window.location.pathname + (qs ? `?${qs}` : "");
		window.history.replaceState({}, "", url);
		for (const el of document.querySelectorAll('[data-testid="bindings-secret-revealed"]')) {
			(el as HTMLElement).textContent = "••••••";
		}
	}, 10_000);
}
