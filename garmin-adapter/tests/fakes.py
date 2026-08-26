from __future__ import annotations

from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from typing import Callable


class FakeClock:
    def __init__(self) -> None:
        self._now = datetime(2026, 8, 25, tzinfo=UTC)

    def now(self) -> datetime:
        return self._now

    def advance(self, duration: timedelta) -> None:
        self._now += duration


class FakeDeadlineHandle:
    def __init__(self, callback: Callable[[], None]) -> None:
        self.callback = callback
        self.cancelled = False

    def cancel(self) -> None:
        self.cancelled = True


class FakeDeadlineScheduler:
    def __init__(self) -> None:
        self.handles: list[FakeDeadlineHandle] = []
        self.delays: list[float] = []

    def call_later(self, delay: float, callback: Callable[[], None]) -> FakeDeadlineHandle:
        handle = FakeDeadlineHandle(callback)
        self.delays.append(delay)
        self.handles.append(handle)
        return handle

    def fire_next(self) -> None:
        handle = self.handles.pop(0)
        if not handle.cancelled:
            handle.callback()


@dataclass
class FakePendingLogin:
    completed_login: object | None = None
    resume_exception: Exception | None = None
    closed: bool = False
    received_code: str | None = None

    def resume(self, code: str) -> object:
        self.received_code = code
        if self.resume_exception is not None:
            raise self.resume_exception
        assert self.completed_login is not None
        return self.completed_login

    def close(self) -> None:
        self.closed = True


class FakeTokenSession:
    def __init__(self, validation: object) -> None:
        self.validation = validation

    def validate(self) -> object:
        if isinstance(self.validation, Exception):
            raise self.validation
        return self.validation


class FakeFacade:
    def __init__(self) -> None:
        self.login_result: object | Exception | None = None
        self.token_session: FakeTokenSession | None = None
        self.received_email: str | None = None
        self.received_password: str | None = None
        self.received_token_json: str | None = None

    def start_login(self, email: str, password: str) -> object:
        self.received_email = email
        self.received_password = password
        assert self.login_result is not None
        if isinstance(self.login_result, Exception):
            raise self.login_result
        return self.login_result

    def from_tokens(self, token_json: str) -> FakeTokenSession:
        self.received_token_json = token_json
        assert self.token_session is not None
        return self.token_session
