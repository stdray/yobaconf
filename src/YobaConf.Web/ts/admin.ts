// Entry-point for admin UI scripts.
// v1 loaded `./prism-hocon` for HOCON syntax highlighting — HOCON is out (spec v2),
// prism-hocon.ts deleted in Phase A.0. Placeholder for Phase B.4+ admin UI wiring
// (binding editor htmx handlers, tag autocomplete, facet-filter sugar).

// Per-feature modules register themselves on import. Keeping one bundle-entry
// (this file) means admin.js stays the single <script> tag in _Layout.cshtml.
import "./bindings-reveal-auto-hide";

export const version = "0.0.0" as const;
