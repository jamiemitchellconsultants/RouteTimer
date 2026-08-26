# Garmin course endpoint verification spike

**Date:** 2026-08-26
**Status:** Not executed live — this implementation ran in a non-interactive environment with no
Garmin account credentials available. Manual verification against a real account is still required
before relying on the course push in production.

## What was verified instead

The two-step flow implemented in this branch —

1. `POST /course-service/course/import` (multipart GPX upload, returns a parsed skeleton with
   `geoPoints` but no distance, bounding box, or start point)
2. `POST /course-service/course` (JSON save, returns the stored course with `courseId`)

— is not verified against a live Garmin Connect account by this session. It is based on multiple
independent public reference implementations that describe using this exact sequence against
`connect.garmin.com`, including a documented request/response shape for the `import` step (a
`geoPoints` list with `latitude`/`longitude`/optional `elevation`, and no `distance`, `boundingBox`,
or `startPoint`) and for the `course` save step (the full payload shape implemented in
`garmin-adapter/src/routetimer_garmin/courses.py`).

## Required manual step before production use

Before enabling the "Send to Garmin" feature for real use, run the spike from
`docs/superpowers/plans/2026-08-26-predictions-route-builder.md` (Task 1) by hand:

1. Start a Python shell with the adapter's dependencies installed.
2. Log in with `garminconnect.Garmin(email, password)`.
3. POST a small test GPX to `/course-service/course/import` and confirm the response contains a
   non-empty `geoPoints` list.
4. Build and POST the save payload to `/course-service/course` and confirm a `courseId` comes back.
5. Confirm the course is visible at `https://connect.garmin.com/modern/course/<courseId>`.
6. Delete the spike course.
7. Update this document with the outcome, including the exact response keys observed, or any
   discrepancy from what `courses.py` assumes.

## Fallback if the endpoints do not work as implemented

The GPX export feature (`GET /api/predictions/{id}/gpx`) does not depend on this flow and works
regardless. A rider can always download the GPX and import it into Garmin Connect manually through
the web UI's own Import button. If the live spike finds the endpoints have changed shape, update
`build_course_payload` in `garmin-adapter/src/routetimer_garmin/courses.py` to match, or disable the
"Send to Garmin" button in `PredictionDetail.razor` until it is fixed.
