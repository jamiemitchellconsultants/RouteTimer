from __future__ import annotations

from dataclasses import dataclass
from datetime import UTC, datetime

import pytest
from fastapi.testclient import TestClient

from routetimer_garmin.api import app, get_service
from routetimer_garmin.challenges import ChallengeStore
from routetimer_garmin.errors import AdapterError
from routetimer_garmin.facade import GarminFacade
from routetimer_garmin.models import AdapterActivity
from routetimer_garmin.service import GarminService


def activity(activity_id: int, activity_type: str = "road-cycling") -> AdapterActivity:
    return AdapterActivity(
        str(activity_id),
        f"Ride {activity_id}",
        datetime(2026, 8, 25, tzinfo=UTC),
        activity_type,  # type: ignore[arg-type]
        1000.0,
        60.0,
        10.0,
        200.0,
    )


@dataclass
class ActivityBatch:
    activities: list[AdapterActivity]
    source_count: int


class FakeActivitySession:
    def __init__(self) -> None:
        self.batches: dict[int, ActivityBatch] = {}
        self.activity: AdapterActivity | None = activity(123)
        self.token_json = '{"di_token":"rotated"}'
        self.requested_offsets: list[tuple[int, int]] = []
        self.requested_activity_ids: list[str] = []
        self.downloaded_activity_ids: list[str] = []

    def list_activities(self, offset: int, limit: int) -> ActivityBatch:
        self.requested_offsets.append((offset, limit))
        return self.batches[offset]

    def get_activity(self, activity_id: str) -> AdapterActivity | None:
        self.requested_activity_ids.append(activity_id)
        return self.activity

    def download_original(self, activity_id: str) -> bytes:
        self.downloaded_activity_ids.append(activity_id)
        return self.original_download

    def dump_tokens(self) -> str:
        return self.token_json


class FakeActivityFacade:
    def __init__(self) -> None:
        self.session = FakeActivitySession()
        self.received_token_json: str | None = None

    def from_tokens(self, token_json: str) -> FakeActivitySession:
        self.received_token_json = token_json
        return self.session


@pytest.fixture
def fake_facade() -> FakeActivityFacade:
    return FakeActivityFacade()


def test_token_session_converts_raw_garmin_activities_to_stable_records() -> None:
    class Client:
        def loads(self, _: str) -> None:
            return None

        def dumps(self) -> str:
            return '{"di_token":"rotated"}'

    class Garmin:
        def __init__(self) -> None:
            self.client = Client()
            self.offset: int | None = None
            self.limit: int | None = None

        def get_activities(self, offset: int, limit: int) -> list[dict[str, object]]:
            self.offset = offset
            self.limit = limit
            return [
                {
                    "activityId": 123,
                    "activityName": "Road ride",
                    "startTimeGMT": "2026-08-25 06:30:00",
                    "activityType": {"typeKey": "road_biking"},
                },
                {
                    "activityId": 124,
                    "activityName": "Run",
                    "startTimeGMT": "2026-08-25 07:30:00",
                    "activityType": {"typeKey": "running"},
                },
            ]

    garmin = Garmin()
    batch = GarminFacade(lambda: garmin).from_tokens('{"di_token":"a"}').list_activities(50, 50)

    assert garmin.offset == 50
    assert garmin.limit == 50
    assert batch.source_count == 2
    assert [item.activity_id for item in batch.activities] == ["123"]


def test_token_session_rejects_a_non_dictionary_activity_response() -> None:
    class Client:
        def loads(self, _: str) -> None:
            return None

    class Garmin:
        client = Client()

        def get_activities(self, _: int, __: int) -> list[object]:
            return ["not a Garmin activity"]

    session = GarminFacade(Garmin).from_tokens('{"di_token":"a"}')

    with pytest.raises(AdapterError, match="response-invalid"):
        session.list_activities(0, 50)


async def test_activity_page_scans_until_it_fills_fifty_allowed_rows(
    fake_facade: FakeActivityFacade,
) -> None:
    fake_facade.session.batches = {
        0: ActivityBatch([activity(50)], 50),
        50: ActivityBatch([activity(item, "gravel-cycling") for item in range(51, 100)], 50),
    }
    service = GarminService(fake_facade, ChallengeStore.system())

    page = await service.activities('{"di_token":"a"}', offset=0)

    assert len(page.activities) == 50
    assert page.activities[0].activity_id == "50"
    assert page.activities[-1].activity_id == "99"
    assert page.next_offset == 100
    assert page.token_json == '{"di_token":"rotated"}'
    assert '{"di_token":"rotated"}' not in repr(page)
    assert fake_facade.session.requested_offsets == [(0, 50), (50, 50)]


