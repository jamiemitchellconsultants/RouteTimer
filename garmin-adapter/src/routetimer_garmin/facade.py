import logging
from collections.abc import Callable, Mapping
from dataclasses import dataclass, field
from datetime import UTC, datetime
from math import isfinite
from typing import Any, Final, Literal, cast

from garminconnect import Garmin
from garminconnect.exceptions import (
    GarminConnectAuthenticationError,
    GarminConnectConnectionError,
    GarminConnectTooManyRequestsError,
)

from routetimer_garmin.errors import AdapterError
from routetimer_garmin.models import AdapterActivity, AdapterActivityBatch

logger = logging.getLogger(__name__)

TYPE_MAP: Final = {
    "road_biking": "road-cycling",
    "gravel_cycling": "gravel-cycling",
}


class GarminFacade:
    def __init__(self, factory: Callable[..., Garmin] = Garmin) -> None:
        self._factory = factory

    def from_tokens(self, token_json: str) -> "TokenSession":
        try:
            garmin = self._factory()
            garmin.client.loads(token_json)
        except Exception as error:
            raise _translate_error(error, "authentication") from None
        return TokenSession(garmin)

    def start_login(self, email: str, password: str) -> "CompletedLogin | PendingLogin":
        try:
            garmin = self._factory(email, password, return_on_mfa=True)
            try:
                needs_mfa, _ = garmin.login()
            finally:
                garmin.username = None
                garmin.password = None
        except Exception as error:
            raise _translate_error(error, "credentials-rejected") from None
        if needs_mfa == "needs_mfa":
            return PendingLogin(garmin)
        if needs_mfa is not None:
            raise AdapterError("response-invalid", 502)
        return _completed_login(garmin, "credentials-rejected")


@dataclass(frozen=True, slots=True)
class CompletedLogin:
    garmin_user_id: str | None
    display_name: str | None
    token_json: str = field(repr=False)


class PendingLogin:
    def __init__(self, garmin: Garmin) -> None:
        self._garmin: Garmin | None = garmin

    def resume(self, code: str) -> CompletedLogin:
        garmin = self._garmin
        if garmin is None:
            raise AdapterError("challenge-expired", 409)
        try:
            garmin.resume_login({}, code)
            return _completed_login(garmin, "mfa-invalid")
        except Exception as error:
            raise _translate_error(error, "mfa-invalid") from None

    def close(self) -> None:
        self._garmin = None


class TokenSession:
    def __init__(self, garmin: Garmin) -> None:
        self._garmin = garmin

    def dump_tokens(self) -> str:
        try:
            return cast(str, self._garmin.client.dumps())
        except Exception as error:
            raise _translate_error(error, "authentication") from None

    def validate(self) -> CompletedLogin:
        try:
            profile = self._garmin.client.connectapi("/userprofile-service/socialProfile")
            user_id, display_name = _profile_identity(self._garmin, profile)
            return CompletedLogin(user_id, display_name, self.dump_tokens())
        except Exception as error:
            raise _translate_error(error, "authentication") from None

    def list_activities(self, offset: int, limit: int) -> AdapterActivityBatch:
        try:
            raw_activities = self._garmin.get_activities(offset, limit)
        except Exception as error:
            raise _translate_error(error, "authentication") from None
        if not isinstance(raw_activities, list):
            raise AdapterError("response-invalid", 502)
        try:
            if not all(isinstance(raw_activity, Mapping) for raw_activity in raw_activities):
                raise AdapterError("response-invalid", 502)
            activities = [
                activity
                for raw_activity in raw_activities
                if (activity := _map_activity(raw_activity)) is not None
            ]
        except AdapterError:
            raise
        except Exception:
            raise AdapterError("response-invalid", 502) from None
        return AdapterActivityBatch(activities, len(raw_activities))

    def get_activity(self, activity_id: str) -> AdapterActivity | None:
        try:
            raw_activity = self._garmin.get_activity(activity_id)
        except Exception as error:
            raise _translate_error(error, "authentication") from None
        if not isinstance(raw_activity, Mapping):
            # Field names/exception text only -- never the raw Garmin payload or its values.
            logger.warning("get_activity: Garmin returned a non-mapping response, type=%s", type(raw_activity))
            raise AdapterError("response-invalid", 502)
        try:
            return _map_activity(raw_activity, require_known_type=False)
        except Exception as error:
            # Field names and exception text only -- never distance/time/power/location values.
            # _map_activity already handles the two response shapes Garmin has been observed
            # returning for this endpoint; if this still fires, it's a third one worth knowing
            # the top-level shape of, not the flat/nested cases already handled above.
            logger.warning(
                "get_activity: mapping the Garmin response raised %s: %s; keys present=%s",
                type(error).__name__, error, sorted(raw_activity.keys()),
            )
            raise AdapterError("response-invalid", 502) from None

    def download_original(self, activity_id: str) -> bytes:
        try:
            content = self._garmin.download_activity(
                activity_id, Garmin.ActivityDownloadFormat.ORIGINAL
            )
        except Exception as error:
            raise _translate_error(error, "authentication") from None
        if not isinstance(content, bytes):
            raise AdapterError("response-invalid", 502)
        return content


