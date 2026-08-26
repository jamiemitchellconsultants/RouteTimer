let apiPromise = null;
let scriptElement = null;
let authFailureMessage = null;
let errorsCaptured = false;

const originalConsoleError = console.error;
const originalGmAuthFailure = window.gm_authFailure;

const AUTH_ERROR_PATTERN =
    /MapError|ApiNotActivated|RefererNotAllowed|InvalidKey|BillingNotEnabled|ApiProjectMapError|ApiTargetBlockedMapError/;

function report(logger, level, message, detail) {
    if (!logger) return;
    logger.invokeMethodAsync('LogFromJs', level, String(message), detail ? String(detail) : null)
        .catch(() => {});
}

// Mirrors KeyRedactor.Mask on the C# side.
function mask(key) {
    return (typeof key === 'string' && key.length >= 8)
        ? key.slice(0, 4) + '\u2026' + key.slice(-4)
        : '\u2026';
}

// Google reports key problems by writing to the console and calling window.gm_authFailure,
// not by rejecting any promise we hold. Capturing both is the only way to surface Google's
// own wording instead of a generic failure.
function captureGoogleErrors(logger) {
    if (errorsCaptured) return;
    errorsCaptured = true;

    window.gm_authFailure = () => {
        authFailureMessage =
            'Google rejected the API key (gm_authFailure). Common causes: the key is invalid, ' +
            'it is restricted to a different HTTP referrer, billing is not enabled on its project, ' +
            'or one of the three required products is not enabled - Maps JavaScript API, ' +
            'Directions API, Elevation API.';
        report(logger, 'Error', authFailureMessage);
    };

    console.error = function (...args) {
        const text = args.map(a => (a && a.message) ? a.message : String(a)).join(' ');
        if (AUTH_ERROR_PATTERN.test(text)) {
            authFailureMessage = text;
            report(logger, 'Error', 'Google reported: ' + text);
        }
        originalConsoleError.apply(console, args);
    };
}

// Restores console.error and window.gm_authFailure to their pre-capture state. Does not
// touch apiPromise or authFailureMessage: the Maps API can only be loaded once per page,
// and a route that already failed with an auth error should keep failing the same way.
export function teardown() {
    if (!errorsCaptured) return;

    console.error = originalConsoleError;

    if (originalGmAuthFailure === undefined) {
        delete window.gm_authFailure;
    } else {
        window.gm_authFailure = originalGmAuthFailure;
    }

    errorsCaptured = false;
}

export function loadApi(key, logger) {
    if (apiPromise) {
        report(logger, 'Warn',
            'The Maps JavaScript API is already loaded in this page.',
            'It can only be loaded once per page, so a different key cannot take effect until you press Reset.');
        return apiPromise;
    }

    captureGoogleErrors(logger);

    const loaderPromise = new Promise((resolve, reject) => {
        const callbackName = '__mapToGarminApiReady';

        window[callbackName] = () => {
            delete window[callbackName];
            report(logger, 'Success', 'Maps JavaScript API loaded.');
            resolve(true);
        };

        scriptElement = document.createElement('script');
        scriptElement.async = true;
        scriptElement.src =
            'https://maps.googleapis.com/maps/api/js' +
            '?key=' + encodeURIComponent(key) +
            '&libraries=geometry' +
            '&loading=async' +
            '&callback=' + callbackName;

        scriptElement.onerror = () =>
            reject(new Error('Network error loading the Maps JavaScript API from maps.googleapis.com.'));

        // The key is masked here as well as in ActionLog. Redaction downstream is the
        // guarantee; masking at the source means a redaction regression still cannot leak it.
        report(logger, 'Info', 'Injecting the Maps JavaScript API loader script.',
            'https://maps.googleapis.com/maps/api/js?key=' + mask(key) + '&libraries=geometry&loading=async');

        document.head.appendChild(scriptElement);
    });

    // A script that returns 200 but evaluates to nothing (corporate proxies, content filters)
    // fires neither the callback nor onerror, so loaderPromise never settles on its own. The
    // timeout is cleared as soon as the race settles, so a load that succeeds just under the
    // wire cannot leave a stray rejection to surface later.
    const LOAD_TIMEOUT_MS = 25000;
    let timeoutHandle;
    const timeoutPromise = new Promise((_, reject) => {
        timeoutHandle = setTimeout(() => reject(new Error(
            'The Maps JavaScript API did not finish loading within 25 seconds. Check that ' +
            'maps.googleapis.com is reachable and not blocked by a browser extension or network ' +
            'filter, then press Reset and try again.')), LOAD_TIMEOUT_MS);
    });

    apiPromise = Promise.race([loaderPromise, timeoutPromise])
        .finally(() => clearTimeout(timeoutHandle));

    return apiPromise;
}

function toLocation(waypoint) {
    return waypoint.name !== null && waypoint.name !== undefined
        ? waypoint.name
        : { lat: waypoint.lat, lng: waypoint.lng };
}

