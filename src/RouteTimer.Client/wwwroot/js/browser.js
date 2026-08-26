export function reload() {
    window.location.reload();
}

export function origin() {
    return window.location.origin;
}

export async function copyToClipboard(text) {
    await navigator.clipboard.writeText(text);
}
