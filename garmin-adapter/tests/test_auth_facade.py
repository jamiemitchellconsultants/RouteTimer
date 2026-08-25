from __future__ import annotations

import logging
from typing import Any

import pytest

from routetimer_garmin.errors import AdapterError
from routetimer_garmin.facade import GarminFacade
from garminconnect.exceptions import GarminConnectAuthenticationError


class FakeClient:
    def __init__(self, profile: object) -> None:
        self.profile = profile
        self.connectapi_paths: list[str] = []

    def connectapi(self, path: str) -> object:
        self.connectapi_paths.append(path)
        if isinstance(self.profile, Exception):
            raise self.profile
        return self.profile

    def dumps(self) -> str:
        return '{"di_token":"rotated"}'

    def loads(self, token_json: str) -> None:
        self.loaded_token = token_json


class FakeGarmin:
    def __init__(
        self, profile: object, login_result: tuple[str | None, str | None] | Exception
    ) -> None:
        self.client = FakeClient(profile)
        self.login_result = login_result
        self.username: str | None = None
        self.password: str | None = None
        self.display_name: str | None = "Library name"
        self.full_name: str | None = "Library full name"
        self.login_calls = 0
        self.resume_calls: list[tuple[dict[str, Any], str]] = []

    def login(self) -> tuple[str | None, str | None]:
        self.login_calls += 1
        if isinstance(self.login_result, Exception):
            raise self.login_result
        return self.login_result

    def resume_login(self, state: dict[str, Any], code: str) -> None:
        self.resume_calls.append((state, code))

    def get_full_name(self) -> str | None:
        return self.full_name


class FakeFactory:
    def __init__(self, garmin: FakeGarmin) -> None:
        self.garmin = garmin
        self.calls: list[tuple[tuple[object, ...], dict[str, object]]] = []

    def __call__(self, *args: object, **kwargs: object) -> FakeGarmin:
        self.calls.append((args, kwargs))
        self.garmin.username = args[0] if args else None
        self.garmin.password = args[1] if len(args) > 1 else None
        return self.garmin


def test_start_login_uses_resumable_same_instance_api_and_clears_credentials() -> None:
    garmin = FakeGarmin({"profileId": 42, "fullName": "Jamie"}, ("needs_mfa", None))
    factory = FakeFactory(garmin)

    pending = GarminFacade(factory).start_login("rider@example.com", "secret")

    assert factory.calls == [(("rider@example.com", "secret"), {"return_on_mfa": True})]
    assert garmin.login_calls == 1
    assert garmin.username is None
    assert garmin.password is None
    completed = pending.resume("123456")
    assert garmin.resume_calls == [({}, "123456")]
    assert completed.garmin_user_id == "42"
    assert completed.display_name == "Jamie"


def test_validate_makes_one_profile_call_and_rejects_non_mapping_response() -> None:
    garmin = FakeGarmin([], (None, None))
    session = GarminFacade(FakeFactory(garmin)).from_tokens('{"di_token":"old"}')

    with pytest.raises(AdapterError, match="response-invalid"):
        session.validate()

    assert garmin.client.connectapi_paths == ["/userprofile-service/socialProfile"]


def test_library_exception_with_secrets_is_translated_without_logging_them(
    caplog: pytest.LogCaptureFixture,
) -> None:
    email = "rider@example.com"
    password = "secret"
    code = "123456"
    token = '{"di_token":"sensitive"}'
    garmin = FakeGarmin(
        GarminConnectAuthenticationError(f"failed {email} {password} {code} {token}"),
        ("needs_mfa", None),
    )

    with caplog.at_level(logging.DEBUG):
        with pytest.raises(AdapterError) as raised:
            pending = GarminFacade(FakeFactory(garmin)).start_login(email, password)
            pending.resume(code)

    captured = caplog.text + repr(raised.value)
    for secret in (email, password, code, token):
        assert secret not in captured
    assert raised.value.code == "mfa-invalid"


def test_validate_returns_current_tokens_and_safe_profile_identity() -> None:
    garmin = FakeGarmin({"userId": "42", "displayName": "Jamie"}, (None, None))
    session = GarminFacade(FakeFactory(garmin)).from_tokens('{"di_token":"old"}')

    result = session.validate()

    assert result.garmin_user_id == "42"
    assert result.display_name == "Jamie"
    assert result.token_json == '{"di_token":"rotated"}'
    assert garmin.client.connectapi_paths == ["/userprofile-service/socialProfile"]
