// Client-side secret reveal for the Bindings dashboard.
// POSTs to /Bindings/Index?handler=Reveal with an antiforgery token,
// receives plaintext JSON, replaces the masked span, auto-masks after 10s.

const MASKED = "\u2022\u2022\u2022\u2022\u2022\u2022"; // masked placeholder

function onRevealClick(evt: Event): void {
	const button = (evt.target as HTMLElement)?.closest<HTMLButtonElement>(
		"button[data-testid='bindings-secret-reveal']",
	);
	if (!button) return;
	const bindingId = button.dataset["bindingId"];
	if (!bindingId) return;

	// Cancel any in-flight timer for this binding.
	const prevTimer = Number.parseInt(button.dataset["timer"] ?? "0", 10);
	if (prevTimer) clearTimeout(prevTimer);

	const span = button.parentElement?.querySelector<HTMLSpanElement>(
		`[data-testid="bindings-secret-masked"][data-binding-id="${bindingId}"]`,
	);
	if (!span) return;

	// Read antiforgery token from the page.
	const tokenInput = document.getElementById("__requestVerificationToken") as HTMLInputElement | null;
	const token = tokenInput?.value ?? "";

	const body = new URLSearchParams();
	body.set("id", bindingId);
	body.set("__RequestVerificationToken", token);

	fetch("/Bindings?handler=Reveal", {
		method: "POST",
		headers: { "Content-Type": "application/x-www-form-urlencoded" },
		body: body.toString(),
	})
		.then(async (res) => {
			if (res.ok) {
				const json = await res.json();
				span.textContent = json.plaintext;
				span.setAttribute("data-testid", "bindings-secret-revealed");

				const timerId = window.setTimeout(() => {
					span.textContent = MASKED;
					span.setAttribute("data-testid", "bindings-secret-masked");
				}, 10_000);
				button.dataset["timer"] = String(timerId);
			}
		})
		.catch(() => {
			/* network error - leave masked */
		});
}

// Bootstrap: wire reveal buttons if the bindings page is visible.
let _listenerAdded = false;

function bootstrap(): void {
	if (_listenerAdded) return;
	if (!document.querySelector('[data-testid="bindings-secret-reveal"]')) return;
	document.addEventListener("click", onRevealClick);
	_listenerAdded = true;
}

bootstrap();
