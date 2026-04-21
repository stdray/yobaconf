// Entry-point for admin UI scripts.
// Phase A loads:
//   - prism-hocon: registers HOCON language tokens against the Prism core (loaded from CDN
//     in _Layout.cshtml) so `<code class="language-hocon">` blocks get coloured.
// Phase B adds CodeMirror editor + htmx-sse helpers.

import "./prism-hocon";

// Run Prism.highlightAll after DOM is ready so both language-json and language-hocon blocks
// get tokenised on the admin pages. Fires once per full page load — htmx swaps would need
// their own re-highlight hook (deferred to Phase B).
document.addEventListener("DOMContentLoaded", () => {
	window.Prism?.highlightAll?.();
});

export const version = "0.0.0" as const;
