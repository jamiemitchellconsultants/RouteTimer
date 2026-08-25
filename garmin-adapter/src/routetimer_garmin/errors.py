"""Stable adapter error definitions belong in this module."""


class AdapterError(Exception):
    """An adapter error that never retains upstream exception details."""

    def __init__(self, code: str, status_code: int) -> None:
        super().__init__(code)
        self.code = code
        self.status_code = status_code

    @property
    def safe_detail(self) -> str:
        return self.code
