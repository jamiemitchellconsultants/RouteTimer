from dataclasses import dataclass
from datetime import datetime
from typing import Literal


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
