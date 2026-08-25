# Garmin Activity Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the authenticated RouteTimer rider connect Garmin, browse road and gravel activities, and import selected original FIT files into the existing performance-model pipeline.

**Architecture:** A private Python adapter pinned to `python-garminconnect==0.3.4` owns Garmin's unofficial login, MFA, token refresh, activity listing, and FIT download behavior. The .NET API owns public authentication, AES-256-GCM token persistence, idempotent upload/link persistence, stable contracts, and reuse of the existing training jobs; Blazor owns only forms and selection state.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10, PostgreSQL 16, Blazor WebAssembly, bUnit, xUnit, Testcontainers, Python 3.12, FastAPI, Uvicorn, pytest, Ruff, mypy, `python-garminconnect==0.3.4`, Docker Compose.

**Spec:** `docs/superpowers/specs/2026-08-25-garmin-activity-import-design.md`

## Global Constraints

- Keep the integration personal-use and unofficial; do not introduce Garmin's business Activity API.
- Pin `python-garminconnect` exactly to `0.3.4`; all library types remain inside `garmin-adapter/`.
- Never persist or log Garmin email, password, MFA code, plaintext access token, plaintext refresh token, cookies, or plaintext token JSON.
- Persist the token bundle only as AES-256-GCM nonce, ciphertext, and tag; load the 32-byte key from base64 `Garmin__TokenEncryptionKey`.
- The Python adapter has no public host port, database access, Keycloak access, token volume, or encryption key.
- List only Garmin `road_biking` and `gravel_cycling`, mapped to `road-cycling` and `gravel-cycling`; exclude every other type.
- Return at most 50 Garmin activities per page and accept one to ten distinct activity IDs per import.
- Keep the existing 50 MiB FIT limit and 512-character stored filename limit.
- Reuse `TrainingUploadService`, `ParseTraining`, and the coalesced `BuildModel` path; do not add Garmin-specific parsing or model code.
- Automated tests and CI never contact Garmin; real-account verification is explicit and opt-in.
- Preserve the existing manual FIT upload API behavior, including hiding identifiers on manual duplicate outcomes.
- The pre-existing client-test hang is not a pass: use bounded hang diagnostics during feature verification and report the observed result.

---

## File Structure

### Python adapter

- `garmin-adapter/pyproject.toml` — exact runtime and development dependencies plus Ruff, mypy, and pytest configuration.
- `garmin-adapter/src/routetimer_garmin/models.py` — adapter request/response models and canonical activity summaries.
- `garmin-adapter/src/routetimer_garmin/errors.py` — stable adapter error codes and FastAPI exception mapping.
- `garmin-adapter/src/routetimer_garmin/facade.py` — the only module importing `garminconnect`; token load/dump and library calls.
- `garmin-adapter/src/routetimer_garmin/challenges.py` — five-minute in-memory MFA challenge lifecycle.
- `garmin-adapter/src/routetimer_garmin/service.py` — login, MFA, validation, activity mapping, and secure original-FIT extraction.
- `garmin-adapter/src/routetimer_garmin/api.py` — versioned internal HTTP routes and health endpoint.
- `garmin-adapter/tests/` — fake-facade unit and HTTP contract tests.

### .NET Garmin boundary

- `src/RouteTimer.Services/Garmin/GarminAdapterContracts.cs` — internal adapter records, error enum, and `IGarminAdapterClient`.
- `src/RouteTimer.Api/Garmin/GarminAdapterClient.cs` — typed private HTTP client; no Garmin library knowledge.
- `src/RouteTimer.Services/Garmin/GarminTokenProtection.cs` — AES-GCM protector and protected-token value.
- `src/RouteTimer.Services/Garmin/GarminOperationGate.cs` — singleton serialization for single-rider token rotation.

### Persistence and application services

- `src/RouteTimer.Persistence/Entities/GarminConnectionEntity.cs` — encrypted single-row connection.
- `src/RouteTimer.Persistence/Entities/GarminActivityImportEntity.cs` — Garmin ID to retained-upload link.
- `src/RouteTimer.Services/Persistence/IGarminConnectionRepository.cs` — connection persistence boundary.
- `src/RouteTimer.Persistence/Repositories/GarminConnectionRepository.cs` — EF implementation.
- `src/RouteTimer.Services/Garmin/GarminConnectionService.cs` — connect/MFA/status/disconnect and token rotation.
- `src/RouteTimer.Services/Garmin/GarminActivityService.cs` — cursor validation, filtering, listing, and sequential import.
- `src/RouteTimer.Persistence/Repositories/TrainingUploadRepository.cs` — atomic upload/job/Garmin-link acceptance.
- `src/RouteTimer.Persistence/Migrations/` — EF-generated `AddGarminActivityImport` migration and updated model snapshot.

### Public API and UI

- `src/RouteTimer.Contracts/Garmin/GarminContracts.cs` — token-free public request/response records.
- `src/RouteTimer.Api/Endpoints/GarminEndpoints.cs` — authenticated Garmin routes and stable problem mapping.
- `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs` and `RouteTimerApiClient.cs` — typed client operations.
- `src/RouteTimer.Client/Components/GarminConnection.razor` — login, MFA, status, and disconnect UI.
- `src/RouteTimer.Client/Components/GarminActivityPicker.razor` — pagination, selection, and import outcomes.
- `src/RouteTimer.Client/Pages/Training.razor` — integrates Garmin components without changing manual upload behavior.

---

### Task 1: Scaffold the adapter contract and canonical activity mapping

**Files:**
- Create: `garmin-adapter/pyproject.toml`
- Create: `garmin-adapter/src/routetimer_garmin/__init__.py`
- Create: `garmin-adapter/src/routetimer_garmin/models.py`
- Create: `garmin-adapter/src/routetimer_garmin/errors.py`
- Create: `garmin-adapter/src/routetimer_garmin/facade.py`
- Create: `garmin-adapter/tests/test_activity_mapping.py`
- Create: `garmin-adapter/tests/test_facade_tokens.py`

**Interfaces:**
- Consumes: `garminconnect.Garmin`, `Garmin.ActivityDownloadFormat.ORIGINAL`, `Garmin.client.loads()`, and `Garmin.client.dumps()` from version `0.3.4`.
- Produces: `AdapterActivity`, `AdapterActivityPage`, `TokenSession`, `GarminFacade`, and `map_activity(raw: Mapping[str, Any]) -> AdapterActivity | None`.

- [ ] **Step 1: Create the package metadata with exact dependencies**

```toml
[project]
name = "routetimer-garmin-adapter"
version = "0.1.0"
requires-python = ">=3.12,<3.13"
dependencies = [
  "fastapi==0.116.1",
  "garminconnect==0.3.4",
  "uvicorn==0.35.0",
]

[project.optional-dependencies]
dev = [
  "build==1.3.0",
  "httpx==0.28.1",
  "mypy==1.17.1",
  "pytest==8.4.1",
  "pytest-asyncio==1.1.0",
  "ruff==0.12.10",
]

[build-system]
requires = ["setuptools==80.9.0"]
build-backend = "setuptools.build_meta"

[tool.setuptools.packages.find]
where = ["src"]

[tool.pytest.ini_options]
pythonpath = ["src"]
testpaths = ["tests"]
asyncio_mode = "auto"

[tool.ruff]
line-length = 100
target-version = "py312"

[tool.mypy]
python_version = "3.12"
strict = true
packages = ["routetimer_garmin"]

[[tool.mypy.overrides]]
module = ["garminconnect", "garminconnect.*"]
ignore_missing_imports = true
```

- [ ] **Step 2: Write failing mapping and token round-trip tests**

```python
from routetimer_garmin.models import map_activity


def test_map_activity_accepts_only_road_and_gravel() -> None:
    road = map_activity({
        "activityId": 101,
        "activityName": "Road ride",
        "startTimeGMT": "2026-08-25 06:30:00",
        "activityType": {"typeKey": "road_biking"},
        "distance": 42000.0,
        "duration": 5400.0,
        "elevationGain": 650.0,
        "avgPower": 215.0,
    })
    gravel = map_activity({
        "activityId": 102,
        "activityName": "Gravel ride",
        "startTimeGMT": "2026-08-24 07:00:00",
        "activityType": {"typeKey": "gravel_cycling"},
    })

    assert road is not None and road.activity_type == "road-cycling"
    assert gravel is not None and gravel.activity_type == "gravel-cycling"
    for type_key in ("indoor_cycling", "e_bike_fitness", "mountain_biking", "running"):
        assert map_activity({
            "activityId": 200,
            "activityName": type_key,
            "startTimeGMT": "2026-08-23 08:00:00",
            "activityType": {"typeKey": type_key},
        }) is None
```

```python
def test_facade_loads_and_returns_tokens_without_writing_files(fake_garmin_factory) -> None:
    facade = GarminFacade(fake_garmin_factory)
    session = facade.from_tokens('{"di_token":"a","di_refresh_token":"b","di_client_id":"c"}')

    assert session.dump_tokens() == '{"di_token":"a","di_refresh_token":"b","di_client_id":"c"}'
    assert fake_garmin_factory.created[0].client.loaded_json is not None
    assert fake_garmin_factory.created[0].client.loaded_path is None
```

