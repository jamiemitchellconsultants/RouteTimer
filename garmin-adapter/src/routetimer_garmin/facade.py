from collections.abc import Callable
from typing import cast

from garminconnect import Garmin


class GarminFacade:
    def __init__(self, factory: Callable[..., Garmin] = Garmin) -> None:
        self._factory = factory

    def from_tokens(self, token_json: str) -> "TokenSession":
        garmin = self._factory()
        garmin.client.loads(token_json)
        return TokenSession(garmin)


class TokenSession:
    def __init__(self, garmin: Garmin) -> None:
        self.garmin = garmin

    def dump_tokens(self) -> str:
        return cast(str, self.garmin.client.dumps())
