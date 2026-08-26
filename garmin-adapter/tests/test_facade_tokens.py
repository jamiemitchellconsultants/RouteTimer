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