- [ ] **Step 3: Run the tests and verify the missing package fails**

Run: `cd garmin-adapter && python3.12 -m venv .venv && .venv/bin/pip install -e '.[dev]' && .venv/bin/pytest tests/test_activity_mapping.py tests/test_facade_tokens.py -q`

Expected: FAIL during collection because `routetimer_garmin.models` and `routetimer_garmin.facade` do not exist.

- [ ] **Step 4: Implement focused adapter models and the sole Garmin-library facade**

```python
@dataclass(frozen=True, slots=True)
class AdapterActivity:
    activity_id: str
    name: str
    started_at: datetime
    activity_type: Literal["road-cycling", "gravel-cycling"]
    distance_metres: float | None
    duration_seconds: float | None
    ascent_metres: float | None
    average_power_watts: float | None


TYPE_MAP: Final = {
    "road_biking": "road-cycling",
    "gravel_cycling": "gravel-cycling",
}


def map_activity(raw: Mapping[str, Any]) -> AdapterActivity | None:
    garmin_type = str(raw.get("activityType", {}).get("typeKey", ""))
    canonical = TYPE_MAP.get(garmin_type)
    if canonical is None:
        return None
    started_at = datetime.strptime(str(raw["startTimeGMT"]), "%Y-%m-%d %H:%M:%S").replace(tzinfo=UTC)
    return AdapterActivity(
        activity_id=str(int(raw["activityId"])),
        name=str(raw.get("activityName") or f"Garmin {raw['activityId']}").strip(),
        started_at=started_at,
        activity_type=cast(Literal["road-cycling", "gravel-cycling"], canonical),
        distance_metres=_optional_finite(raw.get("distance")),
        duration_seconds=_optional_finite(raw.get("duration")),
        ascent_metres=_optional_finite(raw.get("elevationGain")),
        average_power_watts=_optional_finite(raw.get("avgPower")),
    )
```

```python
class GarminFacade:
    def __init__(self, factory: Callable[..., Garmin] = Garmin) -> None:
        self._factory = factory

    def from_tokens(self, token_json: str) -> TokenSession:
        garmin = self._factory()
        garmin.client.loads(token_json)
        return TokenSession(garmin)


class TokenSession:
    def __init__(self, garmin: Garmin) -> None:
        self.garmin = garmin

    def dump_tokens(self) -> str:
        return self.garmin.client.dumps()
```

Keep raw dictionaries and imports from `garminconnect` in `facade.py`; `models.py`, `service.py`, and `api.py` consume facade methods and stable records only.

- [ ] **Step 5: Run adapter quality checks**

Run: `cd garmin-adapter && .venv/bin/pytest -q && .venv/bin/ruff check . && .venv/bin/ruff format --check . && .venv/bin/mypy src`

Expected: all tests pass and all three static checks exit `0`.

- [ ] **Step 6: Commit the adapter foundation**

```bash
git add garmin-adapter
git commit -m "feat: scaffold Garmin adapter"
```

---

### Task 2: Implement credential login, MFA challenges, and token validation

**Files:**
- Create: `garmin-adapter/src/routetimer_garmin/challenges.py`
- Create: `garmin-adapter/src/routetimer_garmin/service.py`
- Create: `garmin-adapter/src/routetimer_garmin/api.py`
- Create: `garmin-adapter/tests/fakes.py`
- Create: `garmin-adapter/tests/test_authentication.py`
- Create: `garmin-adapter/tests/test_auth_http.py`
- Modify: `garmin-adapter/src/routetimer_garmin/facade.py`

**Interfaces:**
- Consumes: `GarminFacade.start_login(email, password)`, `PendingLogin.resume(mfa_code)`, `TokenSession.validate()`, and `TokenSession.dump_tokens()`.
- Produces: `POST /v1/auth/login`, `POST /v1/auth/mfa`, `POST /v1/auth/validate`, `GET /health`, and `ChallengeStore` with a five-minute TTL.

- [ ] **Step 1: Write failing service tests for login, MFA, expiry, and cleanup**

```python
async def test_login_returns_token_without_retaining_credentials(fake_facade, clock) -> None:
    fake_facade.login_result = CompletedLogin("42", "Jamie", '{"di_token":"a"}')
    service = GarminService(fake_facade, ChallengeStore(clock, timedelta(minutes=5)))

    result = await service.login("rider@example.com", "secret")

    assert result.state == "connected"
    assert result.token_json == '{"di_token":"a"}'
    assert service.challenge_count == 0
    assert "secret" not in repr(result)


async def test_mfa_challenge_expires_and_clears_pending_login(fake_facade, clock) -> None:
    fake_facade.login_result = FakePendingLogin()
    service = GarminService(fake_facade, ChallengeStore(clock, timedelta(minutes=5)))
    challenge = await service.login("rider@example.com", "secret")
    clock.advance(timedelta(minutes=6))

    with pytest.raises(AdapterError, match="challenge-expired"):
        await service.complete_mfa(challenge.challenge_id or "", "123456")

    assert fake_facade.login_result.closed
    assert service.challenge_count == 0
```

Add a `caplog` test whose fake library exception contains the submitted email, password, MFA code, and token JSON. Assert none appears in adapter logs, response JSON, or exception `repr`; only the stable adapter code may be logged.

- [ ] **Step 2: Run the authentication tests and verify they fail**

Run: `cd garmin-adapter && .venv/bin/pytest tests/test_authentication.py -q`

Expected: FAIL because `GarminService` and `ChallengeStore` are not defined.

- [ ] **Step 3: Implement resumable MFA using the library's same-instance API**

```python
class PendingLogin:
    def __init__(self, garmin: Garmin) -> None:
        self._garmin: Garmin | None = garmin

    def resume(self, code: str) -> CompletedLogin:
        garmin = self._garmin
        if garmin is None:
            raise AdapterError("challenge-expired", 409)
        garmin.resume_login({}, code)
        return CompletedLogin(
            garmin_user_id=_profile_id(garmin),
            display_name=garmin.get_full_name() or garmin.display_name,
            token_json=garmin.client.dumps(),
        )

    def close(self) -> None:
        self._garmin = None
```

Add the login entry point to `GarminFacade` after defining `PendingLogin`:

```python
def start_login(self, email: str, password: str) -> CompletedLogin | PendingLogin:
    garmin = self._factory(email, password, return_on_mfa=True)
    needs_mfa, _ = garmin.login()
    garmin.username = None
    garmin.password = None
    if needs_mfa == "needs_mfa":
        return PendingLogin(garmin)
    if needs_mfa is not None:
        raise AdapterError("response-invalid", 502)
    return _completed_login(garmin)
```

Version `0.3.4` selects resumable MFA with the `Garmin(..., return_on_mfa=True)` constructor and then calls parameterless `login()`; tests pin this exact call shape. Clear the library object's `username` and `password` attributes as soon as `login()` returns, including while an MFA challenge is pending.

```python
class ChallengeStore:
    def put(self, pending: PendingLogin) -> str:
        self.prune()
        challenge_id = secrets.token_urlsafe(32)
        self._entries[challenge_id] = Challenge(pending, self._clock.now() + self._ttl)
        return challenge_id

    def take_for_attempt(self, challenge_id: str) -> PendingLogin:
        self.prune()
        challenge = self._entries.get(challenge_id)
        if challenge is None:
            raise AdapterError("challenge-expired", 409)
        return challenge.pending

    def complete(self, challenge_id: str) -> None:
        challenge = self._entries.pop(challenge_id, None)
        if challenge is not None:
            challenge.pending.close()
```

Keep a challenge after invalid MFA so the rider may retry; remove it after success, expiry, adapter shutdown, or a non-retryable error. Cleanup releases the in-memory client reference and must not call Garmin `logout()` after token creation. `repr` for secret-bearing request models must return only their type and non-secret challenge ID.

- [ ] **Step 4: Write and run failing HTTP contract tests**

```python
def test_login_http_never_returns_submitted_credentials(client, fake_service) -> None:
    fake_service.login_result = LoginResult(
        state="mfa-required",
        challenge_id="challenge-1",
        token_json=None,
        garmin_user_id=None,
        display_name=None,
    )

    response = client.post("/v1/auth/login", json={"email": "rider@example.com", "password": "secret"})

    assert response.status_code == 200
    assert response.json() == {"state": "mfa-required", "challengeId": "challenge-1"}
    assert "secret" not in response.text
    assert "rider@example.com" not in response.text
```

Run: `cd garmin-adapter && .venv/bin/pytest tests/test_auth_http.py -q`

Expected: FAIL with `404 Not Found` because the routes are not registered.

- [ ] **Step 5: Implement the FastAPI auth and health routes with stable errors**

```python
app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)

for logger_name in ("garminconnect", "garminconnect.client"):
    logging.getLogger(logger_name).setLevel(logging.CRITICAL)


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "healthy"}


@app.post("/v1/auth/login", response_model=LoginResponse)
async def login(request: LoginRequest, service: GarminService = Depends(get_service)) -> LoginResponse:
    return LoginResponse.from_result(await service.login(request.email, request.password.get_secret_value()))


@app.post("/v1/auth/mfa", response_model=LoginResponse)
async def complete_mfa(request: MfaRequest, service: GarminService = Depends(get_service)) -> LoginResponse:
    return LoginResponse.from_result(
        await service.complete_mfa(request.challenge_id, request.code.get_secret_value())
    )
```

