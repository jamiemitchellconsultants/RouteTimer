const HANDOFF_PATH = "/open";

function expectedOriginOf(expectedOrigin) {
  // Parsed rather than string-compared: "https://host" and "https://host/" are the same origin,
  // and a comparison that says otherwise would reject every real link.
  try {
    return new URL(expectedOrigin).origin;
  } catch {
    return null;
  }
}

/**
 * Validates a RoutePacer handoff link against the origin the client was told to expect, entirely
 * in the browser. The server already validated the relay's response, but the check is repeated
 * here because this is the value about to be turned into a QR code a rider points a phone at: a
 * link is only rendered if it is HTTPS, on the expected PaceTracker origin, on the handoff path,
 * and still inside its lifetime.
 *
 * Returns the original string, not a re-serialized URL: the signature covers the exact encoded
 * query, and round-tripping through URL can normalize percent-encoding enough to break it.
 */
export function validateHandoffUrl(url, expectedOrigin, nowMilliseconds, expiresAtMilliseconds) {
  const origin = expectedOriginOf(expectedOrigin);
  if (origin === null) {
    throw new Error("The expected PaceTracker origin is not a valid origin.");
  }

  let parsed;
  try {
    parsed = new URL(url);
  } catch {
    throw new Error("The PaceTracker link is not a valid link.");
  }

  if (parsed.protocol !== "https:") {
    throw new Error("The PaceTracker link must be secure.");
  }

  if (parsed.origin !== origin) {
    throw new Error("The PaceTracker link does not point at the expected PaceTracker site.");
  }

  if (parsed.pathname !== HANDOFF_PATH) {
    throw new Error("The link is not a PaceTracker handoff link.");
  }

  if (!Number.isFinite(nowMilliseconds) || !Number.isFinite(expiresAtMilliseconds)) {
    throw new Error("The PaceTracker link expiry could not be read.");
  }

  if (nowMilliseconds >= expiresAtMilliseconds) {
    throw new Error("The PaceTracker link has expired.");
  }

  return url;
}
