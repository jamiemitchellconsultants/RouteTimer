import test from "node:test";
import assert from "node:assert/strict";

import {
  alignComparisonSegments,
  buildComparisonProfileDatasets,
  buildProfileDatasets,
  downsampleComparisonPoints,
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

const rawAdjustment = [
  {
    sequence: 2,
    powerWatts: 275,
    speedMetresPerSecond: 9.2,
    segmentMovingSeconds: 58,
    cumulativeMovingSeconds: 116,
    zoneNumber: 4,
    strategyPhase: null,
    wPrimeBalanceJoules: null
  },
  {
    sequence: 1,
    powerWatts: 260,
    speedMetresPerSecond: 8.5,
    segmentMovingSeconds: 58,
    cumulativeMovingSeconds: 58,
    zoneNumber: 3,
    strategyPhase: null,
    wPrimeBalanceJoules: null
  }
];

function comparisonSegments(count, mutate = () => ({})) {
  const baseline = [];
  const adjustment = [];
  for (let index = 0; index < count; index++) {
    const sequence = index + 1;
    baseline.push({
      sequence,
      latitude: 51.5 + (index * 0.0001),
      longitude: -0.12 + (index * 0.0001),
      elevationMetres: 100 + index,
      cumulativeDistanceMetres: (index + 1) * 100,
      segmentDistanceMetres: 100,
      gradient: 0.01,
      curvaturePerMetre: 0,
      predictedPowerWatts: 200,
      predictedSpeedMetresPerSecond: 8,
      segmentMovingSeconds: 12.5,
      cumulativeMovingSeconds: (index + 1) * 12.5,
      confidence: "High"
    });
    adjustment.push({
      sequence,
      powerWatts: 220,
      speedMetresPerSecond: 8.4,
      segmentMovingSeconds: 11.9,
      cumulativeMovingSeconds: (index + 1) * 11.9,
      zoneNumber: 3,
      strategyPhase: null,
      wPrimeBalanceJoules: null,
      ...mutate(index)
    });
  }

  return { baseline, adjustment };
}

test("buildComparisonProfileDatasets aligns adjustment by sequence", () => {
  const datasets = buildComparisonProfileDatasets(rawSegments, rawAdjustment);

  assert.deepEqual(datasets[2].adjustmentPoints.map(point => point.sequence), [1, 2]);
});

test("alignComparisonSegments rejects missing, duplicate, and extra sequences", () => {
  assert.throws(() => alignComparisonSegments(rawSegments, [rawAdjustment[0]]), /sequence/i);
  assert.throws(
    () => alignComparisonSegments(rawSegments, [rawAdjustment[0], { ...rawAdjustment[0] }]),
    /duplicate/i
  );
  assert.throws(
    () => alignComparisonSegments(rawSegments, [...rawAdjustment, { ...rawAdjustment[0], sequence: 3 }]),
    /sequence/i
  );
});

test("alignComparisonSegments rejects non-finite adjusted metrics", () => {
  const invalid = [rawAdjustment[0], { ...rawAdjustment[1], powerWatts: Number.NaN }];

  assert.throws(() => alignComparisonSegments(rawSegments, invalid), /powerWatts/i);
});

test("alignComparisonSegments rejects a non-finite W-prime balance when present", () => {
  const invalid = [rawAdjustment[0], { ...rawAdjustment[1], wPrimeBalanceJoules: Number.POSITIVE_INFINITY }];

  assert.throws(() => alignComparisonSegments(rawSegments, invalid), /wPrimeBalanceJoules/i);
  assert.doesNotThrow(() => alignComparisonSegments(rawSegments, rawAdjustment));
});

test("buildComparisonProfileDatasets returns four groups with baseline before adjustment", () => {
  const datasets = buildComparisonProfileDatasets(rawSegments, rawAdjustment);

  assert.deepEqual(datasets.map(dataset => dataset.label), ["Elevation", "Gradient", "Power", "Speed"]);
  assert.deepEqual(datasets.map(dataset => dataset.suffix), [" m", "%", " W", " km/h"]);
  assert.deepEqual(datasets[0].adjustmentPoints, []);
  assert.deepEqual(datasets[1].adjustmentPoints, []);
  assert.equal(datasets[2].baselinePoints.length, 2);
  assert.equal(datasets[2].adjustmentPoints.length, 2);
});

test("buildComparisonProfileDatasets converts distance to km and speed to km/h", () => {
  const datasets = buildComparisonProfileDatasets(rawSegments, rawAdjustment);

  assert.deepEqual(datasets[2].baselinePoints[0], {
    sequence: 1,
    x: 0.5,
    y: 246,
    baselineSegmentMovingSeconds: 60
  });
  assert.deepEqual(datasets[2].adjustmentPoints[0], {
    sequence: 1,
    x: 0.5,
    y: 260,
    baselineY: 246,
    delta: 14,
    baselineSegmentMovingSeconds: 60,
    adjustmentSegmentMovingSeconds: 58,
    segmentMovingSecondsDelta: -2,
    zoneNumber: 3,
    strategyPhase: null,
    wPrimeBalanceJoules: null
  });
  assert.deepEqual(datasets[3].adjustmentPoints[1], {
    sequence: 2,
    x: 1,
    y: 33.12,
    baselineY: 32.04,
    delta: 1.08,
    baselineSegmentMovingSeconds: 62,
    adjustmentSegmentMovingSeconds: 58,
    segmentMovingSecondsDelta: -4,
    zoneNumber: 4,
    strategyPhase: null,
    wPrimeBalanceJoules: null
  });
});

test("downsampleComparisonPoints preserves the first and last points", () => {
  const points = Array.from({ length: 4000 }, (_, index) => ({ sequence: index + 1 }));

  const sampled = downsampleComparisonPoints(points);

  assert.equal(sampled[0].sequence, 1);
  assert.equal(sampled[sampled.length - 1].sequence, 4000);
  assert.ok(sampled.length <= 1500);
});

test("downsampleComparisonPoints returns short inputs untouched", () => {
  const points = Array.from({ length: 10 }, (_, index) => ({ sequence: index + 1 }));

  assert.deepEqual(downsampleComparisonPoints(points), points);
});

test("downsampleComparisonPoints keeps both sides of every zone and phase change", () => {
  const points = Array.from({ length: 3000 }, (_, index) => ({
    sequence: index + 1,
    zoneNumber: index < 1500 ? 2 : 5,
    strategyPhase: index < 2000 ? "baseline" : "burn"
  }));

  const sampled = downsampleComparisonPoints(points);
  const sequences = sampled.map(point => point.sequence);

  assert.ok(sequences.includes(1500), "last sequence before the zone change");
  assert.ok(sequences.includes(1501), "first sequence after the zone change");
  assert.ok(sequences.includes(2000), "last sequence before the phase change");
  assert.ok(sequences.includes(2001), "first sequence after the phase change");
  assert.deepEqual(sequences, [...sequences].sort((left, right) => left - right));
  assert.equal(new Set(sequences).size, sequences.length);
});

test("downsampleComparisonPoints keeps every mandatory point even past the soft cap", () => {
  const points = Array.from({ length: 4000 }, (_, index) => ({
    sequence: index + 1,
    zoneNumber: index % 2 === 0 ? 2 : 5,
    strategyPhase: null
  }));

  const sampled = downsampleComparisonPoints(points);

  assert.equal(sampled.length, 4000);
});

test("buildComparisonProfileDatasets downsamples every group to the same sequences", () => {
  const { baseline, adjustment } = comparisonSegments(4000);

  const datasets = buildComparisonProfileDatasets(baseline, adjustment);
  const powerSequences = datasets[2].baselinePoints.map(point => point.sequence);

  assert.ok(powerSequences.length <= 1500);
  assert.deepEqual(datasets[0].baselinePoints.map(point => point.sequence), powerSequences);
  assert.deepEqual(datasets[2].adjustmentPoints.map(point => point.sequence), powerSequences);
  assert.deepEqual(datasets[3].adjustmentPoints.map(point => point.sequence), powerSequences);
});

class FakeChart {
  constructor(context, config) {
    this.config = config;
    this.data = config.data;
    this.options = config.options;
    this.updateCount = 0;
    this.destroyed = false;
  }

  update() {
    this.updateCount++;
  }

  destroy() {
    this.destroyed = true;
  }
}

const canvasIds = { elevation: "elevation", gradient: "gradient", power: "power", speed: "speed" };

function installChartStubs() {
  globalThis.Chart = FakeChart;
  globalThis.document = { getElementById: () => ({ getContext: () => ({}) }) };
  const selected = [];
  return {
    selected,
    dotNetReference: {
      invokeMethodAsync(_method, sequence) {
        selected.push(sequence);
        return Promise.resolve();
      }
    }
  };
}

// Sequences deliberately do not start at 1: a chart index is not a segment sequence.
const offsetBaseline = rawSegments.map(segment => ({ ...segment, sequence: segment.sequence + 4 }));
const offsetAdjustment = rawAdjustment.map(segment => ({ ...segment, sequence: segment.sequence + 4 }));

test("initializeProfiles keeps one line plus the cursor and no legend", async () => {
  const { dotNetReference } = installChartStubs();
  const charts = await import("./route-visualization.js");

  charts.initializeProfiles("baseline-only", canvasIds, rawSegments, dotNetReference);
  const power = charts.__profileChartsForTest("baseline-only")[2];

  assert.equal(power.chart.data.datasets.length, 2);
  assert.equal(power.chart.data.datasets[0].label, "Power");
  assert.equal(power.cursorDatasetIndex, 1);
  assert.equal(power.chart.options.plugins.legend.display, false);
  assert.equal(power.chart.options.interaction.mode, "nearest");
  charts.disposeProfiles("baseline-only");
});

test("initializeComparisonProfiles draws the adjustment line only on power and speed", async () => {
  const { dotNetReference } = installChartStubs();
  const charts = await import("./route-visualization.js");

  charts.initializeComparisonProfiles("comparison", canvasIds, offsetBaseline, offsetAdjustment, dotNetReference);
  const [elevation, , power, speed] = charts.__profileChartsForTest("comparison");

  assert.equal(elevation.chart.data.datasets.length, 2);
  assert.equal(elevation.chart.options.plugins.legend.display, false);

  for (const entry of [power, speed]) {
    assert.equal(entry.chart.data.datasets.length, 3);
    assert.equal(entry.cursorDatasetIndex, 2);
    assert.equal(entry.chart.options.plugins.legend.display, true);
    assert.equal(entry.chart.data.datasets[1].borderColor, "#d1495b");
    assert.equal(entry.chart.data.datasets[1].borderWidth, 2);
    assert.equal(entry.chart.data.datasets[1].fill, false);
  }

  assert.equal(power.chart.data.datasets[0].label, "Power (baseline)");
  assert.equal(power.chart.data.datasets[1].label, "Power (adjustment)");
  charts.disposeProfiles("comparison");
});

test("comparison tooltips report baseline, adjustment, deltas, and annotations", async () => {
  const { dotNetReference } = installChartStubs();
  const charts = await import("./route-visualization.js");
  const annotated = offsetAdjustment.map(segment =>
    segment.sequence === 5
      ? { ...segment, zoneNumber: 3, strategyPhase: "burn", wPrimeBalanceJoules: 12500 }
      : segment);

  charts.initializeComparisonProfiles("tooltips", canvasIds, offsetBaseline, annotated, dotNetReference);
  const power = charts.__profileChartsForTest("tooltips")[2].chart;
  const label = power.options.plugins.tooltip.callbacks.label;

  assert.equal(
    label({ datasetIndex: 0, dataIndex: 0, dataset: power.data.datasets[0], parsed: { y: 246 } }),
    "Baseline power: 246 W");
  assert.deepEqual(
    label({ datasetIndex: 1, dataIndex: 0, dataset: power.data.datasets[1], parsed: { y: 260 } }),
    [
      "Adjustment power: 260 W",
      "Delta: +14 W",
      "Segment time: -2 s",
      "Zone: 3",
      "Phase: burn",
      "W' balance: 12500 J"
    ]);

  // The cursor dataset never appears in the tooltip or the legend.
  assert.equal(power.options.plugins.tooltip.filter({ datasetIndex: 2 }), false);
  assert.equal(power.options.plugins.legend.labels.filter({ datasetIndex: 2 }), false);
  charts.disposeProfiles("tooltips");
});

test("hovering a comparison chart reports the point's sequence, not its index", async () => {
  const { selected, dotNetReference } = installChartStubs();
  const charts = await import("./route-visualization.js");

  charts.initializeComparisonProfiles("hover", canvasIds, offsetBaseline, offsetAdjustment, dotNetReference);
  const power = charts.__profileChartsForTest("hover")[2].chart;

  power.options.onHover(null, [{ datasetIndex: 2, index: 0 }, { datasetIndex: 1, index: 0 }]);

  assert.deepEqual(selected, [5]);
  charts.disposeProfiles("hover");
});

test("selectProfileSequence moves the cursor dataset in both chart modes", async () => {
  const { dotNetReference } = installChartStubs();
  const charts = await import("./route-visualization.js");

  charts.initializeProfiles("cursor-baseline", canvasIds, rawSegments, dotNetReference);
  charts.selectProfileSequence("cursor-baseline", 2);
  const baselinePower = charts.__profileChartsForTest("cursor-baseline")[2];
  assert.deepEqual(baselinePower.chart.data.datasets[1].data, [{ x: 1, y: 250, sequence: 2 }]);

  charts.initializeComparisonProfiles("cursor-comparison", canvasIds, offsetBaseline, offsetAdjustment, dotNetReference);
  charts.selectProfileSequence("cursor-comparison", 6);
  const comparisonPower = charts.__profileChartsForTest("cursor-comparison")[2];
  assert.deepEqual(comparisonPower.chart.data.datasets[2].data, [{ x: 1, y: 250, sequence: 6 }]);

  charts.disposeProfiles("cursor-baseline");
  charts.disposeProfiles("cursor-comparison");
});

test("selectProfileSequence falls back to the nearest surviving point", async () => {
  const { dotNetReference } = installChartStubs();
  const charts = await import("./route-visualization.js");

  charts.initializeComparisonProfiles("missing", canvasIds, offsetBaseline, offsetAdjustment, dotNetReference);
  charts.selectProfileSequence("missing", 99);
  const power = charts.__profileChartsForTest("missing")[2];

  assert.equal(power.chart.data.datasets[2].data[0].sequence, 6);
  charts.disposeProfiles("missing");
});

test("disposeProfiles destroys every chart it created", async () => {
  const { dotNetReference } = installChartStubs();
  const charts = await import("./route-visualization.js");

  charts.initializeProfiles("disposable", canvasIds, rawSegments, dotNetReference);
  const created = charts.__profileChartsForTest("disposable").map(entry => entry.chart);

  charts.disposeProfiles("disposable");

  assert.ok(created.every(chart => chart.destroyed));
  assert.equal(charts.__profileChartsForTest("disposable"), undefined);
});

test("downsampleComparisonPoints spreads its filler across the whole route", () => {
  // Many mandatory boundaries early on: a filler that walks the whole range and stops at the cap
  // would leave the back of the route represented only by its boundary points.
  const points = Array.from({ length: 4000 }, (_, index) => ({
    sequence: index + 1,
    zoneNumber: index < 1200 && index % 2 === 0 ? 2 : 5,
    strategyPhase: null
  }));

  const sampled = downsampleComparisonPoints(points);
  const secondHalf = sampled.filter(point => point.sequence > 2000).length;

  assert.ok(sampled.length <= 1500);
  assert.ok(
    secondHalf > 100,
    `only ${secondHalf} sampled points fell in the second half of the route`);
});