Map adapter errors to JSON `{ "code": code, "detail": safe_detail }`; never serialize exception text from Garmin.

- [ ] **Step 6: Run adapter tests and static checks**

Run: `cd garmin-adapter && .venv/bin/pytest -q && .venv/bin/ruff check . && .venv/bin/ruff format --check . && .venv/bin/mypy src`

Expected: all checks pass.

- [ ] **Step 7: Commit authentication**

```bash
git add garmin-adapter
git commit -m "feat: add Garmin adapter authentication"
```

---

### Task 3: Implement activity pagination and secure original-FIT extraction

**Files:**
- Modify: `garmin-adapter/src/routetimer_garmin/models.py`
- Modify: `garmin-adapter/src/routetimer_garmin/facade.py`
- Modify: `garmin-adapter/src/routetimer_garmin/service.py`
- Modify: `garmin-adapter/src/routetimer_garmin/api.py`
- Create: `garmin-adapter/tests/test_activities.py`
- Create: `garmin-adapter/tests/test_fit_download.py`

**Interfaces:**
- Consumes: token JSON, integer offset, `Garmin.get_activities(start, 50)`, `Garmin.get_activity(id)`, and `Garmin.download_activity(id, ORIGINAL)`.
- Produces: `POST /v1/activities/page`, `POST /v1/activities/{activity_id}/fit`, canonical activity JSON, raw FIT body, and base64url `X-RouteTimer-Garmin-Token` response header.

- [ ] **Step 1: Write failing pagination and filtering tests**

```python
async def test_activity_page_scans_until_it_fills_fifty_allowed_rows(fake_facade) -> None:
    fake_facade.pages = [
        [raw_activity(i, "running") for i in range(1, 50)] + [raw_activity(50, "road_biking")],
        [raw_activity(i, "gravel_cycling") for i in range(51, 100)],
    ]
    service = GarminService(fake_facade, ChallengeStore.system())

    page = await service.activities('{"di_token":"a"}', offset=0)

    assert len(page.activities) == 50
    assert page.activities[0].activity_id == "50"
    assert page.activities[-1].activity_id == "99"
    assert page.next_offset == 100
    assert page.token_json == fake_facade.rotated_tokens
```

The service may scan at most ten Garmin pages per request; if fewer than 50 allowed activities exist in that window, return those rows and the next scanned offset only when Garmin returned a full final page.

- [ ] **Step 2: Write failing secure ZIP extraction tests**

```python
def test_original_download_returns_the_single_fit_member_without_extracting_paths(fake_facade) -> None:
    fake_facade.original_download = zip_bytes({"../../ride.fit": b"FIT-CONTENT"})
    result = GarminService(fake_facade, ChallengeStore.system()).download_fit(
        '{"di_token":"a"}', "123"
    )

    assert result.content == b"FIT-CONTENT"
    assert result.file_name == "123.fit"


@pytest.mark.parametrize("members", [
    {},
    {"one.fit": b"a", "two.fit": b"b"},
    {"readme.txt": b"not fit"},
])
def test_original_download_rejects_missing_or_ambiguous_fit_members(fake_facade, members) -> None:
    fake_facade.original_download = zip_bytes(members)
    with pytest.raises(AdapterError, match="response-invalid"):
        GarminService(fake_facade, ChallengeStore.system()).download_fit(
            '{"di_token":"a"}', "123"
        )
```

Also test a member declaring more than 50 MiB and a decompressed stream that crosses 50 MiB; both raise `fit-too-large` without writing a file.

- [ ] **Step 3: Run the focused tests and verify the missing methods fail**

Run: `cd garmin-adapter && .venv/bin/pytest tests/test_activities.py tests/test_fit_download.py -q`

Expected: FAIL because `activities()` and `download_fit()` do not exist.

- [ ] **Step 4: Implement bounded activity scanning and FIT extraction**

```python
PAGE_SIZE = 50
MAX_SCAN_PAGES = 10
MAX_FIT_BYTES = 50 * 1024 * 1024


def _read_single_fit(archive_bytes: bytes, activity_id: str) -> bytes:
    with ZipFile(BytesIO(archive_bytes)) as archive:
        members = [entry for entry in archive.infolist() if not entry.is_dir() and entry.filename.lower().endswith(".fit")]
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
```

Use `Garmin.get_activity(activity_id)` immediately before download and reject any type except `road_biking` or `gravel_cycling`. Validate IDs with `str(int(activity_id)) == activity_id` and require a positive integer. Return the fixed safe filename `{activity_id}.fit`; never copy the ZIP member name into an HTTP header. The extraction test uses a CRLF/path-like member name and asserts the response header contains only the validated activity ID.

- [ ] **Step 5: Implement the HTTP routes**

```python
@app.post("/v1/activities/page", response_model=ActivityPageResponse)
async def activities(request: ActivityPageRequest, service: GarminService = Depends(get_service)) -> ActivityPageResponse:
    return ActivityPageResponse.from_result(await service.activities(request.token.get_secret_value(), request.offset))


@app.post("/v1/activities/{activity_id}/fit")
async def fit(activity_id: str, request: TokenRequest, service: GarminService = Depends(get_service)) -> Response:
    result = await service.download_fit(request.token.get_secret_value(), activity_id)
    encoded_token = urlsafe_b64encode(result.token_json.encode("utf-8")).rstrip(b"=").decode("ascii")
    return Response(
        content=result.content,
        media_type="application/octet-stream",
        headers={
            "Content-Disposition": f'attachment; filename="{result.file_name}"',
            "X-RouteTimer-Garmin-Token": encoded_token,
        },
    )
```

- [ ] **Step 6: Run all adapter checks**

Run: `cd garmin-adapter && .venv/bin/pytest -q && .venv/bin/ruff check . && .venv/bin/ruff format --check . && .venv/bin/mypy src`

Expected: all checks pass.

- [ ] **Step 7: Commit activity access**

```bash
git add garmin-adapter
git commit -m "feat: list and download Garmin activities"
```

---

### Task 4: Add the .NET adapter interface and private HTTP client

**Files:**
- Create: `src/RouteTimer.Services/Garmin/GarminAdapterContracts.cs`
- Create: `src/RouteTimer.Api/Garmin/GarminAdapterClient.cs`
- Create: `tests/RouteTimer.Api.Tests/Garmin/GarminAdapterClientTests.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `tests/RouteTimer.Api.Tests/RouteTimerApiFactory.cs`

**Interfaces:**
- Consumes: adapter `/v1` JSON/binary contracts from Tasks 2–3.
- Produces: `IGarminAdapterClient.LoginAsync`, `CompleteMfaAsync`, `ValidateAsync`, `GetActivitiesAsync`, and `DownloadFitAsync`.

- [ ] **Step 1: Define the exact internal .NET contract in the failing tests**

```csharp
public interface IGarminAdapterClient
{
    Task<GarminAdapterLogin> LoginAsync(string email, string password, CancellationToken ct);
    Task<GarminAdapterLogin> CompleteMfaAsync(string challengeId, string code, CancellationToken ct);
    Task<GarminAdapterSession> ValidateAsync(string tokenJson, CancellationToken ct);
    Task<GarminAdapterActivityPage> GetActivitiesAsync(string tokenJson, int offset, CancellationToken ct);
    Task<GarminAdapterFitDownload> DownloadFitAsync(string tokenJson, string activityId, CancellationToken ct);
}

public sealed record GarminAdapterLogin(
    string State,
    string? ChallengeId,
    string? TokenJson,
    string? GarminUserId,
    string? DisplayName);

public sealed record GarminAdapterSession(string TokenJson, string? GarminUserId, string? DisplayName);
public sealed record GarminAdapterActivity(
    string ActivityId,
    string Name,
    DateTimeOffset StartedAt,
    string ActivityType,
    double? DistanceMetres,
    double? DurationSeconds,
    double? AscentMetres,
    double? AveragePowerWatts);
public sealed record GarminAdapterActivityPage(IReadOnlyList<GarminAdapterActivity> Activities, int? NextOffset, string TokenJson);
public sealed record GarminAdapterFitDownload(string FileName, Stream Content, string TokenJson) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
```

Test exact methods/paths, camel-case JSON, cancellation, stable adapter error mapping, the token response header, and response-stream disposal with a custom `HttpMessageHandler`.

- [ ] **Step 2: Run the focused API test and verify it fails**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --filter FullyQualifiedName~GarminAdapterClientTests`

Expected: FAIL because `IGarminAdapterClient` and `GarminAdapterClient` are undefined.

- [ ] **Step 3: Implement the typed client without logging request bodies or headers**

