import pytest

from routetimer_garmin.facade import GarminFacade


class FakeClient:
    def __init__(self) -> None:
        self.loaded_json: str | None = None
        self.loaded_path: str | None = None

    def loads(self, token_json: str) -> None:
        self.loaded_json = token_json

    def dumps(self) -> str:
        assert self.loaded_json is not None
        return self.loaded_json


class FakeGarmin:
    def __init__(self) -> None:
        self.client = FakeClient()
        self.raw_activity: dict[str, object] = {}

    def get_activity(self, activity_id: str) -> dict[str, object]:
        return self.raw_activity


class FakeGarminFactory:
    def __init__(self) -> None:
        self.created: list[FakeGarmin] = []

    def __call__(self) -> FakeGarmin:
        garmin = FakeGarmin()
        self.created.append(garmin)
        return garmin


@pytest.fixture
def fake_garmin_factory() -> FakeGarminFactory:
    return FakeGarminFactory()


def test_facade_loads_and_returns_tokens_without_writing_files(
    fake_garmin_factory: FakeGarminFactory,
) -> None:
    facade = GarminFacade(fake_garmin_factory)
    session = facade.from_tokens('{"di_token":"a","di_refresh_token":"b","di_client_id":"c"}')

    assert session.dump_tokens() == '{"di_token":"a","di_refresh_token":"b","di_client_id":"c"}'
    assert fake_garmin_factory.created[0].client.loaded_json is not None
    assert fake_garmin_factory.created[0].client.loaded_path is None


def test_token_session_does_not_expose_the_garmin_client(
    fake_garmin_factory: FakeGarminFactory,
) -> None:
    session = GarminFacade(fake_garmin_factory).from_tokens('{"di_token":"a"}')

    assert not hasattr(session, "garmin")


def test_get_activity_maps_an_unrecognised_type_instead_of_dropping_it(
    fake_garmin_factory: FakeGarminFactory,
) -> None:
    # Garmin's list and single-activity detail endpoints are separate backend services that can
    # disagree on an activity's type for the same activity -- observed directly against a real
    # account. get_activity (used only for the summary/download re-check of an activity the rider
    # already selected from the list) must map it anyway rather than reporting "not found", unlike
    # list_activities, which still excludes an unrecognised type from what's offered for import.
    session = GarminFacade(fake_garmin_factory).from_tokens('{"di_token":"a"}')
    fake_garmin_factory.created[0].raw_activity = {
        "activityId": 300,
        "activityName": "Disagreeing detail endpoint",
        "startTimeGMT": "2026-08-23 08:00:00",
        "activityType": {"typeKey": "indoor_cycling"},
    }

    mapped = session.get_activity("300")

    assert mapped is not None
    assert mapped.activity_id == "300"
    assert mapped.activity_type == "indoor_cycling"
