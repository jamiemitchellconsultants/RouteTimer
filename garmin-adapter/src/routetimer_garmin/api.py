from __future__ import annotations

import logging
from typing import Annotated, Any

from fastapi import Depends, FastAPI, Request, Response
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field, SecretStr

from routetimer_garmin.challenges import ChallengeStore
from routetimer_garmin.errors import AdapterError
from routetimer_garmin.facade import GarminFacade
from routetimer_garmin.service import GarminService, LoginResult, SessionResult


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


class ValidateRequest(_SecretRequest):
    token: SecretStr


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
        content={"code": "response-invalid", "detail": "response-invalid"},
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
    request: ValidateRequest,
    service: Annotated[GarminService, Depends(get_service)],
) -> SessionResponse:
    result = await service.validate(request.token.get_secret_value())
    return SessionResponse.from_result(result)


@app.delete("/v1/auth/challenges", status_code=204)
async def clear_challenges(service: Annotated[GarminService, Depends(get_service)]) -> Response:
    await service.clear_challenges()
    return Response(status_code=204)
