import {
  buildComparisonProfileDatasets,
  buildProfileDatasets,
  nearestSegmentSequence,
  normalizeSegments
} from "./route-visualization-core.mjs";

const mapRegistry = new Map();
const profileRegistry = new Map();

function requireLeaflet() {
  if (!globalThis.L) {
    throw new Error("Leaflet is not available.");
  }

  return globalThis.L;
}

function requireChartJs() {
  if (!globalThis.Chart) {
    throw new Error("Chart.js is not available.");
  }

  return globalThis.Chart;
}

function getTileOption(source, name) {
  return source?.[name] ?? source?.[name[0].toUpperCase() + name.slice(1)];
}

function selectedPoint(points, sequence) {
  const exact = points.find(point => point.sequence === sequence);
  if (exact) {
    return exact;
  }

  // A downsampled comparison series may not carry the selected sequence, so fall back to the
  // nearest point that survived rather than snapping the cursor back to the start of the route.
  return points.reduce(
    (best, point) =>
      Math.abs(point.sequence - sequence) < Math.abs(best.sequence - sequence) ? point : best,
    points[0]);
}

const baselineLine = {
  borderColor: "#2f5d62",
  backgroundColor: "rgba(47, 93, 98, 0.15)",
  borderWidth: 2,
  pointRadius: 0,
  pointHoverRadius: 5,
  tension: 0.2
};

const adjustmentLine = {
  borderColor: "#d1495b",
  backgroundColor: "transparent",
  borderWidth: 2,
  fill: false,
  pointRadius: 0,
  pointHoverRadius: 5,
  tension: 0.2
};

function signed(value, suffix) {
  return `${value < 0 ? "" : "+"}${value}${suffix}`;
}

function annotationRows(point) {
  const rows = [];
  if (point.zoneNumber !== null && point.zoneNumber !== undefined) {
    rows.push(`Zone: ${point.zoneNumber}`);
  }

  if (point.strategyPhase) {
    rows.push(`Phase: ${point.strategyPhase}`);
  }

  if (point.wPrimeBalanceJoules !== null && point.wPrimeBalanceJoules !== undefined) {
    rows.push(`W' balance: ${Math.round(point.wPrimeBalanceJoules)} J`);
  }

  return rows;
}

function createProfileChart(Chart, canvasId, config, dotNetReference) {
  const context = document.getElementById(canvasId)?.getContext("2d");
  if (!context) {
    throw new Error(`Canvas '${canvasId}' was not found.`);
  }

  const { title, suffix, points, comparisonPoints } = config;
  const comparison = comparisonPoints.length > 0;

  const toData = source => source.map(point => ({ x: point.x, y: point.y, sequence: point.sequence }));

  const datasets = [
    { ...baselineLine, label: comparison ? `${title} (baseline)` : title, data: toData(points), $points: points }
  ];

  if (comparison) {
    datasets.push({
      ...adjustmentLine,
      label: `${title} (adjustment)`,
      data: toData(comparisonPoints),
      $points: comparisonPoints
    });
  }

  const cursorDatasetIndex = datasets.length;
  datasets.push({
    label: `${title} cursor`,
    data: [],
    borderColor: "#d1495b",
    backgroundColor: "#d1495b",
    pointRadius: 5,
    pointHoverRadius: 5,
    showLine: false
  });

  const chart = new Chart(context, {
    type: "line",
    data: { datasets },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      animation: false,
      interaction: {
        // Comparison charts report both lines for one x so a single hover reads as one block.
        mode: comparison ? "index" : "nearest",
        intersect: false
      },
      parsing: false,
      plugins: {
        legend: {
          display: comparison,
          labels: {
            filter: item => item.datasetIndex !== cursorDatasetIndex
          }
        },
        tooltip: {
          filter: item => item.datasetIndex !== cursorDatasetIndex,
          callbacks: {
            label(item) {
              const point = item.dataset.$points?.[item.dataIndex];
              if (!comparison || !point) {
                return `${title}: ${item.parsed.y}${suffix}`;
              }

              if (item.datasetIndex === 0) {
                return `Baseline ${title.toLowerCase()}: ${point.y}${suffix}`;
              }

              return [
                `Adjustment ${title.toLowerCase()}: ${point.y}${suffix}`,
                `Delta: ${signed(point.delta, suffix)}`,
                `Segment time: ${signed(point.segmentMovingSecondsDelta, " s")}`,
                ...annotationRows(point)
              ];
            }
          }
        }
      },
      scales: {
        x: {
          type: "linear",
          title: {
            display: true,
            text: "Distance (km)"
          }
        },
        y: {
          title: {
            display: true,
            text: title
          }
        }
      },
      onHover(_event, activeElements) {
        const active = activeElements.find(element => element.datasetIndex !== cursorDatasetIndex);
        if (!active) {
          return;
        }

        const dataPoint = chart.data.datasets[active.datasetIndex].$points?.[active.index];
        if (!dataPoint) {
          return;
        }

        void dotNetReference.invokeMethodAsync("OnSequenceSelected", dataPoint.sequence);
      }
    }
  });

  return { chart, points, cursorDatasetIndex };
}

