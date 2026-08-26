from datetime import UTC, datetime

import pytest

import routetimer_garmin.models as models
from routetimer_garmin.facade import _map_activity, _parse_garmin_timestamp


def test_map_activity_accepts_only_road_and_gravel() -> None:
    road = _map_activity(
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
    gravel = _map_activity(
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
            _map_activity(
                {
                    "activityId": 200,
                    "activityName": type_key,
                    "startTimeGMT": "2026-08-23 08:00:00",
                    "activityType": {"typeKey": type_key},
                }
            )
            is None
        )


def test_map_activity_with_permissive_type_still_maps_an_unrecognised_type() -> None:
    # get_activity (the single-activity summary/download path) calls this with
    # require_known_type=False: Garmin's list and single-activity detail endpoints are separate
    # backend services that can disagree on an activity's type for the same activity, and rejecting
    # an already-listed, already-selected activity on the word of the second, less authoritative
    # opinion breaks a legitimate import. list_activities always uses the default (True) and must
    # keep excluding unrecognised types -- that's what decides what's offered for import at all.
    mapped = _map_activity(
        {
            "activityId": 300,
            "activityName": "Disagreeing detail endpoint",
            "startTimeGMT": "2026-08-23 08:00:00",
            "activityType": {"typeKey": "indoor_cycling"},
        },
        require_known_type=False,
    )

    assert mapped is not None
    assert mapped.activity_id == "300"
    assert mapped.activity_type == "indoor_cycling"


def test_map_activity_reads_the_nested_dto_shape_garmins_detail_endpoint_also_returns() -> None:
    # Garmin's single-activity detail endpoint has been observed returning two different shapes
    # for the same ordinary ride depending on internal Garmin routing, not anything about the
    # activity: the flat shape above (also what the list endpoint always uses), and this nested
    # DTO shape, with the type under activityTypeDTO and everything else under summaryDTO -- and,
    # within summaryDTO, average power renamed from avgPower to averagePower and startTimeGMT
    # formatted differently (ISO-ish with fractional seconds, not "%Y-%m-%d %H:%M:%S"). Confirmed
    # directly against a real account: a plain road ride's detail lookup came back in exactly this
    # shape, format quirks included.
    activity = _map_activity(
        {
            "activityId": 400,
            "activityName": "Nested DTO shape ride",
            "activityTypeDTO": {"typeKey": "road_biking"},
            "summaryDTO": {
                "startTimeGMT": "2026-08-25T06:30:00.0",
                "distance": 42000.0,
                "duration": 5400.0,
                "elevationGain": 650.0,
                "averagePower": 215.0,
            },
        }
    )

    assert activity is not None
    assert activity.activity_type == "road-cycling"
    assert activity.started_at == datetime(2026, 8, 25, 6, 30, tzinfo=UTC)
    assert activity.distance_metres == 42000.0
    assert activity.duration_seconds == 5400.0
    assert activity.ascent_metres == 650.0
    assert activity.average_power_watts == 215.0


@pytest.mark.parametrize(
    ("raw_value", "expected"),
    [
        ("2026-08-25 06:30:00", datetime(2026, 8, 25, 6, 30, tzinfo=UTC)),
        ("2026-08-25T06:30:00.0", datetime(2026, 8, 25, 6, 30, tzinfo=UTC)),
        ("2026-08-25T06:30:00.123456", datetime(2026, 8, 25, 6, 30, 0, 123456, tzinfo=UTC)),
    ],
)
def test_parse_garmin_timestamp_accepts_both_observed_formats(
    raw_value: str, expected: datetime
) -> None:
    assert _parse_garmin_timestamp(raw_value) == expected


def test_parse_garmin_timestamp_rejects_a_genuinely_unrecognised_format() -> None:
    with pytest.raises(ValueError, match="did not match any known Garmin format"):
        _parse_garmin_timestamp("not-a-timestamp")


def test_map_activity_normalizes_stable_fields_and_nonfinite_metrics() -> None:
    activity = _map_activity(
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


def test_models_exposes_stable_records_without_a_raw_mapper() -> None:
    assert not hasattr(models, "map_activity")
