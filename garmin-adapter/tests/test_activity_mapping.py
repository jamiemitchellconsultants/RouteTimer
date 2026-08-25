from datetime import UTC, datetime

from routetimer_garmin.models import map_activity


def test_map_activity_accepts_only_road_and_gravel() -> None:
    road = map_activity(
        {
            "activityId": 101,
            "activityName": "Road ride",
            "startTimeGMT": "2026-08-25 06:30:00",
            "activityType": {"typeKey": "road_biking"},
            "distance": 42000.0,
            "duration": 5400.0,
            "elevationGain": 650.0,
            "avgPower": 215.0,
        }
    )
    gravel = map_activity(
        {
            "activityId": 102,
            "activityName": "Gravel ride",
            "startTimeGMT": "2026-08-24 07:00:00",
            "activityType": {"typeKey": "gravel_cycling"},
        }
    )

    assert road is not None and road.activity_type == "road-cycling"
    assert gravel is not None and gravel.activity_type == "gravel-cycling"
    for type_key in ("indoor_cycling", "e_bike_fitness", "mountain_biking", "running"):
        assert (
            map_activity(
                {
                    "activityId": 200,
                    "activityName": type_key,
                    "startTimeGMT": "2026-08-23 08:00:00",
                    "activityType": {"typeKey": type_key},
                }
            )
            is None
        )


def test_map_activity_normalizes_stable_fields_and_nonfinite_metrics() -> None:
    activity = map_activity(
        {
            "activityId": 103.0,
            "activityName": None,
            "startTimeGMT": "2026-08-25 08:15:00",
            "activityType": {"typeKey": "road_biking"},
            "distance": "1200.5",
            "duration": float("inf"),
            "elevationGain": "not-a-number",
            "avgPower": None,
        }
    )

    assert activity is not None
    assert activity.activity_id == "103"
    assert activity.name == "Garmin 103.0"
    assert activity.started_at == datetime(2026, 8, 25, 8, 15, tzinfo=UTC)
    assert activity.distance_metres == 1200.5
    assert activity.duration_seconds is None
    assert activity.ascent_metres is None
    assert activity.average_power_watts is None
