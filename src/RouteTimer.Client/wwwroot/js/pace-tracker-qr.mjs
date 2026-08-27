import { toString } from "../vendor/qrcode/qrcode.mjs";
import { validateHandoffUrl } from "./pace-tracker-qr-core.mjs";

// Error correction M and a quiet zone of 2 modules: enough redundancy for a phone camera reading
// a screen at an angle without inflating a link this long past what a small panel can show.
const options = {
  type: "svg",
  errorCorrectionLevel: "M",
  margin: 2,
  width: 256
};

export async function render(element, url, expectedOrigin, now, expiresAt) {
  if (!element) {
    return;
  }

  const validated = validateHandoffUrl(url, expectedOrigin, Date.parse(now), Date.parse(expiresAt));
  const svg = await toString(validated, options);

  // Replaced wholesale rather than appended: a re-render must never leave the previous, now
  // superseded, code on screen beside the new one for a rider to scan by mistake.
  element.replaceChildren();
  element.insertAdjacentHTML("afterbegin", svg);
}

export function clear(element) {
  element?.replaceChildren();
}
