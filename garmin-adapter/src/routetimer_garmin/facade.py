from collections.abc import Callable, Mapping
from datetime import UTC, datetime
from math import isfinite
from typing import Any, Final, Literal, cast

from garminconnect import Garmin

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
        garmin.client.loads(token_json)
        return TokenSession(garmin)


class TokenSession:
    def __init__(self, garmin: Garmin) -> None:
        self._garmin = garmin

    def dump_tokens(self) -> str:
        return cast(str, self._garmin.client.dumps())


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
