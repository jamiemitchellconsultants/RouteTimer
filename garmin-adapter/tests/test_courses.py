import pytest

from routetimer_garmin.courses import (
    ACTIVITY_TYPE_IDS,
    CoursePayloadError,
    build_course_payload,
    initial_bearing,
    haversine_metres,
)
from routetimer_garmin.errors import AdapterError
from routetimer_garmin.facade import TokenSession


def _points():
    return [
        {"latitude": 51.4085, "longitude": -0.3064, "elevation": 12.4},
        {"latitude": 51.4090, "longitude": -0.3070, "elevation": 15.0},
        {"latitude": 51.4100, "longitude": -0.3080, "elevation": 30.0},
    ]


def test_haversine_matches_a_known_separation():
    north = {"latitude": 51.0, "longitude": 0.0}
    south = {"latitude": 50.0, "longitude": 0.0}
    assert haversine_metres(north, south) == pytest.approx(111_195, rel=0.01)


def test_initial_bearing_due_east_is_ninety_degrees():
    west = {"latitude": 0.0, "longitude": 0.0}
    east = {"latitude": 0.0, "longitude": 1.0}
    assert initial_bearing(west, east) == pytest.approx(90.0, abs=0.1)


def test_payload_carries_cumulative_distance_and_a_total():
    payload = build_course_payload(
        {"geoPoints": _points()},
        course_name="Kingston to Dorking",
        activity_type_id=ACTIVITY_TYPE_IDS["road_biking"],
        description=None,
        elevation_gain_metres=17.6,
        elevation_loss_metres=0.0,
    )

    points = payload["geoPoints"]
    assert points[0]["distance"] == 0.0
    assert points[1]["distance"] > 0
    assert points[2]["distance"] > points[1]["distance"]
    assert payload["distanceMeter"] == pytest.approx(points[2]["distance"])
    assert payload["courseLines"][0]["numberOfPoints"] == 3
    assert payload["courseLines"][0]["distanceInMeters"] == payload["distanceMeter"]


def test_payload_sends_our_own_elevation_totals():
    payload = build_course_payload(
        {"geoPoints": _points()},
        course_name="Kingston to Dorking",
        activity_type_id=10,
        description=None,
        elevation_gain_metres=17.6,
        elevation_loss_metres=3.2,
    )

    assert payload["elevationGainMeter"] == 17.6
    assert payload["elevationLossMeter"] == 3.2


def test_payload_bounding_box_spans_the_points():
    payload = build_course_payload(
        {"geoPoints": _points()},
        course_name="R",
        activity_type_id=10,
        description=None,
        elevation_gain_metres=0.0,
        elevation_loss_metres=0.0,
    )

    box = payload["boundingBox"]
    assert box["lowerLeft"]["latitude"] == 51.4085
    assert box["upperRight"]["latitude"] == 51.4100
    assert box["center"]["latitude"] == pytest.approx((51.4085 + 51.4100) / 2)


def test_payload_defaults_missing_elevation_to_zero():
    payload = build_course_payload(
        {"geoPoints": [{"latitude": 51.0, "longitude": 0.0}, {"latitude": 51.1, "longitude": 0.1}]},
        course_name="R",
        activity_type_id=10,
        description=None,
        elevation_gain_metres=0.0,
        elevation_loss_metres=0.0,
    )

    assert all(point["elevation"] == 0.0 for point in payload["geoPoints"])


@pytest.mark.parametrize("parsed", [{"geoPoints": []}, {"geoPoints": [{"latitude": 1.0, "longitude": 1.0}]}, {}])
def test_rejects_fewer_than_two_points(parsed):
    with pytest.raises(CoursePayloadError):
        build_course_payload(
            parsed,
            course_name="R",
            activity_type_id=10,
            description=None,
            elevation_gain_metres=0.0,
            elevation_loss_metres=0.0,
        )


class _FakeGarminClient:
    def __init__(self, parsed, saved):
        self._parsed = parsed
        self._saved = saved
        self.calls = []

    def post(self, service, path, **kwargs):
        self.calls.append((path, kwargs))
        if path == "/course-service/course/import":
            return self._parsed
        if path == "/course-service/course":
            return self._saved
        raise AssertionError(f"unexpected path {path}")


class _FakeGarmin:
    def __init__(self, client):
        self.client = client


def test_create_course_posts_the_gpx_then_saves():
    client = _FakeGarminClient(
        parsed={"courseName": "Parsed name", "geoPoints": _points()},
        saved={"courseId": 4242, "courseName": "Kingston to Dorking", "distanceMeter": 1234.5},
    )
    session = TokenSession(_FakeGarmin(client))

    result = session.create_course(
        gpx=b"<gpx/>",
        file_name="route.gpx",
        course_name="Kingston to Dorking",
        activity_type="road_biking",
        description=None,
        elevation_gain_metres=17.6,
        elevation_loss_metres=3.2,
    )

    assert result.course_id == 4242
    assert [call[0] for call in client.calls] == [
        "/course-service/course/import",
        "/course-service/course",
    ]
    assert client.calls[0][1]["files"]["file"][2] == "application/gpx+xml"
    assert client.calls[1][1]["json"]["elevationGainMeter"] == 17.6


def test_create_course_rejects_an_unknown_activity_type():
    session = TokenSession(_FakeGarmin(_FakeGarminClient({"geoPoints": _points()}, {"courseId": 1})))

    with pytest.raises(AdapterError) as raised:
        session.create_course(
            gpx=b"<gpx/>",
            file_name="route.gpx",
            course_name="R",
            activity_type="unicycling",
            description=None,
            elevation_gain_metres=0.0,
            elevation_loss_metres=0.0,
        )

    assert raised.value.code == "request-invalid"
