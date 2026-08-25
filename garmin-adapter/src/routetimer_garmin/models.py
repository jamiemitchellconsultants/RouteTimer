from collections.abc import Mapping
from dataclasses import dataclass
from datetime import UTC, datetime
from math import isfinite
from typing import Any, Final, Literal, cast


@dataclass(frozen=True, slots=True)
class AdapterActivity:
    activity_id: str
    name: str
    started_at: datetime
    activity_type: Literal["road-cycling", "gravel-cycling"]
    distance_metres: float | None
    duration_seconds: float | None
    ascent_metres: float | None
    average_power_watts: float | None


@dataclass(frozen=True, slots=True)
class AdapterActivityPage:
    activities: list[AdapterActivity]
    next_offset: int | None
    token_json: str


TYPE_MAP: Final = {
    "road_biking": "road-cycling",
    "gravel_cycling": "gravel-cycling",
}


def map_activity(raw: Mapping[str, Any]) -> AdapterActivity | None:
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
