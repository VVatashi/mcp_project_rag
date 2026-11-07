/** Reads a number of an address, or gives null for an empty or wrong value. */
export function numberOrNull(value: string | null | undefined): number | null {
  const parsed = value ? Number(value) : NaN;
  return Number.isFinite(parsed) ? parsed : null;
}

/** Decodes a part of an address. A broken escape such as "%E0" stays as it is instead of failing the page. */
export function decodePart(value: string): string {
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
}

/** The address that resolves an anchor of a document to its chunk. A name such as "table-8.14" stays in the query, so the server reads no file extension. */
export function anchorAddress(documentId: number, name: string): string {
  return `/documents/${documentId}/anchors?name=${encodeURIComponent(name)}`;
}

const dateTime = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });
const number = new Intl.NumberFormat();

export function formatDate(value: string | null | undefined): string {
  return value ? dateTime.format(new Date(value)) : '—';
}

export function formatNumber(value: number): string {
  return number.format(value);
}

export function formatCost(value: number): string {
  return `$${value.toFixed(value < 0.01 ? 5 : 3)}`;
}

export function formatBytes(value: number): string {
  if (value < 1024) return `${value} B`;
  const units = ['KB', 'MB', 'GB'];
  let size = value / 1024;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit++;
  }
  return `${size.toFixed(size < 10 ? 1 : 0)} ${units[unit]}`;
}

export function formatDuration(start: string, end: string | null): string {
  const seconds = Math.max(0, Math.round(((end ? new Date(end) : new Date()).getTime() - new Date(start).getTime()) / 1000));
  if (seconds < 60) return `${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  return minutes < 60 ? `${minutes} min ${seconds % 60} s` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

export function percent(part: number, total: number): number {
  return total > 0 ? Math.round((part / total) * 100) : 0;
}
