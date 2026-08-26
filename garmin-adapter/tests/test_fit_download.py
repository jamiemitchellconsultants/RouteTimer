from __future__ import annotations

from base64 import urlsafe_b64decode
from io import BytesIO
from zipfile import ZIP_DEFLATED, ZipFile

import pytest
from fastapi.testclient import TestClient

from routetimer_garmin.api import app, get_service
from routetimer_garmin.challenges import ChallengeStore
from routetimer_garmin.errors import AdapterError
from routetimer_garmin.service import GarminService
from tests.test_activities import FakeActivityFacade


def zip_bytes(members: dict[str, bytes]) -> bytes:
    content = BytesIO()
    with ZipFile(content, "w", ZIP_DEFLATED) as archive:
        for name, body in members.items():
            archive.writestr(name, body)
    return content.getvalue()


@pytest.fixture
def fake_facade() -> FakeActivityFacade:
    facade = FakeActivityFacade()
    facade.session.original_download = zip_bytes({"../../ride\r\n.fit": b"FIT-CONTENT"})
    return facade


async def test_original_download_returns_the_single_fit_member_without_extracting_paths(
    fake_facade: FakeActivityFacade,
) -> None:
    result = await GarminService(fake_facade, ChallengeStore.system()).download_fit(
        '{"di_token":"a"}', "123"
    )

    assert result.content == b"FIT-CONTENT"
    assert result.file_name == "123.fit"
    # No get_activity call here: download_fit doesn't re-verify the activity via a second,
    # independent call to Garmin's detail endpoint -- see the comment on download_fit for why.
    assert fake_facade.session.requested_activity_ids == []
    assert fake_facade.session.downloaded_activity_ids == ["123"]


@pytest.mark.parametrize(
    "members",
    [{}, {"one.fit": b"a", "two.fit": b"b"}, {"readme.txt": b"not fit"}],
)
async def test_original_download_rejects_missing_or_ambiguous_fit_members(
    fake_facade: FakeActivityFacade, members: dict[str, bytes]
) -> None:
    fake_facade.session.original_download = zip_bytes(members)

    with pytest.raises(AdapterError, match="response-invalid"):
        await GarminService(fake_facade, ChallengeStore.system()).download_fit(
            '{"di_token":"a"}', "123"
        )


async def test_original_download_rejects_a_declared_fit_larger_than_fifty_mib(
    fake_facade: FakeActivityFacade, monkeypatch: pytest.MonkeyPatch
) -> None:
    from routetimer_garmin import service as service_module

    class OversizedEntry:
        filename = "ride.fit"
        file_size = 50 * 1024 * 1024 + 1

        def is_dir(self) -> bool:
            return False

    class Archive:
        def __enter__(self) -> Archive:
            return self

        def __exit__(self, *_: object) -> None:
            return None

        def infolist(self) -> list[OversizedEntry]:
            return [OversizedEntry()]

    monkeypatch.setattr(service_module, "ZipFile", lambda _: Archive())

    with pytest.raises(AdapterError, match="fit-too-large"):
        await GarminService(fake_facade, ChallengeStore.system()).download_fit(
            '{"di_token":"a"}', "123"
        )


async def test_original_download_rejects_a_stream_larger_than_fifty_mib(
    fake_facade: FakeActivityFacade, monkeypatch: pytest.MonkeyPatch
) -> None:
    from routetimer_garmin import service as service_module

    class Entry:
        filename = "ride.fit"
        file_size = 50 * 1024 * 1024

        def is_dir(self) -> bool:
            return False

    class Source:
        def __enter__(self) -> Source:
            return self

        def __exit__(self, *_: object) -> None:
            return None

        def read(self, _: int) -> bytes:
            return b"x" * (50 * 1024 * 1024 + 1)

    class Archive:
        def __enter__(self) -> Archive:
            return self

        def __exit__(self, *_: object) -> None:
            return None

        def infolist(self) -> list[Entry]:
            return [Entry()]

        def open(self, _: Entry) -> Source:
            return Source()

    monkeypatch.setattr(service_module, "ZipFile", lambda _: Archive())

    with pytest.raises(AdapterError, match="fit-too-large"):
        await GarminService(fake_facade, ChallengeStore.system()).download_fit(
            '{"di_token":"a"}', "123"
        )


class FakeFitService:
    async def download_fit(self, token_json: str, activity_id: str) -> object:
        assert token_json == '{"di_token":"a"}'
        assert activity_id == "123"
        return type(
            "Download",
            (),
            {
                "content": b"FIT-CONTENT",
                "file_name": "123.fit",
                "token_json": '{"di_token":"rotated"}',
            },
        )()


def test_fit_http_uses_a_safe_numeric_filename_and_unpadded_base64url_token() -> None:
    app.dependency_overrides[get_service] = FakeFitService
    try:
        with TestClient(app) as client:
            response = client.post("/v1/activities/123/fit", json={"token": '{"di_token":"a"}'})
    finally:
        app.dependency_overrides.clear()

    assert response.status_code == 200
    assert response.content == b"FIT-CONTENT"
    assert response.headers["content-disposition"] == 'attachment; filename="123.fit"'
    encoded = response.headers["x-routetimer-garmin-token"]
    assert "=" not in encoded
    assert urlsafe_b64decode(encoded + "==") == b'{"di_token":"rotated"}'
