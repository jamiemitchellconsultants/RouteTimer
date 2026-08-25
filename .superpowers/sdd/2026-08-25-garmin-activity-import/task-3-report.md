# Task 3 Report: activity pagination, revalidation, and FIT extraction

## Implementation

- Added stable `AdapterActivityBatch` records and redacted page token repr.
- Kept all Garmin/raw-dictionary operations in `facade.py`: paged activity reads, single-activity reads, and ORIGINAL downloads. Third-party failures become secret-safe `AdapterError` values.
- Added async service operations for bounded activity pagination, single-activity summary revalidation, and in-memory original-FIT extraction.
- Pagination requests 50 rows at a time, exposes at most 50 allowed records, scans no more than ten Garmin pages, and emits `nextOffset` only when the final scanned Garmin page was full.
- Validates positive canonical numeric activity IDs before Garmin calls. Summary and FIT paths reject non-road/gravel activities with `activity-not-allowed` / 422; FIT re-reads the activity immediately before the ORIGINAL download.
- Extracts exactly one non-directory `.fit` member in memory. It rejects invalid/ambiguous archives, declared entries over 50 MiB, and bounded reads over 50 MiB. The HTTP filename is always `<validated-id>.fit`.
- Added `POST /v1/activities/page`, private `POST /v1/activities/{activity_id}/summary`, and `POST /v1/activities/{activity_id}/fit`. Token bodies and response repr remain redacted; the FIT token header is unpadded base64url.

## Files

- Modified `garmin-adapter/src/routetimer_garmin/models.py`
- Modified `garmin-adapter/src/routetimer_garmin/facade.py`
- Modified `garmin-adapter/src/routetimer_garmin/service.py`
- Modified `garmin-adapter/src/routetimer_garmin/api.py`
- Added `garmin-adapter/tests/test_activities.py`
- Added `garmin-adapter/tests/test_fit_download.py`

## TDD evidence

Initial baseline:

```text
$ .venv/bin/python --version
Python 3.12.13
$ .venv/bin/pytest -q
25 passed in 0.23s
```

Initial RED command:

```text
$ .venv/bin/pytest tests/test_activities.py tests/test_fit_download.py -q
18 failed in 0.21s
```

The expected failures were missing `GarminService.activities`, `activity_summary`, and `download_fit`, missing `ZipFile` extraction support, and absent activity routes. A follow-up RED for malformed list entries was:

```text
$ .venv/bin/pytest tests/test_activities.py tests/test_fit_download.py -q
1 failed, 19 passed in 0.20s
```

It caught a raw non-dictionary member being silently skipped. A later RED verified page-result token redaction:

```text
$ .venv/bin/pytest tests/test_activities.py::test_activity_page_scans_until_it_fills_fifty_allowed_rows -q
1 failed in 0.17s
```

GREEN after the final redaction fix:

```text
$ .venv/bin/pytest tests/test_activities.py tests/test_fit_download.py -q
21 passed in 0.18s
```

## Full checks

```text
$ .venv/bin/pytest -q
46 passed in 0.19s
$ .venv/bin/ruff check .
All checks passed!
$ .venv/bin/ruff format --check .
16 files already formatted
$ .venv/bin/mypy src
Success: no issues found in 7 source files
$ git diff --check
(exit 0; no output)
```

## Self-review

Reviewed against the Task 3 brief and Binding Ruling 2:

- Raw Garmin dictionaries and the private client remain contained in the facade/session layer.
- Only `road_biking` and `gravel_cycling` are surfaced; other types cannot become visible activities or downloadable FITs.
- FIT paths never use a ZIP member name in headers and never write archive contents to disk.
- Test data is fully local; no test contacts Garmin.
- Result/request token repr is redacted, including the new activity page result.

## Concerns

None. The bounded-read ZIP test replaces `ZipFile` with an in-memory test double to exercise the otherwise impractical post-decompression branch without writing a 50 MiB fixture to disk.

## Commit

`feat: list and download Garmin activities` (Task 3 implementation commit)
