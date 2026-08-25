const numericFields = [
  "sequence",
  "latitude",
  "longitude",
  "elevationMetres",
  "cumulativeDistanceMetres",
  "segmentDistanceMetres",
  "gradient",
  "curvaturePerMetre",
  "predictedPowerWatts",
  "predictedSpeedMetresPerSecond",
  "segmentMovingSeconds",
  "cumulativeMovingSeconds"
];

function toPascalCase(name) {
  return `${name[0].toUpperCase()}${name.slice(1)}`;
}

function readValue(source, name) {
  return source?.[name] ?? source?.[toPascalCase(name)];
}

function requireFiniteNumber(source, name) {
  const value = readValue(source, name);
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw new Error(`Segment ${name} must be a finite number.`);
  }

  return value;
}

function round(value, digits = 2) {
  const factor = 10 ** digits;
  return Math.round(value * factor) / factor;
}

export function normalizeSegments(rawSegments) {
  if (!Array.isArray(rawSegments) || rawSegments.length === 0) {
    throw new Error("Prediction segments are required.");
  }

  return rawSegments
    .map(segment => ({
      sequence: requireFiniteNumber(segment, "sequence"),
      latitude: requireFiniteNumber(segment, "latitude"),
      longitude: requireFiniteNumber(segment, "longitude"),
      elevationMetres: requireFiniteNumber(segment, "elevationMetres"),
      cumulativeDistanceMetres: requireFiniteNumber(segment, "cumulativeDistanceMetres"),
      segmentDistanceMetres: requireFiniteNumber(segment, "segmentDistanceMetres"),
      gradient: requireFiniteNumber(segment, "gradient"),
      curvaturePerMetre: requireFiniteNumber(segment, "curvaturePerMetre"),
      predictedPowerWatts: requireFiniteNumber(segment, "predictedPowerWatts"),
      predictedSpeedMetresPerSecond: requireFiniteNumber(segment, "predictedSpeedMetresPerSecond"),
      segmentMovingSeconds: requireFiniteNumber(segment, "segmentMovingSeconds"),
      cumulativeMovingSeconds: requireFiniteNumber(segment, "cumulativeMovingSeconds"),
      confidence: String(readValue(segment, "confidence") ?? "")
    }))
    .sort((left, right) => left.sequence - right.sequence);
}

export function nearestSegmentSequence(segments, latitude, longitude) {
  const normalized = normalizeSegments(segments);
  const targetLatitude = Number(latitude);
  const targetLongitude = Number(longitude);

  if (!Number.isFinite(targetLatitude) || !Number.isFinite(targetLongitude)) {
    throw new Error("Target latitude and longitude must be finite numbers.");
  }

  let bestSegment = normalized[0];
  let bestDistance = Number.POSITIVE_INFINITY;

  for (const segment of normalized) {
    const latitudeDelta = segment.latitude - targetLatitude;
    const longitudeDelta = segment.longitude - targetLongitude;
    const squaredDistance = (latitudeDelta * latitudeDelta) + (longitudeDelta * longitudeDelta);

    if (
      squaredDistance < bestDistance ||
      (squaredDistance === bestDistance && segment.sequence < bestSegment.sequence)
    ) {
      bestSegment = segment;
      bestDistance = squaredDistance;
    }
  }

  return bestSegment.sequence;
}

export function buildProfileDatasets(segments) {
  const normalized = normalizeSegments(segments);
  const point = (segment, y) => ({
    sequence: segment.sequence,
    x: round(segment.cumulativeDistanceMetres / 1000, 3),
    y
  });

  return [
    {
      label: "Elevation",
      points: normalized.map(segment => point(segment, round(segment.elevationMetres, 2)))
    },
    {
      label: "Gradient",
      points: normalized.map(segment => point(segment, round(segment.gradient * 100, 2)))
    },
    {
      label: "Power",
      points: normalized.map(segment => point(segment, round(segment.predictedPowerWatts, 2)))
    },
    {
      label: "Speed",
      points: normalized.map(segment => point(segment, round(segment.predictedSpeedMetresPerSecond * 3.6, 2)))
    }
  ];
}
