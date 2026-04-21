import daisyui from "daisyui";

/** @type {import('tailwindcss').Config} */
export default {
	// AGENTS.md invariant: no HTML in .cs files -- Razor owns all markup,
	// so Tailwind only scans .cshtml + .ts. Same scan list as yobalog.
	content: ["./Pages/**/*.cshtml", "./Views/**/*.cshtml", "./ts/**/*.ts"],
	theme: {
		extend: {},
	},
	plugins: [daisyui],
	daisyui: {
		themes: ["dark", "night", "business"],
		darkTheme: "dark",
		logs: false,
	},
};
