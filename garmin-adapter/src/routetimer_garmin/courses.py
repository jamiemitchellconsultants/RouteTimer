"""Course payload construction for Garmin Connect.

Garmin's course import is a two-step, undocumented flow. POST /course-service/course/import
parses an uploaded GPX and returns geoPoints but no distance, bounding box, or start point;
POST /course-service/course saves the enriched payload. This module owns the arithmetic
between the two steps and nothing else, so it stays testable without a Garmin session.
"""

from __future__ import annotations

import math
from typing import Any, Final

EARTH_RADIUS_M: Final = 6_371_000.0

ACTIVITY_TYPE_IDS: Final[dict[str, int]] = {
    "cycling": 2,
    "gravel_cycling": 4,
    "mountain_biking": 5,
    "road_biking": 10,
}

DEFAULT_ACTIVITY_TYPE: Final = "road_biking"


class CoursePayloadError(Exception):
    """The parsed course cannot be turned into a valid create-course payload."""


def haversine_metres(first: dict[str, float], second: dict[str, float]) -> float:
    lat1, lon1 = math.radians(first["latitude"]), math.radians(first["longitude"])
    lat2, lon2 = math.radians(second["latitude"]), math.radians(second["longitude"])
    dlat, dlon = lat2 - lat1, lon2 - lon1
    a = math.sin(dlat / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin(dlon / 2) ** 2
    return 2 * EARTH_RADIUS_M * math.asin(math.sqrt(a))


def initial_bearing(first: dict[str, float], second: dict[str, float]) -> float:
    lat1, lat2 = math.radians(first["latitude"]), math.radians(second["latitude"])
    dlon = math.radians(second["longitude"] - first["longitude"])
    x = math.sin(dlon) * math.cos(lat2)
    y = math.cos(lat1) * math.sin(lat2) - math.sin(lat1) * math.cos(lat2) * math.cos(dlon)
    return (math.degrees(math.atan2(x, y)) + 360) % 360


def build_course_payload(
    parsed: dict[str, Any],
    *,
    course_name: str,
    activity_type_id: int,
    description: str | None,
    elevation_gain_metres: float,
    elevation_loss_metres: float,
) -> dict[str, Any]:
    points = list(parsed.get("geoPoints") or [])
    if len(points) < 2:
        raise CoursePayloadError("The parsed course has fewer than two geo points.")

    total = 0.0
    for index, point in enumerate(points):
        if index == 0:
            point["distance"] = 0.0
        else:
            total += haversine_metres(points[index - 1], point)
            point["distance"] = total
        if point.get("elevation") is None:
            point["elevation"] = 0.0

    lats = [point["latitude"] for point in points]
    lons = [point["longitude"] for point in points]

    return {
        "courseName": course_name,
        "description": description,
        "openStreetMap": False,
        "matchedToSegments": False,
        "userProfilePk": None,
        "userGroupPk": None,
        "rulePK": 2,  # private
        "geoRoutePk": None,
        "sourceTypeId": 3,  # GPX
        "sourcePk": None,
        "distanceMeter": total,
        # RouteTimer knows the real elevation profile -- from Google's Elevation service or the
        # rider's own GPX -- so it sends its own totals instead of the zeros that leave Garmin to
        # backfill from its terrain database. Garmin may still override them.
        "elevationGainMeter": elevation_gain_metres,
        "elevationLossMeter": elevation_loss_metres,
        "startPoint": {
            "latitude": points[0]["latitude"],
            "longitude": points[0]["longitude"],
            "elevation": points[0]["elevation"],
            "distance": None,
            "timestamp": None,
        },
        "coursePoints": [],
        "boundingBox": {
            "center": {
                "latitude": (min(lats) + max(lats)) / 2,
                "longitude": (min(lons) + max(lons)) / 2,
            },
            "lowerLeft": {"latitude": min(lats), "longitude": min(lons)},
            "upperRight": {"latitude": max(lats), "longitude": max(lons)},
            "lowerLeftLatIsSet": True,
            "lowerLeftLongIsSet": True,
            "upperRightLatIsSet": True,
            "upperRightLongIsSet": True,
        },
        "hasShareableEvent": False,
        "hasTurnDetectionDisabled": False,
        "activityTypePk": activity_type_id,
        "virtualPartnerId": None,
        "includeLaps": False,
        "elapsedSeconds": None,
        "speedMeterPerSecond": None,
        "courseLines": [
            {
                "courseId": None,
                "sortOrder": 1,
                "numberOfPoints": len(points),
                "distanceInMeters": total,
                "bearing": initial_bearing(points[0], points[-1]),
                "points": points,
                "coordinateSystem": "WGS84",
                "originalCoordinateSystem": "WGS84",
            }
        ],
        "coordinateSystem": "WGS84",
        "targetCoordinateSystem": "WGS84",
        "originalCoordinateSystem": "WGS84",
        "consumer": None,
        "elevationSource": 3,
        "hasPaceBand": False,
        "hasPowerGuide": False,
        "favorite": False,
        "startNote": None,
        "finishNote": None,
        "cutoffDuration": None,
        "geoPoints": points,
    }
