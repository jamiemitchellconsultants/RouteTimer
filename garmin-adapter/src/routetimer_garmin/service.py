from __future__ import annotations

from dataclasses import dataclass, field
from io import BytesIO
from typing import Protocol
from zipfile import ZipFile

from routetimer_garmin.challenges import ChallengeStore, PendingChallenge
from routetimer_garmin.errors import AdapterError
from routetimer_garmin.facade import CompletedLogin, GarminFacade
from routetimer_garmin.models import AdapterActivity, AdapterActivityPage


PAGE_SIZE = 50
MAX_SCAN_PAGES = 10
MAX_FIT_BYTES = 50 * 1024 * 1024


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


@dataclass(frozen=True, slots=True)
class ActivitySummaryResult:
    activity: AdapterActivity
    token_json: str = field(repr=False)


@dataclass(frozen=True, slots=True)
class FitDownloadResult:
    content: bytes = field(repr=False)
    file_name: str
    token_json: str = field(repr=False)


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

    async def activities(self, token_json: str, offset: int) -> AdapterActivityPage:
        if offset < 0:
            raise AdapterError("request-invalid", 422)
        session = self._facade.from_tokens(token_json)
        activities: list[AdapterActivity] = []
        next_offset: int | None = None
        current_offset = offset
        for _ in range(MAX_SCAN_PAGES):
            batch = session.list_activities(current_offset, PAGE_SIZE)
            if batch.source_count < 0 or batch.source_count > PAGE_SIZE:
                raise AdapterError("response-invalid", 502)
            activities.extend(batch.activities[: PAGE_SIZE - len(activities)])
            if batch.source_count < PAGE_SIZE:
                break
            current_offset += batch.source_count
            next_offset = current_offset
            if len(activities) == PAGE_SIZE:
                break
        return AdapterActivityPage(activities, next_offset, session.dump_tokens())

    async def activity_summary(self, token_json: str, activity_id: str) -> ActivitySummaryResult:
        activity_id = _canonical_activity_id(activity_id)
        session = self._facade.from_tokens(token_json)
        activity = session.get_activity(activity_id)
        if activity is None:
            raise AdapterError("activity-not-allowed", 422)
        return ActivitySummaryResult(activity, session.dump_tokens())

    async def download_fit(self, token_json: str, activity_id: str) -> FitDownloadResult:
        activity_id = _canonical_activity_id(activity_id)
        session = self._facade.from_tokens(token_json)
        if session.get_activity(activity_id) is None:
            raise AdapterError("activity-not-allowed", 422)
        content = _read_single_fit(session.download_original(activity_id))
        return FitDownloadResult(content, f"{activity_id}.fit", session.dump_tokens())

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


def _canonical_activity_id(activity_id: str) -> str:
    try:
        numeric_id = int(activity_id)
    except (TypeError, ValueError):
        raise AdapterError("request-invalid", 422) from None
    if numeric_id <= 0 or str(numeric_id) != activity_id:
        raise AdapterError("request-invalid", 422)
    return activity_id


def _read_single_fit(archive_bytes: bytes) -> bytes:
    try:
        with ZipFile(BytesIO(archive_bytes)) as archive:
            members = [
                entry
                for entry in archive.infolist()
                if not entry.is_dir() and entry.filename.lower().endswith(".fit")
            ]
            if len(members) != 1:
                raise AdapterError("response-invalid", 502)
            member = members[0]
            if member.file_size > MAX_FIT_BYTES:
                raise AdapterError("fit-too-large", 413)
            with archive.open(member) as source:
                content = source.read(MAX_FIT_BYTES + 1)
            if len(content) > MAX_FIT_BYTES:
                raise AdapterError("fit-too-large", 413)
            return content
    except AdapterError:
        raise
    except Exception:
        raise AdapterError("response-invalid", 502) from None