export function initializeMap(componentId, containerId, rawSegments, tileOptions, dotNetReference) {
  disposeMap(componentId);

  const L = requireLeaflet();
  const segments = normalizeSegments(rawSegments);
  const map = L.map(containerId, {
    preferCanvas: true
  });

  L.tileLayer(getTileOption(tileOptions, "url"), {
    attribution: getTileOption(tileOptions, "attribution")
  }).addTo(map);

  const polyline = L.polyline(segments.map(segment => [segment.latitude, segment.longitude]), {
    color: "#2f5d62",
    weight: 4
  }).addTo(map);

  map.fitBounds(polyline.getBounds(), { padding: [16, 16] });

  const marker = L.circleMarker([segments[0].latitude, segments[0].longitude], {
    radius: 7,
    color: "#d1495b",
    fillColor: "#d1495b",
    fillOpacity: 0.9
  }).addTo(map);

  const handleClick = event => {
    const sequence = nearestSegmentSequence(segments, event.latlng.lat, event.latlng.lng);
    void dotNetReference.invokeMethodAsync("OnSequenceSelected", sequence);
  };

  map.on("click", handleClick);

  mapRegistry.set(componentId, {
    map,
    marker,
    segments,
    handleClick
  });

  selectMapSequence(componentId, segments[0].sequence);
}

export function selectMapSequence(componentId, sequence) {
  const entry = mapRegistry.get(componentId);
  if (!entry) {
    return;
  }

  const segment = entry.segments.find(candidate => candidate.sequence === sequence);
  if (!segment) {
    return;
  }

  entry.marker.setLatLng([segment.latitude, segment.longitude]);
}

export function disposeMap(componentId) {
  const entry = mapRegistry.get(componentId);
  if (!entry) {
    return;
  }

  entry.map.off("click", entry.handleClick);
  entry.map.remove();
  mapRegistry.delete(componentId);
}

export function initializeProfiles(componentId, containerIds, rawSegments, dotNetReference) {
  disposeProfiles(componentId);

  const Chart = requireChartJs();
  const segments = normalizeSegments(rawSegments);
  const datasets = buildProfileDatasets(segments);
  const configs = [
    { key: "elevation", title: "Elevation", suffix: " m", points: datasets[0].points, comparisonPoints: [] },
    { key: "gradient", title: "Gradient", suffix: "%", points: datasets[1].points, comparisonPoints: [] },
    { key: "power", title: "Power", suffix: " W", points: datasets[2].points, comparisonPoints: [] },
    { key: "speed", title: "Speed", suffix: " km/h", points: datasets[3].points, comparisonPoints: [] }
  ];

  registerProfiles(componentId, Chart, containerIds, configs, dotNetReference);
}

export function initializeComparisonProfiles(
  componentId,
  containerIds,
  rawBaselineSegments,
  rawAdjustmentSegments,
  dotNetReference
) {
  disposeProfiles(componentId);

  const Chart = requireChartJs();
  const datasets = buildComparisonProfileDatasets(rawBaselineSegments, rawAdjustmentSegments);
  const keys = ["elevation", "gradient", "power", "speed"];
  const configs = datasets.map((dataset, index) => ({
    key: keys[index],
    title: dataset.label,
    suffix: dataset.suffix,
    points: dataset.baselinePoints,
    comparisonPoints: dataset.adjustmentPoints
  }));

  registerProfiles(componentId, Chart, containerIds, configs, dotNetReference);
}

function registerProfiles(componentId, Chart, containerIds, configs, dotNetReference) {
  const charts = configs.map(config =>
    createProfileChart(Chart, containerIds[config.key], config, dotNetReference));

  profileRegistry.set(componentId, { charts });
  selectProfileSequence(componentId, charts[0].points[0].sequence);
}

export function selectProfileSequence(componentId, sequence) {
  const entry = profileRegistry.get(componentId);
  if (!entry) {
    return;
  }

  entry.charts.forEach(({ chart, points, cursorDatasetIndex }) => {
    const point = selectedPoint(points, sequence);
    chart.data.datasets[cursorDatasetIndex].data = [{ x: point.x, y: point.y, sequence: point.sequence }];
    chart.update("none");
  });
}

/** Test seam: the chart entries registered for a component, or undefined once disposed. */
export function __profileChartsForTest(componentId) {
  return profileRegistry.get(componentId)?.charts;
}

export function disposeProfiles(componentId) {
  const entry = profileRegistry.get(componentId);
  if (!entry) {
    return;
  }

  entry.charts.forEach(({ chart }) => chart.destroy());
  profileRegistry.delete(componentId);
}
