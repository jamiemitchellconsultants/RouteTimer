import { mkdir, readdir, rm, copyFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";

const projectRoot = resolve(import.meta.dirname, "..");
const outputRoot = join(projectRoot, "wwwroot", "vendor");

async function ensureDirectory(path) {
  await mkdir(path, { recursive: true });
}

async function copyExactFile(source, destination) {
  await ensureDirectory(dirname(destination));
  await copyFile(source, destination);
}

async function copyDirectoryContents(sourceDirectory, destinationDirectory) {
  await ensureDirectory(destinationDirectory);

  for (const entry of await readdir(sourceDirectory, { withFileTypes: true })) {
    const sourcePath = join(sourceDirectory, entry.name);
    const destinationPath = join(destinationDirectory, entry.name);

    if (entry.isDirectory()) {
      await copyDirectoryContents(sourcePath, destinationPath);
      continue;
    }

    await copyExactFile(sourcePath, destinationPath);
  }
}

await rm(outputRoot, { recursive: true, force: true });
await ensureDirectory(outputRoot);

await copyExactFile(
  join(projectRoot, "node_modules", "leaflet", "dist", "leaflet.js"),
  join(outputRoot, "leaflet", "leaflet.js")
);
await copyExactFile(
  join(projectRoot, "node_modules", "leaflet", "dist", "leaflet.css"),
  join(outputRoot, "leaflet", "leaflet.css")
);
await copyDirectoryContents(
  join(projectRoot, "node_modules", "leaflet", "dist", "images"),
  join(outputRoot, "leaflet", "images")
);
await copyExactFile(
  join(projectRoot, "node_modules", "chart.js", "dist", "chart.umd.js"),
  join(outputRoot, "chart.js", "chart.umd.js")
);
