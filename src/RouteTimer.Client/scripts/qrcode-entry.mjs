// The bundle's entire public surface. qrcode's package entry also pulls in canvas and file-system
// renderers that a browser cannot use and esbuild would otherwise have to shim; exporting only
// toString keeps the vendored bundle to the SVG path this application actually calls.
import QRCode from "qrcode";

export const toString = QRCode.toString;
