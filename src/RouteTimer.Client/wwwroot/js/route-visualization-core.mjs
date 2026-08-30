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

function optionalFiniteNumber(source, name) {
  const value = readValue(source, name);
  if (value === null || value === undefined) {
    return null;
  }

  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw new Error(`Adjustment segment ${name} must be a finite number when present.`);
  }

  return value;
}

function optionalText(source, name) {
  const value = readValue(source, name);
  return value === null || value === undefined ? null : String(value);
}

/**
 * Pairs each baseline segment with the adjustment segment carrying the same sequence. The C# side
 * has already checked alignment; this stays defensive because the module is also callable directly.
 */
export function alignComparisonSegments(rawBaseline, rawAdjustment) {
  const baseline = normalizeSegments(rawBaseline);

  if (!Array.isArray(rawAdjustment) || rawAdjustment.length === 0) {
    throw new Error("Adjustment segments are required.");
  }

  const bySequence = new Map();
  for (const segment of rawAdjustment) {
    const sequence = requireFiniteNumber(segment, "sequence");
    if (bySequence.has(sequence)) {
      throw new Error(`Duplicate adjustment sequence ${sequence}.`);
    }

    bySequence.set(sequence, {
      powerWatts: requireFiniteNumber(segment, "powerWatts"),
      speedMetresPerSecond: requireFiniteNumber(segment, "speedMetresPerSecond"),
      segmentMovingSeconds: requireFiniteNumber(segment, "segmentMovingSeconds"),
      cumulativeMovingSeconds: requireFiniteNumber(segment, "cumulativeMovingSeconds"),
      zoneNumber: optionalFiniteNumber(segment, "zoneNumber"),
      strategyPhase: optionalText(segment, "strategyPhase"),
      wPrimeBalanceJoules: optionalFiniteNumber(segment, "wPrimeBalanceJoules")
    });
  }

  if (bySequence.size !== baseline.length) {
    throw new Error(
      `Adjustment covers ${bySequence.size} sequence(s) but the baseline has ${baseline.length}.`);
  }

  return baseline.map(segment => {
    const adjustment = bySequence.get(segment.sequence);
    if (!adjustment) {
      throw new Error(`Adjustment is missing baseline sequence ${segment.sequence}.`);
    }

    return {
      sequence: segment.sequence,
      cumulativeDistanceMetres: segment.cumulativeDistanceMetres,
      elevationMetres: segment.elevationMetres,
      gradient: segment.gradient,
      baselinePowerWatts: segment.predictedPowerWatts,
      baselineSpeedMetresPerSecond: segment.predictedSpeedMetresPerSecond,
      baselineSegmentMovingSeconds: segment.segmentMovingSeconds,
      adjustmentPowerWatts: adjustment.powerWatts,
      adjustmentSpeedMetresPerSecond: adjustment.speedMetresPerSecond,
      adjustmentSegmentMovingSeconds: adjustment.segmentMovingSeconds,
      zoneNumber: adjustment.zoneNumber,
      strategyPhase: adjustment.strategyPhase,
      wPrimeBalanceJoules: adjustment.wPrimeBalanceJoules
    };
  });
}

/**
 * Thins a comparison series towards `maximumPoints` while keeping every point a reader would notice
 * losing: the ends, and both sides of each zone or phase change. When those mandatory points alone
 * exceed the cap they are all kept - the semantic boundary matters more than the soft display limit.
 */
export function downsampleComparisonPoints(points, maximumPoints = 1500) {
  if (!Array.isArray(points) || points.length <= maximumPoints || maximumPoints < 2) {
    return points;
  }

  const mandatory = new Set([0, points.length - 1]);
  for (let index = 1; index < points.length; index++) {
    const previous = points[index - 1];
    const current = points[index];
    if (previous.zoneNumber !== current.zoneNumber || previous.strategyPhase !== current.strategyPhase) {
      mandatory.add(index - 1);
      mandatory.add(index);
    }
  }

  if (mandatory.size >= maximumPoints) {
    return [...mandatory].sort((left, right) => left - right).map(index => points[index]);
  }

  const selected = new Set(mandatory);
  for (let slot = 0; slot < maximumPoints && selected.size < maximumPoints; slot++) {
    selected.add(Math.round((slot * (points.length - 1)) / (maximumPoints - 1)));
  }

  return [...selected].sort((left, right) => left - right).map(index => points[index]);
}

export function buildComparisonProfileDatasets(rawBaseline, rawAdjustment) {
  const aligned = downsampleComparisonPoints(alignComparisonSegments(rawBaseline, rawAdjustment));

  const distanceKilometres = point => round(point.cumulativeDistanceMetres / 1000, 3);

  const baselinePoint = (point, y) => ({
    sequence: point.sequence,
    x: distanceKilometres(point),
    y: round(y, 2),
    baselineSegmentMovingSeconds: point.baselineSegmentMovingSeconds
  });

  const adjustmentPoint = (point, baselineY, adjustedY) => ({
    sequence: point.sequence,
    x: distanceKilometres(point),
    y: round(adjustedY, 2),
    baselineY: round(baselineY, 2),
    delta: round(adjustedY - baselineY, 2),
    baselineSegmentMovingSeconds: point.baselineSegmentMovingSeconds,
    adjustmentSegmentMovingSeconds: point.adjustmentSegmentMovingSeconds,
    segmentMovingSecondsDelta: round(point.adjustmentSegmentMovingSeconds - point.baselineSegmentMovingSeconds, 2),
    zoneNumber: point.zoneNumber,
    strategyPhase: point.strategyPhase,
    wPrimeBalanceJoules: point.wPrimeBalanceJoules
  });

  return [
    {
      label: "Elevation",
      suffix: " m",
      baselinePoints: aligned.map(point => baselinePoint(point, point.elevationMetres)),
      adjustmentPoints: []
    },
    {
      label: "Gradient",
      suffix: "%",
      baselinePoints: aligned.map(point => baselinePoint(point, point.gradient * 100)),
      adjustmentPoints: []
    },
    {
      label: "Power",
      suffix: " W",
      baselinePoints: aligned.map(point => baselinePoint(point, point.baselinePowerWatts)),
      adjustmentPoints: aligned.map(point =>
        adjustmentPoint(point, point.baselinePowerWatts, point.adjustmentPowerWatts))
    },
    {
      label: "Speed",
      suffix: " km/h",
      baselinePoints: aligned.map(point => baselinePoint(point, point.baselineSpeedMetresPerSecond * 3.6)),
      adjustmentPoints: aligned.map(point =>
        adjustmentPoint(point, point.baselineSpeedMetresPerSecond * 3.6, point.adjustmentSpeedMetresPerSecond * 3.6))
    }
  ];
}
