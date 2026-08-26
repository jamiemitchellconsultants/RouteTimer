from __future__ import annotations

import logging
from base64 import urlsafe_b64encode
from typing import Annotated, Any

from fastapi import Depends, FastAPI, Request, Response
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field, SecretStr

from routetimer_garmin.challenges import ChallengeStore
from routetimer_garmin.errors import AdapterError
from routetimer_garmin.facade import GarminFacade
from routetimer_garmin.models import AdapterActivity, AdapterActivityPage
from routetimer_garmin.service import (
    ActivitySummaryResult,
    FitDownloadResult,
    GarminService,
    LoginResult,
    SessionResult,
)


for logger_name in ("garminconnect", "garminconnect.client"):
    logging.getLogger(logger_name).setLevel(logging.CRITICAL)


app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)
_service = GarminService(GarminFacade(), ChallengeStore.system())


class _SecretRequest(BaseModel):
    def __repr_args__(self) -> Any:
        return []


class LoginRequest(_SecretRequest):
    email: str
    password: SecretStr


class MfaRequest(_SecretRequest):
    challenge_id: str = Field(alias="challengeId")
    code: SecretStr

    def __repr_args__(self) -> Any:
        return [("challenge_id", self.challenge_id)]


class TokenRequest(_SecretRequest):
    token: SecretStr


class ActivityPageRequest(TokenRequest):
    offset: int = Field(default=0, ge=0, strict=True)


class LoginResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    state: str
    challenge_id: str | None = Field(default=None, alias="challengeId")
    token_json: str | None = Field(default=None, alias="tokenJson", repr=False)
    garmin_user_id: str | None = Field(default=None, alias="garminUserId")
    display_name: str | None = Field(default=None, alias="displayName")

    @classmethod
    def from_result(cls, result: LoginResult) -> LoginResponse:
        return cls(
            state=result.state,
            challengeId=result.challenge_id,
            tokenJson=result.token_json,
            garminUserId=result.garmin_user_id,
            displayName=result.display_name,
        )


class SessionResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    token_json: str = Field(alias="tokenJson", repr=False)
    garmin_user_id: str | None = Field(default=None, alias="garminUserId")
    display_name: str | None = Field(default=None, alias="displayName")

    @classmethod
    def from_result(cls, result: SessionResult) -> SessionResponse:
        return cls(
            tokenJson=result.token_json,
            garminUserId=result.garmin_user_id,
            displayName=result.display_name,
        )


class ActivityResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    activity_id: str = Field(alias="activityId")
    name: str
    started_at: str = Field(alias="startedAt")
    activity_type: str = Field(alias="activityType")
    distance_metres: float | None = Field(alias="distanceMetres")
    duration_seconds: float | None = Field(alias="durationSeconds")
    ascent_metres: float | None = Field(alias="ascentMetres")
    average_power_watts: float | None = Field(alias="averagePowerWatts")

    @classmethod
    def from_activity(cls, activity: AdapterActivity) -> ActivityResponse:
        return cls(
            activityId=activity.activity_id,
            name=activity.name,
            startedAt=activity.started_at.isoformat().replace("+00:00", "Z"),
            activityType=activity.activity_type,
            distanceMetres=activity.distance_metres,
            durationSeconds=activity.duration_seconds,
            ascentMetres=activity.ascent_metres,
            averagePowerWatts=activity.average_power_watts,
        )


class ActivityPageResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    activities: list[ActivityResponse]
    next_offset: int | None = Field(alias="nextOffset")
    token_json: str = Field(alias="tokenJson", repr=False)

    @classmethod
    def from_result(cls, result: AdapterActivityPage) -> ActivityPageResponse:
        return cls(
            activities=[ActivityResponse.from_activity(item) for item in result.activities],
            nextOffset=result.next_offset,
            tokenJson=result.token_json,
        )


class ActivitySummaryResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    activity: ActivityResponse
    token_json: str = Field(alias="tokenJson", repr=False)

    @classmethod
    def from_result(cls, result: ActivitySummaryResult) -> ActivitySummaryResponse:
        return cls(
            activity=ActivityResponse.from_activity(result.activity), tokenJson=result.token_json
        )


def get_service() -> GarminService:
    return _service


@app.exception_handler(AdapterError)
async def adapter_error_handler(_: Request, error: AdapterError) -> JSONResponse:
    return JSONResponse(
        status_code=error.status_code,
        content={"code": error.code, "detail": error.safe_detail},
    )


@app.exception_handler(RequestValidationError)
async def request_validation_error_handler(_: Request, __: RequestValidationError) -> JSONResponse:
    return JSONResponse(
        status_code=422,
        content={"code": "request-invalid", "detail": "request-invalid"},
    )


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "healthy"}


@app.post("/v1/auth/login", response_model=LoginResponse, response_model_exclude_none=True)
async def login(
    request: LoginRequest,
    service: Annotated[GarminService, Depends(get_service)],
) -> LoginResponse:
    result = await service.login(request.email, request.password.get_secret_value())
    return LoginResponse.from_result(result)


@app.post("/v1/auth/mfa", response_model=LoginResponse, response_model_exclude_none=True)
async def complete_mfa(
    request: MfaRequest,
    service: Annotated[GarminService, Depends(get_service)],
) -> LoginResponse:
    result = await service.complete_mfa(request.challenge_id, request.code.get_secret_value())
    return LoginResponse.from_result(result)


@app.post("/v1/auth/validate", response_model=SessionResponse, response_model_exclude_none=True)
async def validate(
    request: TokenRequest,
    service: Annotated[GarminService, Depends(get_service)],
) -> SessionResponse:
    result = await service.validate(request.token.get_secret_value())
    return SessionResponse.from_result(result)


@app.post("/v1/activities/page", response_model=ActivityPageResponse)
async def activities(
    request: ActivityPageRequest,
    service: Annotated[GarminService, Depends(get_service)],
) -> ActivityPageResponse:
    result = await service.activities(request.token.get_secret_value(), request.offset)
    return ActivityPageResponse.from_result(result)


@app.post("/v1/activities/{activity_id}/summary", response_model=ActivitySummaryResponse)
async def activity_summary(
    activity_id: str,
    request: TokenRequest,
    service: Annotated[GarminService, Depends(get_service)],
) -> ActivitySummaryResponse:
    result = await service.activity_summary(request.token.get_secret_value(), activity_id)
    return ActivitySummaryResponse.from_result(result)


@app.post("/v1/activities/{activity_id}/fit")
async def download_fit(
    activity_id: str,
    request: TokenRequest,
    service: Annotated[GarminService, Depends(get_service)],
) -> Response:
    result: FitDownloadResult = await service.download_fit(
        request.token.get_secret_value(), activity_id
    )
    encoded_token = (
        urlsafe_b64encode(result.token_json.encode("utf-8")).rstrip(b"=").decode("ascii")
    )
    return Response(
        content=result.content,
        media_type="application/octet-stream",
        headers={
            "Content-Disposition": f'attachment; filename="{result.file_name}"',
            "X-RouteTimer-Garmin-Token": encoded_token,
        },
    )


@app.delete("/v1/auth/challenges", status_code=204)
async def clear_challenges(service: Annotated[GarminService, Depends(get_service)]) -> Response:
    await service.clear_challenges()
    return Response(status_code=204)