export async function route(request, logger) {
    if (authFailureMessage) throw new Error(authFailureMessage);

    const service = new google.maps.DirectionsService();
    const travelMode = google.maps.TravelMode[request.mode];

    report(logger, 'Info',
        `Requesting directions: ${request.intermediates.length} intermediate waypoint(s), mode ${request.mode}.`);

    let response;
    try {
        response = await service.route({
            origin: toLocation(request.origin),
            destination: toLocation(request.destination),
            waypoints: request.intermediates.map(w => ({ location: toLocation(w), stopover: true })),
            travelMode: travelMode,
            provideRouteAlternatives: false
        });
    } catch (e) {
        const code = e && e.code ? e.code : 'UNKNOWN';
        const text = e && e.message ? e.message : String(e);

        if (code === 'ZERO_RESULTS') {
            throw new Error(
                `DirectionsService returned ZERO_RESULTS: ${text}. Google could not connect every ` +
                `waypoint by ${request.mode}. Check the waypoint list logged above - one pair has no ` +
                `route in this mode. Google does not say which.`);
        }

        if (code === 'OVER_QUERY_LIMIT') {
            throw new Error(
                `DirectionsService returned OVER_QUERY_LIMIT: ${text}. Your key has hit its rate or ` +
                `quota limit. This is a limit on your own Google project, not on this app.`);
        }

        if (code === 'REQUEST_DENIED') {
            throw new Error(
                `DirectionsService returned REQUEST_DENIED: ${text}. Your key's Google Cloud project ` +
                `needs all three products enabled - Maps JavaScript API, Directions API, Elevation API - ` +
                `and billing enabled on that project. If the key is restricted to specific HTTP referrers, ` +
                `it must also allow this page's origin, logged above.`);
        }

        if (code === 'NOT_FOUND') {
            throw new Error(
                `DirectionsService returned NOT_FOUND: ${text}. Google could not geocode one of the ` +
                `place names in this route. Check the waypoint names logged above.`);
        }

        throw new Error(`DirectionsService returned ${code}: ${text}`);
    }

    if (authFailureMessage) throw new Error(authFailureMessage);

    if (!response.routes || response.routes.length === 0) {
        throw new Error('DirectionsService returned no routes.');
    }

    const chosen = response.routes[0];
    const path = [];
    let previous = null;

    // step.path, not overview_path: the overview is simplified and would cut corners.
    for (const leg of chosen.legs) {
        for (const step of leg.steps) {
            for (const point of step.path) {
                const lat = point.lat();
                const lng = point.lng();
                if (previous && previous[0] === lat && previous[1] === lng) continue;
                previous = [lat, lng];
                path.push(previous);
            }
        }
    }

    const waypoints = [];
    chosen.legs.forEach((leg, index) => {
        if (index === 0) {
            waypoints.push({ lat: leg.start_location.lat(), lng: leg.start_location.lng(), name: leg.start_address });
        }
        waypoints.push({ lat: leg.end_location.lat(), lng: leg.end_location.lng(), name: leg.end_address });
    });

    const distanceMeters = chosen.legs.reduce((sum, l) => sum + (l.distance ? l.distance.value : 0), 0);
    const durationSeconds = chosen.legs.reduce((sum, l) => sum + (l.duration ? l.duration.value : 0), 0);

    report(logger, 'Success',
        `Directions returned ${chosen.legs.length} leg(s) and ${path.length} track point(s).`);

    return {
        path: path,
        waypoints: waypoints,
        distanceMeters: distanceMeters,
        durationSeconds: durationSeconds,
        legSummaries: chosen.legs.map(l =>
            `${l.start_address} to ${l.end_address}: ${l.distance ? l.distance.text : '?'} / ${l.duration ? l.duration.text : '?'}`)
    };
}

// getElevationForLocations, never getElevationAlongPath: the latter resamples to evenly
// spaced points and would move the track off the routed geometry.
export async function elevate(points, batchSize, logger) {
    const service = new google.maps.ElevationService();
    const elevations = [];
    const batches = Math.ceil(points.length / batchSize);

    report(logger, 'Info',
        `Requesting elevation for ${points.length} point(s) in ${batches} batch(es) of up to ${batchSize}.`,
        'These calls bill against your own key and quota.');

    for (let i = 0; i < points.length; i += batchSize) {
        const batch = points.slice(i, i + batchSize);
        const response = await service.getElevationForLocations({
            locations: batch.map(p => ({ lat: p[0], lng: p[1] }))
        });

        for (const result of response.results) elevations.push(result.elevation);

        report(logger, 'Info',
            `Elevation batch ${Math.floor(i / batchSize) + 1} of ${batches} complete (${elevations.length}/${points.length}).`);
    }

    return elevations;
}

export function scrub() {
    if (scriptElement && scriptElement.parentNode) {
        scriptElement.parentNode.removeChild(scriptElement);
    }
    scriptElement = null;
}
