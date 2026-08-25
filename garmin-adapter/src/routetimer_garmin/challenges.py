from __future__ import annotations

import asyncio
import secrets
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from typing import Protocol

from routetimer_garmin.errors import AdapterError


class PendingChallenge(Protocol):
    def close(self) -> None: ...


class Clock(Protocol):
    def now(self) -> datetime: ...


class DeadlineHandle(Protocol):
    def cancel(self) -> None: ...


class DeadlineScheduler(Protocol):
    def call_later(self, delay: float, callback: Callable[[], None]) -> DeadlineHandle: ...


@dataclass(slots=True)
class Challenge:
    pending: PendingChallenge
    expires_at: datetime
    deadline_handle: DeadlineHandle | None = None


class SystemClock:
    def now(self) -> datetime:
        return datetime.now(UTC)


class AsyncioDeadlineScheduler:
    def call_later(self, delay: float, callback: Callable[[], None]) -> DeadlineHandle:
        return asyncio.get_running_loop().call_later(delay, callback)


class ChallengeStore:
    def __init__(
        self,
        clock: Clock,
        ttl: timedelta,
        deadline_scheduler: DeadlineScheduler | None = None,
    ) -> None:
        self._clock = clock
        self._ttl = ttl
        self._deadline_scheduler = deadline_scheduler or AsyncioDeadlineScheduler()
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
        handle = self._deadline_scheduler.call_later(
            self._ttl.total_seconds(), lambda: self._expire(challenge_id)
        )
        challenge = self._entries.get(challenge_id)
        if challenge is None:
            handle.cancel()
        else:
            challenge.deadline_handle = handle
        return challenge_id

    def take_for_attempt(self, challenge_id: str) -> PendingChallenge:
        self.prune()
        challenge = self._entries.get(challenge_id)
        if challenge is None:
            raise AdapterError("challenge-expired", 409)
        return challenge.pending

    def complete(self, challenge_id: str) -> None:
        self._remove(challenge_id, cancel_deadline=True)

    def clear(self) -> None:
        for challenge_id in list(self._entries):
            self.complete(challenge_id)

    def prune(self) -> None:
        expired_ids = [
            challenge_id
            for challenge_id, challenge in self._entries.items()
            if challenge.expires_at <= self._clock.now()
        ]
        for challenge_id in expired_ids:
            self.complete(challenge_id)

    def _expire(self, challenge_id: str) -> None:
        self._remove(challenge_id, cancel_deadline=False)

    def _remove(self, challenge_id: str, cancel_deadline: bool) -> None:
        challenge = self._entries.pop(challenge_id, None)
        if challenge is None:
            return
        if cancel_deadline and challenge.deadline_handle is not None:
            challenge.deadline_handle.cancel()
        challenge.pending.close()
