from __future__ import annotations

import pytest

from routetimer_garmin.challenges import ChallengeStore
from routetimer_garmin.errors import AdapterError
from routetimer_garmin.facade import CreatedCourse
from routetimer_garmin.service import GarminService


class FakeCourseSession:
    def __init__(self) -> None:
        self.token_json = '{"di_token":"rotated"}'
        self.received: dict[str, object] | None = None
        self.result = CreatedCourse(4242, "Kingston to Dorking", 1234.5, 17.6, 3.2)
        self.error: Exception | None = None

    def create_course(self, **kwargs: object) -> CreatedCourse:
        self.received = kwargs
        if self.error is not None:
            raise self.error
        return self.result

    def dump_tokens(self) -> str:
        return self.token_json


class FakeCourseFacade:
    def __init__(self) -> None:
        self.session = FakeCourseSession()
        self.received_token_json: str | None = None

    def from_tokens(self, token_json: str) -> FakeCourseSession:
        self.received_token_json = token_json
        return self.session


@pytest.fixture
def fake_facade() -> FakeCourseFacade:
    return FakeCourseFacade()


async def test_create_course_returns_the_created_course_and_refreshed_token(
    fake_facade: FakeCourseFacade,
) -> None:
    result = await GarminService(fake_facade, ChallengeStore.system()).create_course(
        '{"di_token":"a"}',
        gpx=b"<gpx/>",
        file_name="route.gpx",
        course_name="Kingston to Dorking",
        activity_type="road_biking",
        description=None,
        elevation_gain_metres=17.6,
        elevation_loss_metres=3.2,
    )

    assert result.course.course_id == 4242
    assert result.token_json == '{"di_token":"rotated"}'
    assert fake_facade.received_token_json == '{"di_token":"a"}'
    assert fake_facade.session.received == {
        "gpx": b"<gpx/>",
        "file_name": "route.gpx",
        "course_name": "Kingston to Dorking",
        "activity_type": "road_biking",
        "description": None,
        "elevation_gain_metres": 17.6,
        "elevation_loss_metres": 3.2,
    }


async def test_create_course_propagates_adapter_errors(fake_facade: FakeCourseFacade) -> None:
    fake_facade.session.error = AdapterError("course-rejected", 422)

    with pytest.raises(AdapterError, match="course-rejected"):
        await GarminService(fake_facade, ChallengeStore.system()).create_course(
            '{"di_token":"a"}',
            gpx=b"<gpx/>",
            file_name="route.gpx",
            course_name="R",
            activity_type="road_biking",
            description=None,
            elevation_gain_metres=0.0,
            elevation_loss_metres=0.0,
        )
