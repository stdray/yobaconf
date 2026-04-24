// Client-side copy-to-clipboard for the API key plaintext token.
// Single use — token is shown once after creation.
// Click copies via navigator.clipboard, shows "Copied" for 2s.

const COPY_BUTTON_SELECTOR = "button[data-testid='api-keys-copy-token']";
const COPY_FEEDBACK_SELECTOR = "span[data-testid='api-keys-copy-feedback']";

let _copyListenerAdded = false;

function onCopyClick(evt: Event): void {
	const button = (evt.target as HTMLElement)?.closest<HTMLButtonElement>(COPY_BUTTON_SELECTOR);
	if (!button) return;

	const token = button.dataset["token"];
	if (!token) return;

	const feedbackEl = document.querySelector(COPY_FEEDBACK_SELECTOR) as HTMLSpanElement | null;

	button.disabled = true;

	navigator.clipboard
		.writeText(token)
		.then(() => {
			button.textContent = "Copied";
			if (feedbackEl) {
				feedbackEl.textContent = "Copied to clipboard";
			}
		})
		.catch(() => {
			button.textContent = "Failed";
		})
		.finally(() => {
			const timerId = window.setTimeout(() => {
				button.textContent = "Copy";
				button.disabled = false;
				if (feedbackEl) {
					feedbackEl.textContent = "";
				}
			}, 2_000);
			button.dataset["timer"] = String(timerId);
		});
}

function _init(): void {
	if (_copyListenerAdded) return;
	if (!document.querySelector(COPY_BUTTON_SELECTOR)) return;
	document.addEventListener("click", onCopyClick);
	_copyListenerAdded = true;
}

_init();
