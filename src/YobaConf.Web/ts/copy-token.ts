// Client-side copy-to-clipboard for one-shot plaintext tokens (api-keys, admin-profile).
// Each origin page renders a button with data-testid="<origin>-copy-token" and a feedback
// span with data-testid="<origin>-copy-feedback"; this module finds them via the shared
// suffix so a new origin only needs to follow the naming convention.

const COPY_BUTTON_SELECTOR = "button[data-testid$='-copy-token']";
const COPY_FEEDBACK_SELECTOR = "span[data-testid$='-copy-feedback']";

let _copyListenerAdded = false;

function onCopyClick(evt: Event): void {
	const button = (evt.target as HTMLElement)?.closest<HTMLButtonElement>(COPY_BUTTON_SELECTOR);
	if (!button) return;

	const token = button.dataset["token"];
	if (!token) return;

	// Feedback span lives in the same alert container as the button; scope the lookup
	// so multiple copy-token contexts on one page can't cross-fire. Falls back to a
	// global lookup for backwards compatibility with single-context pages.
	const container = button.closest(".alert");
	const feedbackEl = (container ?? document).querySelector(COPY_FEEDBACK_SELECTOR) as HTMLSpanElement | null;

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