```csharp
public sealed class GarminAdapterClient(HttpClient httpClient) : IGarminAdapterClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<GarminAdapterLogin> LoginAsync(string email, string password, CancellationToken ct) =>
        SendJsonAsync<GarminAdapterLogin>("/v1/auth/login", new { email, password }, ct);

    public Task<GarminAdapterActivityPage> GetActivitiesAsync(string tokenJson, int offset, CancellationToken ct) =>
        SendJsonAsync<GarminAdapterActivityPage>("/v1/activities/page", new { token = tokenJson, offset }, ct);

    public async Task<GarminAdapterFitDownload> DownloadFitAsync(string tokenJson, string activityId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/activities/{Uri.EscapeDataString(activityId)}/fit")
        {
            Content = JsonContent.Create(new { token = tokenJson }, options: JsonOptions)
        };
        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        var encoded = response.Headers.GetValues("X-RouteTimer-Garmin-Token").Single();
        var refreshed = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(encoded));
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"') ?? $"{activityId}.fit";
        return new GarminAdapterFitDownload(fileName, await response.Content.ReadAsStreamAsync(ct), refreshed);
    }
}
```

When returning a download, transfer response ownership to a wrapper stream so disposing `GarminAdapterFitDownload` disposes the response and content. Map adapter `code` values to `GarminAdapterException` without copying unknown body fields.

- [ ] **Step 4: Register the private typed client**

```csharp
builder.Services.AddHttpClient<IGarminAdapterClient, GarminAdapterClient>(client =>
{
    var baseUrl = builder.Configuration["GarminAdapter:BaseUrl"]
        ?? throw new InvalidOperationException("GarminAdapter:BaseUrl is required.");
    client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromMinutes(2);
})
.RedactLoggedHeaders(["X-RouteTimer-Garmin-Token"]);
```

Set `GarminAdapter:BaseUrl` to `http://garmin-adapter.invalid/` in `RouteTimerApiFactory.ConfigureWebHost` before tests build the API host. Focused client tests still replace the handler and never make a network request.

