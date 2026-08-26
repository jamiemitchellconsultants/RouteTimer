from __future__ import annotations

import logging
from dataclasses import dataclass, field
from io import BytesIO
from typing import Protocol
from zipfile import ZipFile

from routetimer_garmin.challenges import ChallengeStore, PendingChallenge
from routetimer_garmin.errors import AdapterError
from routetimer_garmin.facade import CompletedLogin, CreatedCourse, GarminFacade
from routetimer_garmin.models import AdapterActivity, AdapterActivityPage

logger = logging.getLogger(__name__)


PAGE_SIZE = 50
MAX_SCAN_PAGES = 10
MAX_FILE_BYTES = 50 * 1024 * 1024


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


@dataclass(frozen=True, slots=True)
class CourseResult:
    course: CreatedCourse
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
                next_offset = None
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
        if activity.activity_id != activity_id:
            # Not the raw Garmin payload, just the two IDs that decide this branch -- but worth
            # keeping: this endpoint has already shown its own inconsistency once (see
            # download_fit's comment), so if this genuinely-not-redundant check ever fires, this
            # is what tells us whether it's Garmin's detail endpoint again or something else.
            logger.warning(
                "activity_summary id mismatch: requested=%r returned=%r", activity_id, activity.activity_id
            )
            raise AdapterError("response-invalid", 502)
        return ActivitySummaryResult(activity, session.dump_tokens())

    async def download_fit(self, token_json: str, activity_id: str) -> FitDownloadResult:
        activity_id = _canonical_activity_id(activity_id)
        session = self._facade.from_tokens(token_json)
        # No get_activity re-check here (there used to be one): RouteTimer's API always calls
        # activity_summary for this exact activity_id, on this exact token, immediately before
        # calling this -- see GarminActivityService.ImportAsync. Re-fetching from Garmin's
        # single-activity detail endpoint a second time added nothing but a second independent
        # chance to hit its own inconsistency: confirmed directly against a real account, this
        # endpoint returned different data for the same activity_id between two calls moments
        # apart within the same import, intermittently rejecting an activity_summary call had just
        # accepted. download_original below fetches by the exact ID Garmin was asked for; there is
        # no path through it that could return a different activity's bytes for that ID.
        content = _read_single_fit(session.download_original(activity_id))
        return FitDownloadResult(content, f"{activity_id}.fit", session.dump_tokens())

    async def create_course(
        self,
        token_json: str,
        *,
        gpx: bytes,
        file_name: str,
        course_name: str,
        activity_type: str,
        description: str | None,
        elevation_gain_metres: float,
        elevation_loss_metres: float,
    ) -> CourseResult:
        session = self._facade.from_tokens(token_json)
        course = session.create_course(
            gpx=gpx,
            file_name=file_name,
            course_name=course_name,
            activity_type=activity_type,
            description=description,
            elevation_gain_metres=elevation_gain_metres,
            elevation_loss_metres=elevation_loss_metres,
        )
        return CourseResult(course, session.dump_tokens())

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
            if member.file_size > MAX_FILE_BYTES:
                raise AdapterError("fit-too-large", 413)
            with archive.open(member) as source:
                content = source.read(MAX_FILE_BYTES + 1)
            if len(content) > MAX_FILE_BYTES:
                raise AdapterError("fit-too-large", 413)
            return content
    except AdapterError:
        raise
    except Exception:
        raise AdapterError("response-invalid", 502) from None
