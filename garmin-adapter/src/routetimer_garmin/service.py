from __future__ import annotations

from dataclasses import dataclass, field
from typing import Protocol

from routetimer_garmin.challenges import ChallengeStore, PendingChallenge
from routetimer_garmin.errors import AdapterError
from routetimer_garmin.facade import CompletedLogin, GarminFacade


@dataclass(frozen=True, slots=True)
class LoginResult:
    state: str
    challenge_id: str | None
    token_json: str | None = field(repr=False)
    garmin_user_id: str | None
    display_name: str | None


@dataclass(frozen=True, slots=True)
class SessionResult:
    token_json: str = field(repr=False)
    garmin_user_id: str | None
    display_name: str | None


class ResumableLogin(PendingChallenge, Protocol):
    def resume(self, code: str) -> CompletedLogin: ...


class GarminService:
    def __init__(self, facade: GarminFacade, challenges: ChallengeStore) -> None:
        self._facade = facade
        self._challenges = challenges

    @property
    def challenge_count(self) -> int:
        return self._challenges.count

    async def login(self, email: str, password: str) -> LoginResult:
        started = self._facade.start_login(email, password)
        if isinstance(started, CompletedLogin):
            return _connected_result(started)
        challenge_id = self._challenges.put(started)
        return LoginResult("mfa-required", challenge_id, None, None, None)

    async def complete_mfa(self, challenge_id: str, code: str) -> LoginResult:
        pending = self._challenges.take_for_attempt(challenge_id)
        try:
            completed = _as_resumable(pending).resume(code)
        except AdapterError as error:
            if error.code != "mfa-invalid":
                self._challenges.complete(challenge_id)
            raise
        self._challenges.complete(challenge_id)
        return _connected_result(completed)

    async def validate(self, token_json: str) -> SessionResult:
        session = self._facade.from_tokens(token_json)
        validated = session.validate()
        return SessionResult(
            validated.token_json,
            validated.garmin_user_id,
            validated.display_name,
        )

    async def clear_challenges(self) -> None:
        self._challenges.clear()


def _connected_result(completed: CompletedLogin) -> LoginResult:
    return LoginResult(
        "connected",
        None,
        completed.token_json,
        completed.garmin_user_id,
        completed.display_name,
    )


def _as_resumable(pending: PendingChallenge) -> ResumableLogin:
    return pending  # type: ignore[return-value]