- [ ] **Step 5: Run API and service builds/tests**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --filter FullyQualifiedName~GarminAdapterClientTests && dotnet build RouteTimer.slnx --no-restore`

Expected: focused tests pass and build exits `0`.

- [ ] **Step 6: Commit the adapter boundary**

```bash
git add src/RouteTimer.Services/Garmin src/RouteTimer.Api/Garmin src/RouteTimer.Api/Program.cs tests/RouteTimer.Api.Tests/Garmin
git commit -m "feat: add Garmin adapter client"
```

---

### Task 5: Add encrypted connection persistence and migration

**Files:**
- Create: `src/RouteTimer.Services/Garmin/GarminTokenProtection.cs`
- Create: `src/RouteTimer.Services/Garmin/GarminOperationGate.cs`
- Create: `src/RouteTimer.Services/Persistence/IGarminConnectionRepository.cs`
- Create: `src/RouteTimer.Persistence/Entities/GarminConnectionEntity.cs`
- Create: `src/RouteTimer.Persistence/Entities/GarminActivityImportEntity.cs`
- Create: `src/RouteTimer.Persistence/Repositories/GarminConnectionRepository.cs`
- Modify: `src/RouteTimer.Persistence/RouteTimerDbContext.cs`
- Create via EF tooling: the timestamped `AddGarminActivityImport.cs` migration and designer in `src/RouteTimer.Persistence/Migrations/`
- Modify: `src/RouteTimer.Persistence/Migrations/RouteTimerDbContextModelSnapshot.cs`
- Create: `tests/RouteTimer.Services.Tests/Garmin/GarminTokenProtectionTests.cs`
- Create: `tests/RouteTimer.Persistence.Tests/GarminConnectionRepositoryTests.cs`
- Modify: `tests/RouteTimer.Persistence.Tests/PostgresMigrationTests.cs`
- Modify: `tests/RouteTimer.Api.Tests/RouteTimerApiFactory.cs`

**Interfaces:**
- Consumes: base64 32-byte deployment key and plaintext token JSON from the adapter client.
- Produces: `IGarminTokenProtector`, `ProtectedGarminToken`, `GarminConnectionRecord`, `IGarminConnectionRepository`, `GarminOperationGate`, and EF schema for connection/import links.

- [ ] **Step 1: Write failing AES-GCM behavior tests**

```csharp
[Fact]
public void Protect_round_trips_and_detects_ciphertext_tampering()
{
    var protector = new AesGcmGarminTokenProtector(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    var protectedToken = protector.Protect("{\"di_token\":\"secret\"}");

    Assert.Equal("{\"di_token\":\"secret\"}", protector.Unprotect(protectedToken));
    Assert.DoesNotContain("secret", Convert.ToBase64String(protectedToken.Ciphertext), StringComparison.Ordinal);

    protectedToken.Ciphertext[0] ^= 0x01;
    Assert.Throws<CryptographicException>(() => protector.Unprotect(protectedToken));
}

[Theory]
[InlineData(0)]
[InlineData(16)]
[InlineData(31)]
[InlineData(33)]
public void Constructor_rejects_non_32_byte_keys(int length) =>
    Assert.Throws<ArgumentException>(() => new AesGcmGarminTokenProtector(new byte[length]));
```

- [ ] **Step 2: Run the protection tests and verify they fail**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter FullyQualifiedName~GarminTokenProtectionTests`

Expected: FAIL because `AesGcmGarminTokenProtector` is undefined.

- [ ] **Step 3: Implement token protection and operation serialization**

```csharp
public sealed record ProtectedGarminToken(int Version, byte[] Nonce, byte[] Ciphertext, byte[] Tag);

public sealed class AesGcmGarminTokenProtector(byte[] key) : IGarminTokenProtector
{
    private static readonly byte[] AdditionalData = "RouteTimer:GarminToken:1:1"u8.ToArray();
    private readonly byte[] key = key.Length == 32 ? key.ToArray() : throw new ArgumentException("Garmin token key must be 32 bytes.", nameof(key));

    public ProtectedGarminToken Protect(string tokenJson)
    {
        var plaintext = Encoding.UTF8.GetBytes(tokenJson);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AdditionalData);
        CryptographicOperations.ZeroMemory(plaintext);
        return new ProtectedGarminToken(1, nonce, ciphertext, tag);
    }

    public string Unprotect(ProtectedGarminToken protectedToken)
    {
        var plaintext = new byte[protectedToken.Ciphertext.Length];
        using var aes = new AesGcm(key, protectedToken.Tag.Length);
        aes.Decrypt(protectedToken.Nonce, protectedToken.Ciphertext, protectedToken.Tag, plaintext, AdditionalData);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
```

`GarminOperationGate` wraps a singleton `SemaphoreSlim(1, 1)` and exposes `RunAsync<T>(Func<CancellationToken, Task<T>>, CancellationToken)` with release in `finally`.

- [ ] **Step 4: Write failing repository and migration tests**

```csharp
[Fact]
public async Task Repository_round_trips_ciphertext_and_never_stores_plaintext()
{
    await using var context = CreateInMemoryContext();
    var repository = new GarminConnectionRepository(context);
    var token = new ProtectedGarminToken(1, new byte[12], [1, 2, 3], new byte[16]);

    await repository.SaveAsync(new GarminConnectionRecord("connected", "42", "Jamie", token, Instant), CancellationToken.None);

    var saved = await repository.GetAsync(CancellationToken.None);
    Assert.Equal(token, saved!.Token);
    Assert.DoesNotContain("di_token", Encoding.UTF8.GetString(context.GarminConnections.Single().Ciphertext), StringComparison.Ordinal);
}
```

Add a PostgreSQL migration test that asserts `garmin_connections` has a check constraint fixing `Id = 1`, `garmin_activity_imports."GarminActivityId"` is unique/primary, and deleting a retained upload cascades its Garmin link.

- [ ] **Step 5: Implement entities, mappings, repository, and migration**

```csharp
public sealed class GarminConnectionEntity
{
    public int Id { get; set; }
    public string State { get; set; } = "connected";
    public string? GarminUserId { get; set; }
    public string? DisplayName { get; set; }
    public int EncryptionVersion { get; set; }
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Tag { get; set; } = [];
    public DateTimeOffset? LastValidatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class GarminActivityImportEntity
{
    public string GarminActivityId { get; set; } = "";
    public Guid UploadId { get; set; }
    public string ActivityName { get; set; } = "";
    public DateTimeOffset LinkedAt { get; set; }
}
```

Map binary columns as `bytea`, state to 32 characters, IDs/names to 64/512 characters, timestamps with time zone, `GarminActivityId` as the primary key, and `UploadId` as a cascade FK to `stored_uploads`.

After the entity mappings compile, generate the migration rather than hand-editing the snapshot:

Run: `dotnet ef migrations add AddGarminActivityImport --project src/RouteTimer.Persistence --output-dir Migrations`

Expected: EF creates the migration and designer with its generated timestamp and updates `RouteTimerDbContextModelSnapshot.cs`.

- [ ] **Step 6: Register encryption and repository services with fail-closed key validation**

```csharp
var encodedGarminKey = builder.Configuration["Garmin:TokenEncryptionKey"]
    ?? throw new InvalidOperationException("Garmin:TokenEncryptionKey is required.");
byte[] garminKey;
try { garminKey = Convert.FromBase64String(encodedGarminKey); }
catch (FormatException exception) { throw new InvalidOperationException("Garmin:TokenEncryptionKey must be base64.", exception); }
builder.Services.AddSingleton<IGarminTokenProtector>(new AesGcmGarminTokenProtector(garminKey));
builder.Services.AddSingleton<GarminOperationGate>();
builder.Services.AddScoped<IGarminConnectionRepository, GarminConnectionRepository>();
```

Zero the temporary decoded key after `AesGcmGarminTokenProtector` copies it.

Set `Garmin:TokenEncryptionKey` in `RouteTimerApiFactory` to the fixed test-only base64 value `AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=` so every API test host satisfies the same fail-closed startup validation. This value is not used outside the in-memory test host.

- [ ] **Step 7: Run service and PostgreSQL tests plus pending-model verification**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter FullyQualifiedName~GarminTokenProtectionTests && dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --filter 'FullyQualifiedName~GarminConnectionRepositoryTests|FullyQualifiedName~PostgresMigrationTests'`

Expected: focused tests pass and the existing pending-model assertion reports `HasPendingModelChanges() == false`.

- [ ] **Step 8: Commit encrypted persistence**

```bash
git add src/RouteTimer.Services/Garmin src/RouteTimer.Services/Persistence/IGarminConnectionRepository.cs src/RouteTimer.Persistence src/RouteTimer.Api/Program.cs tests/RouteTimer.Services.Tests/Garmin tests/RouteTimer.Persistence.Tests
git commit -m "feat: persist encrypted Garmin sessions"
```

---

### Task 6: Expose the connection lifecycle through stable public contracts

**Files:**
- Create: `src/RouteTimer.Contracts/Garmin/GarminContracts.cs`
- Create: `src/RouteTimer.Services/Garmin/GarminConnectionService.cs`
- Create: `src/RouteTimer.Api/Endpoints/GarminEndpoints.cs`
- Create: `tests/RouteTimer.Services.Tests/Garmin/GarminConnectionServiceTests.cs`
- Create: `tests/RouteTimer.Api.Tests/Endpoints/GarminConnectionEndpointTests.cs`
- Modify: `src/RouteTimer.Contracts/Errors/ErrorCodes.cs`
- Modify: `src/RouteTimer.Api/Errors/ApiProblems.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `tests/RouteTimer.Api.Tests/Auth/AuthorizationTests.cs`

**Interfaces:**
- Consumes: `IGarminAdapterClient`, `IGarminConnectionRepository`, `IGarminTokenProtector`, `GarminOperationGate`, and `TimeProvider`.
- Produces: connection/login/MFA/disconnect services and `/api/garmin/connection` endpoints with no token fields.

- [ ] **Step 1: Write failing connection-service tests**

```csharp
[Fact]
public async Task Login_saves_only_protected_tokens_and_returns_safe_identity()
{
    adapter.LoginResult = new GarminAdapterLogin("connected", null, "token-json", "42", "Jamie");
    var result = await service.LoginAsync("rider@example.com", "secret", CancellationToken.None);

    Assert.Equal(new GarminConnectionResult("connected", "42", "Jamie", null), result);
    Assert.NotNull(repository.Saved);
    Assert.NotEqual("token-json", Encoding.UTF8.GetString(repository.Saved!.Token.Ciphertext));
    Assert.DoesNotContain("secret", result.ToString(), StringComparison.Ordinal);
}

[Fact]
public async Task Failed_refresh_marks_reconnect_required_but_transient_failure_keeps_connected()
{
    repository.Current = ConnectedRecord();
    adapter.ValidateException = new GarminAdapterException(GarminAdapterError.Authentication, "safe");
    await Assert.ThrowsAsync<GarminReconnectRequiredException>(() => service.ValidateAsync(CancellationToken.None));
    Assert.Equal("reconnect-required", repository.Current!.State);
}
```

- [ ] **Step 2: Run the focused service tests and verify failure**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter FullyQualifiedName~GarminConnectionServiceTests`

Expected: FAIL because `GarminConnectionService` is undefined.

- [ ] **Step 3: Implement the connection service inside the operation gate**

```csharp
public Task<GarminConnectionResult> LoginAsync(string email, string password, CancellationToken ct) =>
    gate.RunAsync(async token =>
    {
        var login = await adapter.LoginAsync(email, password, token);
        if (login.State == "mfa-required")
        {
            return new GarminConnectionResult("mfa-required", null, null, login.ChallengeId);
        }
        return await SaveConnectedAsync(login, token);
    }, ct);

private async Task<GarminConnectionResult> SaveConnectedAsync(GarminAdapterLogin login, CancellationToken ct)
{
    if (login.TokenJson is null) throw new GarminResponseInvalidException();
    var now = timeProvider.GetUtcNow();
    await repository.SaveAsync(new GarminConnectionRecord(
        "connected", login.GarminUserId, login.DisplayName,
        protector.Protect(login.TokenJson), now, now), ct);
    return new GarminConnectionResult("connected", login.GarminUserId, login.DisplayName, null);
}
```

Validate non-empty email/password/challenge/code at the public service boundary. Do not include secret values in validation messages.

- [ ] **Step 4: Define token-free contracts and failing endpoint tests**

```csharp
public sealed record GarminLoginRequest(string Email, string Password);
public sealed record GarminMfaRequest(string ChallengeId, string Code);
public sealed record GarminConnectionResponse(string State, string? GarminUserId, string? DisplayName, string? ChallengeId);
```

Endpoint tests cover rider authorization, `mfa-required`, successful persistence, invalid credential `400`, expired challenge `409`, rate limit `429`, adapter failure `503`, token-free JSON, and idempotent `DELETE`. A persistence-backed disconnect test proves that deleting the connection leaves Garmin import links, retained uploads, training activities, and rider-model history unchanged.

- [ ] **Step 5: Implement endpoint mapping and stable problem codes**

```csharp
public static IEndpointRouteBuilder MapGarminEndpoints(this IEndpointRouteBuilder routes)
{
    routes.MapGet("/api/garmin/connection", GetConnectionAsync);
    routes.MapPost("/api/garmin/connection/login", LoginAsync);
    routes.MapPost("/api/garmin/connection/mfa", CompleteMfaAsync);
    routes.MapDelete("/api/garmin/connection", DisconnectAsync);
    return routes;
}
```

Add all spec codes to `ErrorCodes`; add `TooManyRequests` and `ServiceUnavailable` helpers to `ApiProblems`; map Garmin exceptions in one exhaustive endpoint helper. Add `app.MapGarminEndpoints();` and authorization test data for all four routes.

- [ ] **Step 6: Run service and API tests**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter FullyQualifiedName~GarminConnectionServiceTests && dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --filter 'FullyQualifiedName~GarminConnectionEndpointTests|FullyQualifiedName~AuthorizationTests'`

Expected: all focused tests pass.

- [ ] **Step 7: Commit the connection API**

```bash
git add src/RouteTimer.Contracts/Garmin src/RouteTimer.Contracts/Errors src/RouteTimer.Services/Garmin src/RouteTimer.Api tests/RouteTimer.Services.Tests/Garmin tests/RouteTimer.Api.Tests
git commit -m "feat: expose Garmin connection lifecycle"
```

---

### Task 7: Add activity listing, opaque cursors, and imported-state projection

**Files:**
- Modify: `src/RouteTimer.Contracts/Garmin/GarminContracts.cs`
- Create: `src/RouteTimer.Services/Garmin/GarminActivityService.cs`
- Create: `src/RouteTimer.Services/Persistence/IGarminActivityImportRepository.cs`
- Create: `src/RouteTimer.Persistence/Repositories/GarminActivityImportRepository.cs`
- Modify: `src/RouteTimer.Api/Endpoints/GarminEndpoints.cs`
- Create: `tests/RouteTimer.Services.Tests/Garmin/GarminActivityServiceTests.cs`
- Create: `tests/RouteTimer.Persistence.Tests/GarminActivityImportRepositoryTests.cs`
- Create: `tests/RouteTimer.Api.Tests/Endpoints/GarminActivityEndpointTests.cs`
- Modify: `src/RouteTimer.Api/Program.cs`

**Interfaces:**
- Consumes: saved encrypted connection, adapter activity page, operation gate, and linked Garmin IDs.
- Produces: `GetActivitiesAsync(string? cursor, CancellationToken)`, opaque base64url offset cursor, public activity rows with `AlreadyImported`, and `GET /api/garmin/activities`.

- [ ] **Step 1: Write failing cursor/filter/imported-state tests**

```csharp
[Fact]
public async Task GetActivities_filters_defensively_marks_imported_and_rotates_tokens()
{
    adapter.Page = new GarminAdapterActivityPage(
        [Activity("1", "road-cycling"), Activity("2", "running"), Activity("3", "gravel-cycling")],
        50,
        "rotated-token");
    imports.LinkedIds = new HashSet<string>(StringComparer.Ordinal) { "3" };

    var page = await service.GetActivitiesAsync(null, CancellationToken.None);

    Assert.Equal(["1", "3"], page.Activities.Select(activity => activity.ActivityId));
    Assert.False(page.Activities[0].AlreadyImported);
    Assert.True(page.Activities[1].AlreadyImported);
    Assert.Equal("NTA", page.NextCursor);
    Assert.Equal("rotated-token", protector.LastProtectedPlaintext);
}

[Theory]
[InlineData("not-base64")]
[InlineData("LTE")]
[InlineData("MTAwMDAwMDAx")]
public async Task GetActivities_rejects_invalid_cursors(string cursor) =>
    await Assert.ThrowsAsync<GarminCursorInvalidException>(() => service.GetActivitiesAsync(cursor, CancellationToken.None));
```

The cursor is base64url UTF-8 decimal offset, accepts `0..100000000`, and emits no padding.

- [ ] **Step 2: Run service tests and verify failure**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter FullyQualifiedName~GarminActivityServiceTests`

Expected: FAIL because `GarminActivityService` is undefined.

- [ ] **Step 3: Implement listing under the token-rotation gate**

```csharp
public Task<GarminActivityPage> GetActivitiesAsync(string? cursor, CancellationToken ct) =>
    gate.RunAsync(async token =>
    {
        var offset = GarminCursor.Decode(cursor);
        var connection = await RequireConnectedAsync(token);
        var tokenJson = protector.Unprotect(connection.Token);
        var adapterPage = await adapter.GetActivitiesAsync(tokenJson, offset, token);
        await SaveRotatedTokenAsync(connection, adapterPage.TokenJson, token);
        var allowed = adapterPage.Activities
            .Where(activity => activity.ActivityType is "road-cycling" or "gravel-cycling")
            .ToArray();
        var linked = await imports.GetLinkedIdsAsync(allowed.Select(activity => activity.ActivityId).ToArray(), token);
        return new GarminActivityPage(
            allowed.Select(activity => GarminActivity.FromAdapter(activity, linked.Contains(activity.ActivityId))).ToArray(),
            GarminCursor.Encode(adapterPage.NextOffset));
    }, ct);
```

Zero UTF-8 token buffers used during unprotect/reprotect where the BCL API exposes mutable bytes.

- [ ] **Step 4: Implement repository projection and endpoint contracts**

```csharp
public sealed record GarminActivitySummaryResponse(
    string ActivityId,
    string Name,
    DateTimeOffset StartedAt,
    string ActivityType,
    double? DistanceMetres,
    double? DurationSeconds,
    double? AscentMetres,
    double? AveragePowerWatts,
    bool AlreadyImported);
public sealed record GarminActivityPageResponse(IReadOnlyList<GarminActivitySummaryResponse> Activities, string? NextCursor);
```

`GetLinkedIdsAsync` performs one `WHERE GarminActivityId IN (...)` query and returns an ordinal `HashSet<string>`. Map cursor errors to `400 garmin-cursor-invalid`, no connection to `409 garmin-connection-required`, and failed refresh to `409 garmin-reconnect-required`.

- [ ] **Step 5: Run service, persistence, and API tests**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter FullyQualifiedName~GarminActivityServiceTests && dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --filter FullyQualifiedName~GarminActivityImportRepositoryTests && dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --filter FullyQualifiedName~GarminActivityEndpointTests`

Expected: all focused tests pass.

- [ ] **Step 6: Commit activity listing**

```bash
git add src/RouteTimer.Contracts/Garmin src/RouteTimer.Services/Garmin src/RouteTimer.Services/Persistence/IGarminActivityImportRepository.cs src/RouteTimer.Persistence/Repositories/GarminActivityImportRepository.cs src/RouteTimer.Api tests
git commit -m "feat: list Garmin cycling activities"
```

---

### Task 8: Make Garmin imports idempotent and feed the existing upload pipeline

**Files:**
- Modify: `src/RouteTimer.Services/Training/TrainingUploadService.cs`
- Modify: `src/RouteTimer.Services/Persistence/ITrainingUploadRepository.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/TrainingUploadRepository.cs`
- Modify: `src/RouteTimer.Services/Garmin/GarminActivityService.cs`
- Modify: `src/RouteTimer.Contracts/Garmin/GarminContracts.cs`
- Modify: `src/RouteTimer.Api/Endpoints/GarminEndpoints.cs`
- Modify: `src/RouteTimer.Api/Endpoints/TrainingEndpoints.cs`
- Modify: `tests/RouteTimer.Services.Tests/Training/TrainingUploadServiceTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Garmin/GarminImportServiceTests.cs`
- Modify: `tests/RouteTimer.Persistence.Tests/TrainingUploadRepositoryTests.cs`
- Modify: `tests/RouteTimer.Api.Tests/Endpoints/GarminActivityEndpointTests.cs`

**Interfaces:**
- Consumes: selected Garmin IDs, adapter re-read/download, existing 50 MiB `TrainingUploadService`, stored-upload hash uniqueness, and `ParseTraining` job creation.
- Produces: one ordered `GarminImportResult` per selection, atomic Garmin link creation, and `POST /api/garmin/activities/import`.

- [ ] **Step 1: Write failing upload acceptance tests for external ID and hash duplicates**

```csharp
[Fact]
public async Task AcceptAsync_returns_existing_ids_for_a_Garmin_id_or_hash_duplicate()
{
    var first = await repository.AcceptAsync(Upload("hash"), Now,
        new GarminActivitySource("123", "Road ride"), CancellationToken.None);
    var sameId = await repository.AcceptAsync(Upload("different-hash"), Now,
        new GarminActivitySource("123", "Road ride renamed"), CancellationToken.None);
    var sameHash = await repository.AcceptAsync(Upload("hash"), Now,
        new GarminActivitySource("456", "Gravel ride"), CancellationToken.None);

    Assert.Equal(TrainingUploadAcceptanceOutcome.Accepted, first.Outcome);
    Assert.Equal(TrainingUploadAcceptanceOutcome.AlreadyImported, sameId.Outcome);
    Assert.Equal(TrainingUploadAcceptanceOutcome.DuplicateHash, sameHash.Outcome);
    Assert.Equal(first.UploadId, sameId.UploadId);
    Assert.Equal(first.UploadId, sameHash.UploadId);
    Assert.Equal(first.JobId, sameId.JobId);
    Assert.Equal(first.JobId, sameHash.JobId);
}
```

Add a PostgreSQL concurrency test launching two contexts against the same Garmin ID and asserting one upload, one ParseTraining job, one link, one `Accepted`, and one `AlreadyImported` result.

- [ ] **Step 2: Run repository tests and verify failure**

Run: `dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --filter FullyQualifiedName~TrainingUploadRepositoryTests`

Expected: FAIL because `GarminActivitySource` and `TrainingUploadAcceptanceOutcome` are undefined.

- [ ] **Step 3: Extend upload acceptance atomically**

```csharp
public sealed record GarminActivitySource(string ActivityId, string ActivityName);
public enum TrainingUploadAcceptanceOutcome { Accepted, AlreadyImported, DuplicateHash }
public sealed record TrainingUploadAcceptance(
    TrainingUploadAcceptanceOutcome Outcome,
    Guid UploadId,
    Guid JobId);

public interface ITrainingUploadRepository
{
    Task<TrainingUploadAcceptance> AcceptAsync(
        StoredUpload upload,
        DateTimeOffset now,
        GarminActivitySource? garminSource,
        CancellationToken cancellationToken);
}
```

For PostgreSQL, acquire `pg_advisory_xact_lock(hashtext(activityId))` before checking/inserting a Garmin source. Insert or locate the stored upload by `(Kind, Sha256)`, insert a ParseTraining job only for a new upload, locate the existing ParseTraining job for a duplicate hash, then insert the Garmin link. The unique link remains the final database invariant. The InMemory path performs the same state transitions without raw SQL.

- [ ] **Step 4: Update the training upload service while preserving manual API output**

```csharp
public sealed record TrainingUpload(string FileName, Stream Content, GarminActivitySource? GarminSource = null);

var stored = await repository.AcceptAsync(
    new StoredUpload(uploadId, upload.FileName, "fit", content, hash, now),
    now,
    upload.GarminSource,
    cancellationToken);
results.Add(new TrainingUploadResult(
    upload.FileName,
    stored.Outcome switch
    {
        TrainingUploadAcceptanceOutcome.Accepted => UploadOutcome.Accepted,
        _ => UploadOutcome.Duplicate
    },
    stored.UploadId,
    stored.JobId,
    stored.Outcome == TrainingUploadAcceptanceOutcome.Accepted ? null : "duplicate-upload"));
```

In `TrainingEndpoints.ToResponse`, continue returning null IDs for manual duplicate results even though the internal result now carries linked IDs.

- [ ] **Step 5: Write failing sequential partial-import tests**

```csharp
[Fact]
public async Task ImportAsync_continues_after_one_download_failure_and_preserves_input_order()
{
    adapter.Activities = [Activity("1", "road-cycling"), Activity("2", "gravel-cycling")];
    adapter.Downloads["1"] = new GarminAdapterException(GarminAdapterError.Unavailable, "safe");
    adapter.Downloads["2"] = Fit("gravel.fit", "fit-bytes", "rotated-token");

    var results = await service.ImportAsync(["1", "2"], CancellationToken.None);

    Assert.Equal(["1", "2"], results.Select(result => result.ActivityId));
    Assert.Equal("download-failed", results[0].Outcome);
    Assert.Equal("accepted", results[1].Outcome);
    Assert.Equal(["1", "2"], adapter.RequestedActivityIds);
}
```

Also test zero IDs, eleven IDs, duplicates, imported short-circuit without download, disallowed re-read type, cancellation after an accepted first item, safe filename sanitization, and token rotation after every successful adapter response.

- [ ] **Step 6: Implement sequential import orchestration**

```csharp
public async Task<IReadOnlyList<GarminImportResult>> ImportAsync(IReadOnlyList<string> activityIds, CancellationToken ct)
{
    ValidateSelection(activityIds);
    var results = new List<GarminImportResult>(activityIds.Count);
    foreach (var activityId in activityIds)
    {
        ct.ThrowIfCancellationRequested();
        results.Add(await ImportOneAsync(activityId, ct));
    }
    return results;
}
```

`ImportOneAsync` checks the existing link, re-reads the summary through the adapter, rejects a non-road/gravel type, downloads and disposes the FIT, builds a sanitized `<name>-<id>.fit`, and calls `TrainingUploadService.AcceptAsync` with `GarminActivitySource`. Map the repository outcomes exactly to `accepted`, `already-imported`, and `duplicate`.

- [ ] **Step 7: Add public import contracts and endpoint**

```csharp
public sealed record GarminImportRequest(IReadOnlyList<string> ActivityIds);
public sealed record GarminImportBatchResponse(IReadOnlyList<GarminImportResultResponse> Activities);
public sealed record GarminImportResultResponse(
    string ActivityId,
    string? Name,
    string Outcome,
    Guid? UploadId,
    Guid? JobId,
    string? ErrorCode);
```

Return `202 Accepted` for a structurally valid batch even with per-item failures. Return `400 garmin-import-limit` for count/duplicate validation. Add rider authorization coverage.

- [ ] **Step 8: Run training, Garmin service, persistence, and API tests**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter 'FullyQualifiedName~TrainingUploadServiceTests|FullyQualifiedName~GarminImportServiceTests' && dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --filter FullyQualifiedName~TrainingUploadRepositoryTests && dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --filter 'FullyQualifiedName~GarminActivityEndpointTests|FullyQualifiedName~TrainingEndpointTests'`

Expected: all focused tests pass and manual duplicate endpoint assertions remain unchanged.

- [ ] **Step 9: Commit import orchestration**

```bash
git add src/RouteTimer.Services src/RouteTimer.Persistence src/RouteTimer.Contracts/Garmin src/RouteTimer.Api tests
git commit -m "feat: import Garmin FIT activities"
```

---

### Task 9: Extend the typed Blazor API client

**Files:**
- Modify: `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs`
- Modify: `src/RouteTimer.Client/Api/RouteTimerApiClient.cs`
- Modify: `tests/RouteTimer.Client.Tests/Api/RouteTimerApiClientTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs`

**Interfaces:**
- Consumes: public Garmin contracts from Tasks 6–8.
- Produces: typed client methods used by the connection and picker components.

- [ ] **Step 1: Write failing HTTP-client tests for every Garmin operation**

```csharp
[Fact]
public async Task GetGarminActivitiesAsync_encodes_the_opaque_cursor()
{
    var client = CreateApiClient((request, _) =>
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/garmin/activities?cursor=NTA", request.RequestUri!.PathAndQuery);
        return Task.FromResult(JsonResponse(new GarminActivityPageResponse([], null)));
    });

    await client.GetGarminActivitiesAsync("NTA", CancellationToken.None);
}
```

Add tests for status GET, login POST, MFA POST, disconnect DELETE, list GET without cursor, and import POST with ordered IDs. Assert secret requests are JSON bodies, never query strings.

- [ ] **Step 2: Run focused client tests and verify compile failure**

Run: `dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --filter FullyQualifiedName~RouteTimerApiClientTests --blame-hang-timeout 30s`

Expected: FAIL to compile because Garmin methods are absent.

- [ ] **Step 3: Add exact interface methods and typed implementations**

```csharp
Task<GarminConnectionResponse> GetGarminConnectionAsync(CancellationToken ct);
Task<GarminConnectionResponse> LoginGarminAsync(GarminLoginRequest request, CancellationToken ct);
Task<GarminConnectionResponse> CompleteGarminMfaAsync(GarminMfaRequest request, CancellationToken ct);
Task DisconnectGarminAsync(CancellationToken ct);
Task<GarminActivityPageResponse> GetGarminActivitiesAsync(string? cursor, CancellationToken ct);
Task<GarminImportBatchResponse> ImportGarminActivitiesAsync(GarminImportRequest request, CancellationToken ct);
```

```csharp
public Task<GarminActivityPageResponse> GetGarminActivitiesAsync(string? cursor, CancellationToken ct)
{
    var path = cursor is null
        ? "/api/garmin/activities"
        : $"/api/garmin/activities?cursor={Uri.EscapeDataString(cursor)}";
    return GetRequiredAsync<GarminActivityPageResponse>(path, ct);
}
```

Extend `FakeRouteTimerApiClient` with delegates and request-capture collections for all six operations; default status is `not-connected`, default page is empty, and default import result is empty.

- [ ] **Step 4: Run the bounded client API test assembly**

Run: `dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --filter FullyQualifiedName~RouteTimerApiClientTests --blame-hang-timeout 30s`

Expected: all `RouteTimerApiClientTests` pass within the hang timeout.

- [ ] **Step 5: Commit the client boundary**

```bash
git add src/RouteTimer.Client/Api tests/RouteTimer.Client.Tests/Api tests/RouteTimer.Client.Tests/Fakes
git commit -m "feat: add Garmin client API"
```

---

### Task 10: Build the Garmin connection and activity-picker UI

**Files:**
- Create: `src/RouteTimer.Client/Components/GarminConnection.razor`
- Create: `src/RouteTimer.Client/Components/GarminConnection.razor.css`
- Create: `src/RouteTimer.Client/Components/GarminActivityPicker.razor`
- Create: `src/RouteTimer.Client/Components/GarminActivityPicker.razor.css`
- Modify: `src/RouteTimer.Client/Pages/Training.razor`
- Modify: `src/RouteTimer.Client/Pages/Training.razor.css`
- Modify: `src/RouteTimer.Client/Formatting/RouteTimerFormat.cs`
- Modify: `src/RouteTimer.Client/Formatting/RouteTimerText.cs`
- Create: `tests/RouteTimer.Client.Tests/Components/GarminConnectionTests.cs`
- Create: `tests/RouteTimer.Client.Tests/Components/GarminActivityPickerTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/TrainingPageTests.cs`

**Interfaces:**
- Consumes: `IRouteTimerApiClient` Garmin methods and public contracts.
- Produces: token-free login/MFA/disconnect component, paginated selectable picker, import outcomes, and Training page integration.

- [ ] **Step 1: Write failing connection component tests**

```csharp
[Fact]
public void Connection_switches_from_credentials_to_MFA_and_never_renders_the_password_after_submit()
{
    api.OnGetGarminConnectionAsync = _ => Task.FromResult(NotConnected());
    api.OnLoginGarminAsync = (_, _) => Task.FromResult(MfaRequired("challenge-1"));
    var cut = Render<GarminConnection>();

    cut.Find("[data-testid=garmin-email]").Change("rider@example.com");
    cut.Find("[data-testid=garmin-password]").Change("secret");
    cut.Find("[data-testid=garmin-login]").Click();

    cut.WaitForAssertion(() =>
    {
        Assert.NotNull(cut.Find("[data-testid=garmin-mfa-code]"));
        Assert.DoesNotContain("secret", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("rider@example.com", cut.Markup, StringComparison.Ordinal);
    });
}
```

Also cover loading, invalid credentials, challenge expiry returning to credentials, connected safe identity, duplicate submission disabling, disconnect confirmation, and disposal cancellation.

- [ ] **Step 2: Run connection component tests and verify failure**

Run: `dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --filter FullyQualifiedName~GarminConnectionTests --blame-hang-timeout 30s`

Expected: FAIL because `GarminConnection` is undefined.

- [ ] **Step 3: Implement accessible login/MFA/status/disconnect states**

```razor
@if (connection.State == "not-connected")
{
    <EditForm Model="credentials" OnValidSubmit="LoginAsync" data-testid="garmin-login-form">
        <label for="garmin-email">Garmin email</label>
        <InputText id="garmin-email" data-testid="garmin-email" @bind-Value="credentials.Email" autocomplete="username" />
        <label for="garmin-password">Garmin password</label>
        <InputText id="garmin-password" data-testid="garmin-password" type="password" @bind-Value="credentials.Password" autocomplete="current-password" />
        <p>RouteTimer uses these credentials only to establish the Garmin connection. They are not saved.</p>
        <button data-testid="garmin-login" disabled="@isSubmitting">Connect Garmin</button>
    </EditForm>
}
```

After submission, overwrite password and MFA fields with `string.Empty` in `finally`. Use `ProblemMessage` for safe failures and a component-owned `CancellationTokenSource` cancelled on disposal.

- [ ] **Step 4: Write failing picker tests**

```csharp
[Fact]
public void Picker_disables_imported_rows_enforces_ten_and_appends_load_more()
{
    api.GarminPages.Enqueue(Page(Enumerable.Range(1, 11).Select(Activity).ToArray(), "NTA"));
    api.GarminPages.Enqueue(Page([Activity(12)], null));
    var cut = Render<GarminActivityPicker>();

    cut.WaitForAssertion(() => Assert.Equal(11, cut.FindAll("[data-testid=garmin-activity-row]").Count));
    foreach (var checkbox in cut.FindAll("[data-testid=garmin-activity-select]").Take(10)) checkbox.Change(true);
    Assert.True(cut.FindAll("[data-testid=garmin-activity-select]")[10].HasAttribute("disabled"));

    cut.Find("[data-testid=garmin-load-more]").Click();
    cut.WaitForAssertion(() => Assert.Equal(12, cut.FindAll("[data-testid=garmin-activity-row]").Count));
}
```

Also cover newest-first rendering, optional metric formatting, empty/failure/retry states, per-item accepted/already-imported/duplicate/invalid-fit/download-failed output, job polling for accepted rows, and cancellation on disposal.

- [ ] **Step 5: Implement picker state and import refresh**

```csharp
private readonly HashSet<string> selectedIds = new(StringComparer.Ordinal);

private void SetSelected(string activityId, bool selected)
{
    if (selected) { if (selectedIds.Count < UploadLimits.MaximumTrainingFiles) selectedIds.Add(activityId); }
    else selectedIds.Remove(activityId);
}

private async Task ImportAsync()
{
    if (isImporting || selectedIds.Count is < 1 or > UploadLimits.MaximumTrainingFiles) return;
    isImporting = true;
    try
    {
        importResult = await Api.ImportGarminActivitiesAsync(new GarminImportRequest(selectedIds.ToArray()), pageCancellation.Token);
        selectedIds.Clear();
        await LoadFirstPageAsync();
        await Imported.InvokeAsync(importResult);
    }
    finally { isImporting = false; }
}
```

Use `RouteTimerFormat` for distance/duration/ascent/power and `RouteTimerText` for canonical type/outcome labels. Imported rows remain visible but disabled.

- [ ] **Step 6: Integrate components into Training without coupling failure states**

Render `<GarminConnection ConnectionChanged="HandleGarminConnectionChanged" />` and render the picker only for `connected`. On accepted imports, use the existing job poller and refresh activity/model sections without reloading the page. Do not alter manual file selection or deletion controls.

- [ ] **Step 7: Run bounded component and Training page tests**

Run: `dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --filter 'FullyQualifiedName~GarminConnectionTests|FullyQualifiedName~GarminActivityPickerTests|FullyQualifiedName~TrainingPageTests' --blame-hang-timeout 45s --logger 'console;verbosity=normal'`

Expected: all focused tests pass within the hang timeout. If the pre-existing hang reproduces, preserve the blame artifacts and identify the exact test before modifying unrelated code.

- [ ] **Step 8: Commit the Garmin UI**

```bash
git add src/RouteTimer.Client tests/RouteTimer.Client.Tests
git commit -m "feat: add Garmin activity picker"
```

---

### Task 11: Containerize the adapter and verify the complete feature

**Files:**
- Create: `garmin-adapter/Dockerfile`
- Create: `garmin-adapter/.dockerignore`
- Modify: `docker-compose.yml`
- Modify: `deploy/README.md`
- Create: `docs/garmin-smoke-test.md`
- Create: `tests/RouteTimer.EndToEnd.Tests/GarminDeploymentTests.cs`
- Modify: `tests/RouteTimer.EndToEnd.Tests/RouteTimer.EndToEnd.Tests.csproj` only if the current project lacks test SDK/xUnit references.

**Interfaces:**
- Consumes: adapter ASGI app, private Compose network, RouteTimer Garmin configuration, and the completed public API/UI.
- Produces: internal-only healthy adapter service, deployment instructions, opt-in smoke procedure, and fresh verification evidence.

- [ ] **Step 1: Write failing deployment-structure tests**

```csharp
[Fact]
public void Compose_keeps_the_Garmin_adapter_private_and_gives_it_no_database_or_key()
{
    var compose = File.ReadAllText(FindRepositoryFile("docker-compose.yml"));
    Assert.Contains("routetimer-garmin-adapter:", compose, StringComparison.Ordinal);
    Assert.DoesNotContain("routetimer-garmin-adapter:\n    ports:", compose, StringComparison.Ordinal);
    var adapterBlock = Between(compose, "  routetimer-garmin-adapter:", "  routetimer:");
    Assert.DoesNotContain("ConnectionStrings__RouteTimer", adapterBlock, StringComparison.Ordinal);
    Assert.DoesNotContain("Garmin__TokenEncryptionKey", adapterBlock, StringComparison.Ordinal);
    Assert.Contains("routetimer-private", adapterBlock, StringComparison.Ordinal);
    Assert.Contains("read_only: true", adapterBlock, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the deployment test and verify failure**

Run: `dotnet test tests/RouteTimer.EndToEnd.Tests/RouteTimer.EndToEnd.Tests.csproj --filter FullyQualifiedName~GarminDeploymentTests`

Expected: FAIL because the adapter service is absent from Compose.

- [ ] **Step 3: Add the adapter image and private Compose service**

```dockerfile
FROM python:3.12-slim
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /app
RUN useradd --create-home --uid 10001 adapter
COPY pyproject.toml ./
COPY src ./src
RUN pip install --no-cache-dir .
USER adapter
EXPOSE 8081
CMD ["uvicorn", "routetimer_garmin.api:app", "--host", "0.0.0.0", "--port", "8081", "--no-access-log"]
```

```yaml
  routetimer-garmin-adapter:
    build:
      context: ./garmin-adapter
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8081/health', timeout=3)"]
      interval: 10s
      timeout: 5s
      retries: 6
    read_only: true
    tmpfs:
      - /tmp:size=16m,mode=1777
    security_opt:
      - no-new-privileges:true
    networks:
      - routetimer-private

  routetimer:
    depends_on:
      routetimer-garmin-adapter:
        condition: service_healthy
    environment:
      GarminAdapter__BaseUrl: http://routetimer-garmin-adapter:8081
      Garmin__TokenEncryptionKey: ${ROUTETIMER_GARMIN_TOKEN_KEY:?set ROUTETIMER_GARMIN_TOKEN_KEY}
```

Keep `routetimer-private` internal. Do not add adapter volumes, ports, database settings, or the token key.

- [ ] **Step 4: Document deployment secret generation and opt-in smoke test**

Document key generation as `openssl rand -base64 32`, safe secret rotation consequences, login/MFA, activity pagination, one road/gravel import, parse/model completion, saved-token reuse after app restart, disconnect, and reconnect. State explicitly that screenshots/log bundles must be checked for secrets before sharing.

- [ ] **Step 5: Run Python verification**

Run: `cd garmin-adapter && .venv/bin/ruff format --check . && .venv/bin/ruff check . && .venv/bin/mypy src && .venv/bin/pytest -q && .venv/bin/python -m build`

Expected: every command exits `0` with no failures and `dist/` contains both a source distribution and wheel.

- [ ] **Step 6: Run full .NET verification with bounded client diagnostics**

Run: `dotnet build RouteTimer.slnx -c Release --no-restore`

Expected: exit `0` with zero warnings and errors.

Run: `dotnet test tests/RouteTimer.Domain.Tests/RouteTimer.Domain.Tests.csproj -c Release --no-build && dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj -c Release --no-build && dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj -c Release --no-build && dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj -c Release --no-build && dotnet test tests/RouteTimer.EndToEnd.Tests/RouteTimer.EndToEnd.Tests.csproj -c Release --no-build`

Expected: all discovered non-client tests pass.

Run: `dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj -c Release --no-build --blame-hang-timeout 60s --logger 'console;verbosity=normal'`

Expected: all client tests pass; if the baseline hang remains, the command terminates with blame evidence naming the test and the handoff reports that exact unresolved baseline condition.

- [ ] **Step 7: Verify migrations and containers**

Run: `dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj -c Release --no-build --filter FullyQualifiedName~PostgresMigrationTests`

Expected: migration and pending-model tests pass.

Run: `ROUTETIMER_DB_PASSWORD=test KEYCLOAK_AUTHORITY=https://keycloak.invalid/realms/routetimer ROUTETIMER_HOSTNAME=routetimer.invalid ROUTETIMER_GARMIN_TOKEN_KEY=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA= docker compose config --quiet`

Expected: exit `0` and no adapter host port in `docker compose config` output.

Run: `docker build -t routetimer-garmin-adapter:test garmin-adapter && docker build --build-arg KEYCLOAK_AUTHORITY=https://keycloak.invalid/realms/routetimer --build-arg ROUTETIMER_HOSTNAME=routetimer.invalid -t routetimer:test .`

Expected: both images build successfully.

- [ ] **Step 8: Review requirements against the spec and inspect the final diff**

Run: `git diff main...HEAD --check && git status --short --branch && git log --oneline --decorate main..HEAD`

Expected: no whitespace errors, no uncommitted implementation files, and focused commits for all prior tasks.

Manually check each acceptance criterion in `docs/superpowers/specs/2026-08-25-garmin-activity-import-design.md` against a test or verification command above. Record any unmet criterion instead of claiming completion.

- [ ] **Step 9: Commit deployment and verification documentation**

```bash
git add garmin-adapter/Dockerfile garmin-adapter/.dockerignore docker-compose.yml deploy/README.md docs/garmin-smoke-test.md tests/RouteTimer.EndToEnd.Tests
git commit -m "chore: deploy Garmin activity adapter"
```
