from __future__ import annotations

import asyncio
from datetime import timedelta
import logging

import pytest
from fastapi.testclient import TestClient

from routetimer_garmin.api import app, get_service
from routetimer_garmin.challenges import ChallengeStore
from routetimer_garmin.errors import AdapterError
from routetimer_garmin.service import GarminService, LoginResult, SessionResult
from tests.fakes import FakeClock, FakeFacade, FakePendingLogin


class FakeService:
    def __init__(self) -> None:
        self.login_result = LoginResult("connected", None, '{"di_token":"a"}', "42", "Jamie")
        self.mfa_result = self.login_result
        self.validation_result = SessionResult('{"di_token":"rotated"}', "42", "Jamie")
        self.login_error: AdapterError | None = None
        self.clear_count = 0
        self.received_email: str | None = None
        self.received_password: str | None = None
        self.received_challenge_id: str | None = None
        self.received_code: str | None = None
        self.received_token: str | None = None

    async def login(self, email: str, password: str) -> LoginResult:
        self.received_email = email
        self.received_password = password
        if self.login_error is not None:
            raise self.login_error
        return self.login_result

    async def complete_mfa(self, challenge_id: str, code: str) -> LoginResult:
        self.received_challenge_id = challenge_id
        self.received_code = code
        return self.mfa_result

    async def validate(self, token_json: str) -> SessionResult:
        self.received_token = token_json
        return self.validation_result

    async def clear_challenges(self) -> None:
        self.clear_count += 1


@pytest.fixture
def fake_service() -> FakeService:
    return FakeService()


@pytest.fixture
def client(fake_service: FakeService) -> TestClient:
    app.dependency_overrides[get_service] = lambda: fake_service
    with TestClient(app) as test_client:
        yield test_client
    app.dependency_overrides.clear()


def test_login_http_never_returns_submitted_credentials(
    client: TestClient, fake_service: FakeService
) -> None:
    fake_service.login_result = LoginResult("mfa-required", "challenge-1", None, None, None)

    response = client.post(
        "/v1/auth/login", json={"email": "rider@example.com", "password": "secret"}
    )

    assert response.status_code == 200
    assert response.json() == {"state": "mfa-required", "challengeId": "challenge-1"}
    assert "secret" not in response.text
    assert "rider@example.com" not in response.text


def test_connected_login_returns_only_adapter_session_fields(
    client: TestClient, fake_service: FakeService
) -> None:
    response = client.post(
        "/v1/auth/login", json={"email": "rider@example.com", "password": "secret"}
    )

    assert response.status_code == 200
    assert response.json() == {
        "state": "connected",
        "tokenJson": '{"di_token":"a"}',
        "garminUserId": "42",
        "displayName": "Jamie",
    }
    assert fake_service.received_password == "secret"


def test_validate_uses_secret_token_request_and_returns_rotated_session(
    client: TestClient, fake_service: FakeService
) -> None:
    response = client.post("/v1/auth/validate", json={"token": '{"di_token":"old"}'})

    assert response.status_code == 200
    assert response.json() == {
        "tokenJson": '{"di_token":"rotated"}',
        "garminUserId": "42",
        "displayName": "Jamie",
    }
    assert fake_service.received_token == '{"di_token":"old"}'


def test_stable_error_response_does_not_leak_upstream_details(
    client: TestClient, fake_service: FakeService, caplog: pytest.LogCaptureFixture
) -> None:
    secret = "rider@example.com secret 123456 token-json"
    fake_service.login_error = AdapterError("credentials-rejected", 400)

    with caplog.at_level(logging.DEBUG):
        response = client.post(
            "/v1/auth/login", json={"email": "rider@example.com", "password": "secret"}
        )

    assert response.status_code == 400
    assert response.json() == {"code": "credentials-rejected", "detail": "credentials-rejected"}
    assert secret not in response.text + caplog.text


def test_health_and_clear_challenges_routes(client: TestClient, fake_service: FakeService) -> None:
    health = client.get("/health")
    cleared = client.delete("/v1/auth/challenges")

    assert health.json() == {"status": "healthy"}
    assert cleared.status_code == 204
    assert fake_service.clear_count == 1


def test_clear_challenges_route_closes_real_pending_sessions() -> None:
    facade = FakeFacade()
    pending = FakePendingLogin()
    facade.login_result = pending
    service = GarminService(facade, ChallengeStore(FakeClock(), timedelta(minutes=5)))
    asyncio.run(service.login("rider@example.com", "secret"))
    app.dependency_overrides[get_service] = lambda: service

    with TestClient(app) as client:
        response = client.delete("/v1/auth/challenges")
    app.dependency_overrides.clear()

    assert response.status_code == 204
    assert pending.closed
    assert service.challenge_count == 0
