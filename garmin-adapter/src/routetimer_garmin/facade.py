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
from routetimer_garmin.models import AdapterActivity


TYPE_MAP: Final = {
    "road_biking": "road-cycling",
    "gravel_cycling": "gravel-cycling",
}


class GarminFacade:
    def __init__(self, factory: Callable[..., Garmin] = Garmin) -> None:
        self._factory = factory

    def from_tokens(self, token_json: str) -> "TokenSession":
        garmin = self._factory()
        try:
            garmin.client.loads(token_json)
        except Exception as error:
            raise _translate_error(error, "authentication") from None
        return TokenSession(garmin)

    def start_login(self, email: str, password: str) -> "CompletedLogin | PendingLogin":
        garmin = self._factory(email, password, return_on_mfa=True)
        try:
            needs_mfa, _ = garmin.login()
        except Exception as error:
            raise _translate_error(error, "credentials-rejected") from None
        finally:
            garmin.username = None
            garmin.password = None
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
        return cast(str, self._garmin.client.dumps())

    def validate(self) -> CompletedLogin:
        try:
            profile = self._garmin.client.connectapi("/userprofile-service/socialProfile")
        except Exception as error:
            raise _translate_error(error, "authentication") from None
        user_id, display_name = _profile_identity(self._garmin, profile)
        return CompletedLogin(user_id, display_name, self.dump_tokens())


def _completed_login(garmin: Garmin, authentication_code: str) -> CompletedLogin:
    try:
        profile = garmin.client.connectapi("/userprofile-service/socialProfile")
    except Exception as error:
        raise _translate_error(error, authentication_code) from None
    user_id, display_name = _profile_identity(garmin, profile)
    return CompletedLogin(user_id, display_name, cast(str, garmin.client.dumps()))


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


def _map_activity(raw: Mapping[str, Any]) -> AdapterActivity | None:
    garmin_type = str(raw.get("activityType", {}).get("typeKey", ""))
    canonical = TYPE_MAP.get(garmin_type)
    if canonical is None:
        return None

    started_at = datetime.strptime(str(raw["startTimeGMT"]), "%Y-%m-%d %H:%M:%S").replace(
        tzinfo=UTC
    )
    return AdapterActivity(
        activity_id=str(int(raw["activityId"])),
        name=str(raw.get("activityName") or f"Garmin {raw['activityId']}").strip(),
        started_at=started_at,
        activity_type=cast(Literal["road-cycling", "gravel-cycling"], canonical),
        distance_metres=_optional_finite(raw.get("distance")),
        duration_seconds=_optional_finite(raw.get("duration")),
        ascent_metres=_optional_finite(raw.get("elevationGain")),
        average_power_watts=_optional_finite(raw.get("avgPower")),
    )


def _optional_finite(value: Any) -> float | None:
    try:
        result = float(value)
    except (TypeError, ValueError):
        return None
    return result if isfinite(result) else None
