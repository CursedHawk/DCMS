/**
 * Formatters for the quantities this console actually shows.
 *
 * The console's rule is that a number appears with its ceiling or its window, never bare — so
 * these are built to be read as one phrase ("3.1 / 8 GB") rather than as isolated values.
 */

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'];

/** Bytes at a fixed unit, so a column of them can be compared without re-reading each unit. */
export function bytes(value: number, fractionDigits = 1): string {
  if (!Number.isFinite(value) || value <= 0) return '0 B';
  const exponent = Math.min(Math.floor(Math.log(value) / Math.log(1024)), UNITS.length - 1);
  const scaled = value / 1024 ** exponent;
  return `${scaled.toFixed(exponent === 0 ? 0 : fractionDigits)} ${UNITS[exponent]}`;
}

/** "3.1 / 8 GB" — both halves in the larger unit so the ratio is legible at a glance. */
export function ofLimit(used: number, limit: number): string {
  if (limit <= 0) return bytes(used);
  const exponent = Math.min(Math.floor(Math.log(limit) / Math.log(1024)), UNITS.length - 1);
  const scale = 1024 ** exponent;
  const digits = exponent === 0 ? 0 : 1;
  return `${(used / scale).toFixed(digits)} / ${(limit / scale).toFixed(digits)} ${UNITS[exponent]}`;
}

export function count(value: number): string {
  return new Intl.NumberFormat().format(value);
}

/**
 * "1 tenant", "2 tenants". Trivial, and here because the alternative is the template literal
 * that shipped "1 tenants running" into the first screenshot of this console.
 */
export function plural(value: number, one: string, many = `${one}s`): string {
  return `${count(value)} ${value === 1 ? one : many}`;
}

/**
 * A fill's state. The thresholds are the ones the platform's own alerts use — 80% is the
 * warning, 90% the page — so the colour here and the alert that wakes someone agree.
 */
export type RailState = 'nominal' | 'watch' | 'critical';

export function railState(fraction: number): RailState {
  if (fraction >= 0.9) return 'critical';
  if (fraction >= 0.8) return 'watch';
  return 'nominal';
}

/** Relative time, for "checked 12s ago". Absolute dates are shown where the exact moment matters. */
export function since(iso: string | null | undefined): string {
  if (!iso) return 'never';
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return `${Math.floor(seconds)}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86_400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86_400)}d ago`;
}

export function date(iso: string | null | undefined): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleString(undefined, {
    year: 'numeric', month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit',
  });
}
