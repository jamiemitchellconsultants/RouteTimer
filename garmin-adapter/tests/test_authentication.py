from __future__ import annotations

from datetime import timedelta

import pytest

from routetimer_garmin.challenges import ChallengeStore
from routetimer_garmin.errors import AdapterError
from routetimer_garmin.facade import CompletedLogin
from routetimer_garmin.service import GarminService, SessionResult
from tests.fakes import FakeClock, FakeFacade, FakePendingLogin, FakeTokenSession


@pytest.fixture
def clock() -> FakeClock:
    return FakeClock()


@pytest.fixture
def fake_facade() -> FakeFacade:
    return FakeFacade()


async def test_login_returns_token_without_retaining_credentials(
    fake_facade: FakeFacade, clock: FakeClock
) -> None:
    fake_facade.login_result = CompletedLogin("42", "Jamie", '{"di_token":"a"}')
    service = GarminService(fake_facade, ChallengeStore(clock, timedelta(minutes=5)))

    result = await service.login("rider@example.com", "secret")

    assert result.state == "connected"
    assert result.token_json == '{"di_token":"a"}'
    assert service.challenge_count == 0
    assert "secret" not in repr(result)


async def test_mfa_challenge_expires_and_clears_pending_login(
    fake_facade: FakeFacade, clock: FakeClock
) -> None:
    pending = FakePendingLogin()
    fake_facade.login_result = pending
    service = GarminService(fake_facade, ChallengeStore(clock, timedelta(minutes=5)))
    challenge = await service.login("rider@example.com", "secret")
    clock.advance(timedelta(minutes=6))

    with pytest.raises(AdapterError, match="challenge-expired"):
        await service.complete_mfa(challenge.challenge_id or "", "123456")

    assert pending.closed
    assert service.challenge_count == 0


async def test_mfa_invalid_keeps_challenge_for_retry(
    fake_facade: FakeFacade, clock: FakeClock
) -> None:
    pending = FakePendingLogin(
        resume_exception=AdapterError("mfa-invalid", 400),
        completed_login=CompletedLogin("42", "Jamie", '{"di_token":"a"}'),
    )
    fake_facade.login_result = pending
    service = GarminService(fake_facade, ChallengeStore(clock, timedelta(minutes=5)))
    challenge = await service.login("rider@example.com", "secret")

    with pytest.raises(AdapterError, match="mfa-invalid"):
        await service.complete_mfa(challenge.challenge_id or "", "bad-code")
    pending.resume_exception = None
    result = await service.complete_mfa(challenge.challenge_id or "", "123456")

    assert result.state == "connected"
    assert pending.received_code == "123456"
    assert pending.closed
    assert service.challenge_count == 0


async def test_clear_challenges_closes_every_pending_login(
    fake_facade: FakeFacade, clock: FakeClock
) -> None:
    first = FakePendingLogin()
    second = FakePendingLogin()
    fake_facade.login_result = first
    service = GarminService(fake_facade, ChallengeStore(clock, timedelta(minutes=5)))
    await service.login("one@example.com", "secret")
    fake_facade.login_result = second
    await service.login("two@example.com", "secret")

    await service.clear_challenges()

    assert first.closed
    assert second.closed
    assert service.challenge_count == 0


async def test_validate_returns_rotated_tokens_and_safe_identity(
    fake_facade: FakeFacade, clock: FakeClock
) -> None:
    fake_facade.token_session = FakeTokenSession(
        SessionResult('{"di_token":"rotated"}', "42", "Jamie")
    )
    service = GarminService(fake_facade, ChallengeStore(clock, timedelta(minutes=5)))

    result = await service.validate('{"di_token":"old"}')

    assert result.token_json == '{"di_token":"rotated"}'
    assert result.garmin_user_id == "42"
    assert result.display_name == "Jamie"
    assert fake_facade.received_token_json == '{"di_token":"old"}'
