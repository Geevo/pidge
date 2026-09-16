/** `1.3 KB`, `42 B`, `2.4 MB` — short enough for the status line. */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return "—";
  if (bytes < 1024) return `${bytes} B`;

  const units = ["KB", "MB", "GB"];
  let value = bytes / 1024;
  let unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex += 1;
  }
  // One decimal reads better at a glance than three digits of false precision,
  // and a trailing `.0` is noise.
  const rounded = value.toFixed(1).replace(/\.0$/, "");
  return `${rounded} ${units[unitIndex]}`;
}

/** `143 ms`, `1.20 s` */
export function formatDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return "—";
  if (ms < 1000) return `${Math.round(ms)} ms`;
  return `${(ms / 1000).toFixed(2)} s`;
}

/** Colour band for a status code, used for the status dot. */
export function statusClass(status: number): "ok" | "redirect" | "client" | "server" | "other" {
  if (status >= 200 && status < 300) return "ok";
  if (status >= 300 && status < 400) return "redirect";
  if (status >= 400 && status < 500) return "client";
  if (status >= 500 && status < 600) return "server";
  return "other";
}

/** `14:02:51` in the viewer's locale. */
export function formatTime(timestamp: number): string {
  return new Date(timestamp).toLocaleTimeString();
}

/**
 * A short label for a tab or a history row. Falls back to the method so a
 * blank request still reads as something.
 */
export function requestLabel(url: string, method: string): string {
  const trimmed = url.trim();
  if (trimmed === "") return "New request";

  try {
    const parsed = new URL(trimmed.includes("://") ? trimmed : `http://${trimmed}`);
    const path = parsed.pathname === "/" ? "" : parsed.pathname;
    return `${parsed.host}${path}` || method;
  } catch {
    return trimmed;
  }
}
