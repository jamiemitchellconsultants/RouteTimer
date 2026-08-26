from __future__ import annotations

import json

from fastapi.testclient import TestClient

from routetimer_garmin.api import app, get_service
from routetimer_garmin.errors import AdapterError


class FakeCourseService:
    async def create_course(self, token_json: str, **kwargs: object) -> object:
        assert token_json == '{"di_token":"a"}'
        assert kwargs["gpx"] == b"<gpx/>"
        assert kwargs["file_name"] == "route.gpx"
        assert kwargs["course_name"] == "Kingston to Dorking"
        assert kwargs["activity_type"] == "road_biking"
        assert kwargs["elevation_gain_metres"] == 17.6
        assert kwargs["elevation_loss_metres"] == 3.2
        return type(
            "CourseResult",
            (),
            {
                "course": type(
                    "CreatedCourse",
                    (),
                    {"course_id": 4242, "course_name": "Kingston to Dorking"},
                )(),
                "token_json": '{"di_token":"rotated"}',
            },
        )()


class RejectingCourseService:
    async def create_course(self, token_json: str, **kwargs: object) -> object:
        raise AdapterError("course-rejected", 422)


def _payload(**overrides: object) -> str:
    body: dict[str, object] = {
        "token": '{"di_token":"a"}',
        "fileName": "route.gpx",
        "courseName": "Kingston to Dorking",
        "activityType": "road_biking",
        "elevationGainMetres": 17.6,
        "elevationLossMetres": 3.2,
    }
    body.update(overrides)
    return json.dumps(body)


def test_course_http_posts_the_gpx_and_returns_the_created_course() -> None:
    app.dependency_overrides[get_service] = FakeCourseService
    try:
        with TestClient(app) as client:
            response = client.post(
                "/v1/courses",
                data={"payload": _payload()},
                files={"file": ("route.gpx", b"<gpx/>", "application/gpx+xml")},
            )
    finally:
        app.dependency_overrides.clear()

    assert response.status_code == 200
    body = response.json()
    assert body["courseId"] == 4242
    assert body["courseName"] == "Kingston to Dorking"
    assert body["tokenJson"] == '{"di_token":"rotated"}'


def test_course_http_translates_an_adapter_error() -> None:
    app.dependency_overrides[get_service] = RejectingCourseService
    try:
        with TestClient(app) as client:
            response = client.post(
                "/v1/courses",
                data={"payload": _payload()},
                files={"file": ("route.gpx", b"<gpx/>", "application/gpx+xml")},
            )
    finally:
        app.dependency_overrides.clear()

    assert response.status_code == 422
    assert response.json()["code"] == "course-rejected"


def test_course_http_rejects_a_gpx_larger_than_fifty_mib() -> None:
    app.dependency_overrides[get_service] = FakeCourseService
    try:
        with TestClient(app) as client:
            response = client.post(
                "/v1/courses",
                data={"payload": _payload()},
                files={"file": ("route.gpx", b"x" * (50 * 1024 * 1024 + 1), "application/gpx+xml")},
            )
    finally:
        app.dependency_overrides.clear()

    assert response.status_code == 400
    assert response.json()["code"] == "request-invalid"
