import test from "node:test";
import assert from "node:assert/strict";

import {
  buildProfileDatasets,
  nearestSegmentSequence,
  normalizeSegments
} from "./route-visualization-core.mjs";

const rawSegments = [
  {
    sequence: 2,
    latitude: 51.5105,
    longitude: -0.1224,
    elevationMetres: 132,
    cumulativeDistanceMetres: 1000,
    segmentDistanceMetres: 500,
    gradient: 0.03,
    curvaturePerMetre: 0.001,
    predictedPowerWatts: 250,
    predictedSpeedMetresPerSecond: 8.9,
    segmentMovingSeconds: 62,
    cumulativeMovingSeconds: 122,
    confidence: "High"
  },
  {
    sequence: 1,
    latitude: 51.5007,
    longitude: -0.1246,
    elevationMetres: 126,
    cumulativeDistanceMetres: 500,
    segmentDistanceMetres: 500,
    gradient: 0.02,
    curvaturePerMetre: 0.001,
    predictedPowerWatts: 246,
    predictedSpeedMetresPerSecond: 8.2,
    segmentMovingSeconds: 60,
    cumulativeMovingSeconds: 60,
    confidence: "Medium"
  }
];

test("normalizeSegments sorts by sequence and rejects empty input", () => {
  const normalized = normalizeSegments(rawSegments);

  assert.deepEqual(
    normalized.map(segment => segment.sequence),
    [1, 2]
  );
  assert.throws(() => normalizeSegments([]), /segments/i);
});

test("normalizeSegments rejects non-finite numeric fields", () => {
  const invalid = [{ ...rawSegments[0], predictedSpeedMetresPerSecond: Number.NaN }];

  assert.throws(() => normalizeSegments(invalid), /predictedSpeedMetresPerSecond/i);
});

test("buildProfileDatasets returns converted display values for all four profiles", () => {
  const datasets = buildProfileDatasets(normalizeSegments(rawSegments));

  assert.deepEqual(
    datasets.map(dataset => dataset.label),
    ["Elevation", "Gradient", "Power", "Speed"]
  );
  assert.deepEqual(datasets[0].points, [
    { sequence: 1, x: 0.5, y: 126 },
    { sequence: 2, x: 1, y: 132 }
  ]);
  assert.deepEqual(datasets[1].points, [
    { sequence: 1, x: 0.5, y: 2 },
    { sequence: 2, x: 1, y: 3 }
  ]);
  assert.deepEqual(datasets[2].points, [
    { sequence: 1, x: 0.5, y: 246 },
    { sequence: 2, x: 1, y: 250 }
  ]);
  assert.deepEqual(datasets[3].points, [
    { sequence: 1, x: 0.5, y: 29.52 },
    { sequence: 2, x: 1, y: 32.04 }
  ]);
});

test("nearestSegmentSequence returns the closest sequence", () => {
  const normalized = normalizeSegments(rawSegments);

  const sequence = nearestSegmentSequence(normalized, 51.5102, -0.1226);

  assert.equal(sequence, 2);
});

test("nearestSegmentSequence resolves equal distances to the lower sequence", () => {
  const tied = normalizeSegments([
    { ...rawSegments[0], sequence: 3, latitude: 51.501, longitude: -0.124, cumulativeDistanceMetres: 1500 },
    { ...rawSegments[1], sequence: 2, latitude: 51.499, longitude: -0.124, cumulativeDistanceMetres: 500 }
  ]);

  const sequence = nearestSegmentSequence(tied, 51.5, -0.124);

  assert.equal(sequence, 2);
});
