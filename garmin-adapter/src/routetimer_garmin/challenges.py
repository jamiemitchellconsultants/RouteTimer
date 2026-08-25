from __future__ import annotations

import secrets
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from typing import Protocol

from routetimer_garmin.errors import AdapterError


class PendingChallenge(Protocol):
    def close(self) -> None: ...


class Clock(Protocol):
    def now(self) -> datetime: ...


@dataclass(slots=True)
class Challenge:
    pending: PendingChallenge
    expires_at: datetime


class SystemClock:
    def now(self) -> datetime:
        return datetime.now(UTC)


class ChallengeStore:
    def __init__(self, clock: Clock, ttl: timedelta) -> None:
        self._clock = clock
        self._ttl = ttl
        self._entries: dict[str, Challenge] = {}

    @classmethod
    def system(cls) -> ChallengeStore:
        return cls(SystemClock(), timedelta(minutes=5))

    @property
    def count(self) -> int:
        self.prune()
        return len(self._entries)

    def put(self, pending: PendingChallenge) -> str:
        self.prune()
        challenge_id = secrets.token_urlsafe(32)
        self._entries[challenge_id] = Challenge(pending, self._clock.now() + self._ttl)
        return challenge_id

    def take_for_attempt(self, challenge_id: str) -> PendingChallenge:
        self.prune()
        challenge = self._entries.get(challenge_id)
        if challenge is None:
            raise AdapterError("challenge-expired", 409)
        return challenge.pending

    def complete(self, challenge_id: str) -> None:
        challenge = self._entries.pop(challenge_id, None)
        if challenge is not None:
            challenge.pending.close()

    def clear(self) -> None:
        for challenge in self._entries.values():
            challenge.pending.close()
        self._entries.clear()

    def prune(self) -> None:
        expired_ids = [
            challenge_id
            for challenge_id, challenge in self._entries.items()
            if challenge.expires_at <= self._clock.now()
        ]
        for challenge_id in expired_ids:
            self.complete(challenge_id)
