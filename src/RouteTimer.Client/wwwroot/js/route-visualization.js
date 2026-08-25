import {
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
  return points.find(point => point.sequence === sequence) ?? points[0];
}

function createProfileChart(Chart, canvasId, title, suffix, points, dotNetReference) {
  const context = document.getElementById(canvasId)?.getContext("2d");
  if (!context) {
    throw new Error(`Canvas '${canvasId}' was not found.`);
  }

  return new Chart(context, {
    type: "line",
    data: {
      datasets: [
        {
          label: title,
          data: points.map(point => ({ x: point.x, y: point.y, sequence: point.sequence })),
          borderColor: "#2f5d62",
          backgroundColor: "rgba(47, 93, 98, 0.15)",
          borderWidth: 2,
          pointRadius: 0,
          pointHoverRadius: 5,
          tension: 0.2
        },
        {
          label: `${title} cursor`,
          data: [],
          borderColor: "#d1495b",
          backgroundColor: "#d1495b",
          pointRadius: 5,
          pointHoverRadius: 5,
          showLine: false
        }
      ]
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      animation: false,
      interaction: {
        mode: "nearest",
        intersect: false
      },
      parsing: false,
      plugins: {
        legend: {
          display: false
        },
        tooltip: {
          callbacks: {
            label(context) {
              return `${title}: ${context.parsed.y}${suffix}`;
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
        const active = activeElements.find(element => element.datasetIndex === 0);
        if (!active) {
          return;
        }

        const dataPoint = points[active.index];
        if (!dataPoint) {
          return;
        }

        void dotNetReference.invokeMethodAsync("OnSequenceSelected", dataPoint.sequence);
      }
    }
  });
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

  const charts = [
    createProfileChart(Chart, containerIds.elevation, "Elevation", " m", datasets[0].points, dotNetReference),
    createProfileChart(Chart, containerIds.gradient, "Gradient", "%", datasets[1].points, dotNetReference),
    createProfileChart(Chart, containerIds.power, "Power", " W", datasets[2].points, dotNetReference),
    createProfileChart(Chart, containerIds.speed, "Speed", " km/h", datasets[3].points, dotNetReference)
  ];

  profileRegistry.set(componentId, {
    charts,
    datasets
  });

  selectProfileSequence(componentId, segments[0].sequence);
}

export function selectProfileSequence(componentId, sequence) {
  const entry = profileRegistry.get(componentId);
  if (!entry) {
    return;
  }

  entry.charts.forEach((chart, index) => {
    const point = selectedPoint(entry.datasets[index].points, sequence);
    chart.data.datasets[1].data = [{ x: point.x, y: point.y, sequence: point.sequence }];
    chart.update("none");
  });
}

export function disposeProfiles(componentId) {
  const entry = profileRegistry.get(componentId);
  if (!entry) {
    return;
  }

  entry.charts.forEach(chart => chart.destroy());
  profileRegistry.delete(componentId);
}