async def test_activity_page_stops_after_ten_garmin_pages(
    fake_facade: FakeActivityFacade,
) -> None:
    fake_facade.session.batches = {offset: ActivityBatch([], 50) for offset in range(0, 500, 50)}

    page = await GarminService(fake_facade, ChallengeStore.system()).activities(
        '{"di_token":"a"}', offset=0
    )

    assert page.activities == []
    assert page.next_offset == 500
    assert fake_facade.session.requested_offsets == [(offset, 50) for offset in range(0, 500, 50)]


async def test_activity_page_omits_next_offset_after_a_short_final_page(
    fake_facade: FakeActivityFacade,
) -> None:
    fake_facade.session.batches = {0: ActivityBatch([activity(10)], 1)}

    page = await GarminService(fake_facade, ChallengeStore.system()).activities(
        '{"di_token":"a"}', offset=0
    )

    assert [item.activity_id for item in page.activities] == ["10"]
    assert page.next_offset is None


@pytest.mark.parametrize("activity_id", ["0", "-1", "01", "1.0", "one"])
async def test_summary_rejects_noncanonical_or_nonpositive_ids_before_garmin_calls(
    fake_facade: FakeActivityFacade, activity_id: str
) -> None:
    service = GarminService(fake_facade, ChallengeStore.system())

    with pytest.raises(AdapterError, match="request-invalid"):
        await service.activity_summary('{"di_token":"a"}', activity_id)

    assert fake_facade.received_token_json is None
    assert fake_facade.session.requested_activity_ids == []


async def test_summary_rechecks_a_single_allowed_activity_and_returns_rotated_tokens(
    fake_facade: FakeActivityFacade,
) -> None:
    fake_facade.session.activity = activity(123, "gravel-cycling")

    result = await GarminService(fake_facade, ChallengeStore.system()).activity_summary(
        '{"di_token":"a"}', "123"
    )

    assert result.activity.activity_id == "123"
    assert result.activity.activity_type == "gravel-cycling"
    assert result.token_json == '{"di_token":"rotated"}'
    assert fake_facade.session.requested_activity_ids == ["123"]


async def test_summary_rejects_a_valid_but_disallowed_garmin_activity(
    fake_facade: FakeActivityFacade,
) -> None:
    fake_facade.session.activity = None

    with pytest.raises(AdapterError, match="activity-not-allowed") as raised:
        await GarminService(fake_facade, ChallengeStore.system()).activity_summary(
            '{"di_token":"a"}', "123"
        )

    assert raised.value.status_code == 422


class FakeSummaryService:
    def __init__(self) -> None:
        self.activity = activity(123)
        self.token_json = '{"di_token":"rotated"}'

    async def activity_summary(self, token_json: str, activity_id: str) -> object:
        assert token_json == '{"di_token":"a"}'
        assert activity_id == "123"
        return type("Summary", (), {"activity": self.activity, "token_json": self.token_json})()


class RejectingPageService:
    async def activities(self, *_: object) -> object:
        raise AssertionError("a non-integer offset must be rejected before the service")


def test_activity_page_http_rejects_a_non_integer_offset() -> None:
    app.dependency_overrides[get_service] = RejectingPageService
    try:
        with TestClient(app, raise_server_exceptions=False) as client:
            response = client.post(
                "/v1/activities/page", json={"token": '{"di_token":"a"}', "offset": "0"}
            )
    finally:
        app.dependency_overrides.clear()

    assert response.status_code == 422
    assert response.json() == {"code": "request-invalid", "detail": "request-invalid"}


def test_activity_summary_http_returns_canonical_activity_and_token() -> None:
    app.dependency_overrides[get_service] = FakeSummaryService
    try:
        with TestClient(app) as client:
            response = client.post("/v1/activities/123/summary", json={"token": '{"di_token":"a"}'})
    finally:
        app.dependency_overrides.clear()

    assert response.status_code == 200
    assert response.json() == {
        "activity": {
            "activityId": "123",
            "name": "Ride 123",
            "startedAt": "2026-08-25T00:00:00Z",
            "activityType": "road-cycling",
            "distanceMetres": 1000.0,
            "durationSeconds": 60.0,
            "ascentMetres": 10.0,
            "averagePowerWatts": 200.0,
        },
        "tokenJson": '{"di_token":"rotated"}',
    }
