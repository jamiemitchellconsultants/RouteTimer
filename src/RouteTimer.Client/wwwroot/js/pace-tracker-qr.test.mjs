import test from "node:test";
import assert from "node:assert/strict";

import { validateHandoffUrl } from "./pace-tracker-qr-core.mjs";

const origin = "https://pacetracking.tqaentry.com";
const now = Date.parse("2026-08-27T12:00:00Z");
const expiresAt = Date.parse("2026-08-27T12:10:00Z");
const link = `${origin}/open?src=rt&v=1&payload=https%3A%2F%2Fpacetracking.tqaentry.com%2Fapi%2Fhandoffs%2FAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&name=Kingston%20%26%20Dorking&ts=1787832000000&sig=abc`;

test("returns the link unchanged when it matches the expected origin and is unexpired", () => {
  assert.equal(validateHandoffUrl(link, origin, now, expiresAt), link);
});

test("preserves unicode and query text exactly, so the signature still verifies", () => {
  const unicode = `${origin}/open?src=rt&v=1&payload=x&name=Caf%C3%A9%20%26%20coast&ts=1&sig=abc`;

  assert.equal(validateHandoffUrl(unicode, origin, now, expiresAt), unicode);
});

test("rejects a value that is not a URL at all", () => {
  assert.throws(() => validateHandoffUrl("not a url", origin, now, expiresAt), /not a valid link/i);
});

test("rejects a non-HTTPS link", () => {
  assert.throws(
    () => validateHandoffUrl(link.replace("https://", "http://"), origin, now, expiresAt),
    /secure/i);
});

// The whole point of the independent origin: a compromised or misconfigured server response must
// not be able to send the rider's phone to some other host.
test("rejects a foreign origin", () => {
  assert.throws(
    () => validateHandoffUrl("https://elsewhere.invalid/open?src=rt", origin, now, expiresAt),
    /expected PaceTracker/i);
});

test("rejects a link on the expected origin but the wrong path", () => {
  assert.throws(
    () => validateHandoffUrl(`${origin}/somewhere?src=rt`, origin, now, expiresAt),
    /PaceTracker handoff link/i);
});

test("rejects an already expired link", () => {
  assert.throws(() => validateHandoffUrl(link, origin, expiresAt, expiresAt), /expired/i);
  assert.throws(() => validateHandoffUrl(link, origin, expiresAt + 1, expiresAt), /expired/i);
});

test("accepts a link one millisecond before expiry", () => {
  assert.equal(validateHandoffUrl(link, origin, expiresAt - 1, expiresAt), link);
});

test("rejects an unusable expected origin rather than trusting the link", () => {
  assert.throws(() => validateHandoffUrl(link, "", now, expiresAt), /expected PaceTracker/i);
  assert.throws(() => validateHandoffUrl(link, "not-an-origin", now, expiresAt), /expected PaceTracker/i);
});

test("rejects non-finite times rather than silently treating them as unexpired", () => {
  assert.throws(() => validateHandoffUrl(link, origin, Number.NaN, expiresAt), /expiry/i);
  assert.throws(() => validateHandoffUrl(link, origin, now, Number.NaN), /expiry/i);
});