def _completed_login(garmin: Garmin, authentication_code: str) -> CompletedLogin:
    try:
        profile = garmin.client.connectapi("/userprofile-service/socialProfile")
        user_id, display_name = _profile_identity(garmin, profile)
        return CompletedLogin(user_id, display_name, cast(str, garmin.client.dumps()))
    except Exception as error:
        raise _translate_error(error, authentication_code) from None


def _profile_identity(garmin: Garmin, profile: object) -> tuple[str | None, str | None]:
    if not isinstance(profile, Mapping):
        raise AdapterError("response-invalid", 502)
    user_id = _first_value(profile, "profileId", "id", "userId")
    display_name = _first_value(profile, "fullName", "displayName")
    if display_name is None:
        display_name = garmin.get_full_name() or garmin.display_name
    return user_id, display_name


def _first_value(profile: Mapping[object, object], *keys: str) -> str | None:
    for key in keys:
        value = profile.get(key)
        if value is not None and str(value).strip():
            return str(value).strip()
    return None


def _translate_error(error: Exception, authentication_code: str) -> AdapterError:
    if isinstance(error, AdapterError):
        return error
    if isinstance(error, GarminConnectAuthenticationError):
        return AdapterError(
            authentication_code, 400 if authentication_code != "authentication" else 401
        )
    if isinstance(error, GarminConnectTooManyRequestsError):
        return AdapterError("rate-limited", 429)
    if isinstance(error, GarminConnectConnectionError):
        return AdapterError("unavailable", 503)
    return AdapterError("unavailable", 503)


def _map_activity(raw: Mapping[str, Any], *, require_known_type: bool = True) -> AdapterActivity | None:
    # Garmin's single-activity detail endpoint has been observed returning two different shapes for
    # an ordinary ride, seemingly depending on internal Garmin backend routing rather than anything
    # about the activity itself: a flat shape (also what the list endpoint always uses, with
    # activityType/startTimeGMT/distance/... at the top level) and a nested DTO shape, with the same
    # information under activityTypeDTO and summaryDTO instead -- and, within summaryDTO, average
    # power renamed from avgPower to averagePower. Both are handled here so a rider isn't at the
    # mercy of which shape Garmin happens to answer with for a given request.
    type_source = raw.get("activityTypeDTO", raw.get("activityType", {}))
    garmin_type = str(type_source.get("typeKey", "")) if isinstance(type_source, Mapping) else ""
    canonical = TYPE_MAP.get(garmin_type)
    if canonical is None:
        # list_activities calls this with the default (require_known_type=True): Garmin's list
        # endpoint is the one place a not-yet-imported activity's type gets decided, so an
        # unrecognised type there means "don't show this as importable" -- correct to exclude it.
        # get_activity calls this with require_known_type=False: Garmin's list and single-activity
        # detail endpoints are separate backend services and can disagree on typeKey for the same
        # activity (observed directly: an activity the list reported as road_biking came back with
        # a different, unmapped typeKey from the detail endpoint days later, with no indication the
        # rider had ever reclassified it). Rejecting the already-selected, already-listed activity
        # on a second, independent opinion just breaks a legitimate import; the canonical type this
        # dataclass carries is not read for anything past this point once permissively mapped, so
        # echoing Garmin's own raw label back here (rather than fabricating one of the two allowed
        # values) is the honest choice, not a meaningful one.
        if require_known_type:
            return None
        canonical = garmin_type

    summary = raw.get("summaryDTO", raw)
    if not isinstance(summary, Mapping):
        summary = raw

    started_at = _parse_garmin_timestamp(summary["startTimeGMT"])
    average_power = summary.get("avgPower", summary.get("averagePower"))
    return AdapterActivity(
        activity_id=str(int(raw["activityId"])),
        name=str(raw.get("activityName") or f"Garmin {raw['activityId']}").strip(),
        started_at=started_at,
        activity_type=cast(Literal["road-cycling", "gravel-cycling"], canonical),
        distance_metres=_optional_finite(summary.get("distance")),
        duration_seconds=_optional_finite(summary.get("duration")),
        ascent_metres=_optional_finite(summary.get("elevationGain")),
        average_power_watts=_optional_finite(average_power),
    )


_START_TIME_FORMATS: Final = ("%Y-%m-%d %H:%M:%S", "%Y-%m-%dT%H:%M:%S.%f")


def _parse_garmin_timestamp(value: object) -> datetime:
    # The flat and nested-DTO response shapes format startTimeGMT differently -- confirmed
    # directly: "2026-08-25 06:30:00" from the flat shape, "2025-10-01T12:15:37.0" (ISO-ish,
    # fractional seconds) from the nested one. Try both rather than assuming the shape that
    # decided which format also decided which parse this call needs.
    text = str(value)
    for time_format in _START_TIME_FORMATS:
        try:
            return datetime.strptime(text, time_format).replace(tzinfo=UTC)
        except ValueError:
            continue
    raise ValueError(f"startTimeGMT {text!r} did not match any known Garmin format")


def _optional_finite(value: Any) -> float | None:
    try:
        result = float(value)
    except (TypeError, ValueError):
        return None
    return result if isfinite(result) else None
