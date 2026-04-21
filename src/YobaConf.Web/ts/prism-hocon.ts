// HOCON language component for Prism.js. Ported from the sabieber/vscode-hocon TextMate
// grammar (MIT) — one-time port, we're not chasing upstream updates because HOCON syntax
// is stable.
//
// Coverage (good-enough for admin-view read path, not a full LSP):
//   - line comments `#` and `//`
//   - quoted strings `"..."` with escape sequences
//   - substitution variables `${var.path}` and `${?optional}`
//   - keywords `include`, `url`, `file`, `classpath`, `required`
//   - booleans `true/false/on/off/yes/no` and `null`
//   - numbers (with optional duration/byte-size suffix like `10s`, `1MB`)
//   - structural punctuation { } [ ] = : ,
//
// Intentionally skipped: triple-quoted strings, invalid-escape highlighting, unquoted
// strings (they'd compete with keyword/boolean tokens and the UI copes fine without them).

// Minimal Prism API shape — we attach a new language without pulling the full Prism
// TypeScript types (not worth the dep). `unknown` narrows away at the use site.
declare global {
	interface Window {
		Prism?: {
			languages: Record<string, unknown>;
			highlightAll?: () => void;
		};
	}
}

const hoconLanguage = {
	comment: [
		{ pattern: /#.*/, greedy: true },
		{ pattern: /\/\/.*/, greedy: true },
	],
	string: {
		pattern: /"(?:[^"\\]|\\.)*"/,
		greedy: true,
		inside: {
			entity: /\\(?:["\\/bfnrt]|u[0-9a-fA-F]{4})/,
		},
	},
	variable: {
		// ${var}, ${?optional.var}
		pattern: /\$\{\??[^}]*\}/,
		greedy: true,
		alias: "important",
	},
	keyword: /\b(?:include|url|file|classpath|required)\b/,
	boolean: /\b(?:true|false|on|off|yes|no)\b/,
	null: {
		pattern: /\bnull\b/,
		alias: "keyword",
	},
	// Numbers with optional duration/bytesize suffix. The suffix alternation is
	// permissive (any letters) so duration-long / bytesize-long (`5seconds`, `10kilobytes`)
	// colour the same as the short forms — users read them the same anyway.
	number: {
		pattern: /-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?(?:[a-zA-Z]+)?\b/,
		greedy: true,
	},
	punctuation: /[{}[\]=:,]/,
};

if (window.Prism) {
	window.Prism.languages["hocon"] = hoconLanguage;
}

// Empty export turns the file into a module. Required for the `declare global` above to
// augment the global scope — TS forbids augmentations in script-mode files.
export { };
