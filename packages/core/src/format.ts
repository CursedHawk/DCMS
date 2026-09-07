/**
 * The formatters both consoles show numbers and times with.
 *
 * These existed three times over before this file did: the platform console's `lib/format.ts`,
 * the admin IDE's `useRelativeTime`, and a third relative-time formatter inside the notification
 * bell built on i18n keys. All three answered "how long ago", and all three answered it
 * differently — one in English only, one via `Intl`, one via a locale bundle that then had to
 * carry `notifications.time.*` in every language.
 *
 * `Intl.RelativeTimeFormat` does the job for all three and is already localised for every
 * language a browser ships, so the keys are gone rather than translated.
 */

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'] as const;

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

export function count(value: number, locale?: string): string {
  return new Intl.NumberFormat(locale).format(value);
}

/**
 * "1 tenant", "2 tenants". Trivial, and here because the alternative is the template literal
 * that shipped "1 tenants running" into the first screenshot of the platform console.
 */
export function plural(value: number, one: string, many = `${one}s`, locale?: string): string {
  return `${count(value, locale)} ${value === 1 ? one : many}`;
}

const RELATIVE_UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 31_536_000_000],
  ['month', 2_592_000_000],
  ['week', 604_800_000],
  ['day', 86_400_000],
  ['hour', 3_600_000],
  ['minute', 60_000],
  ['second', 1000],
];

/**
 * "2 hours ago", "in 3 days", in the reader's own language.
 *
 * Deliberately not a live-ticking clock. The notification bell can hold twenty rows and a
 * per-row interval would re-render the popover every second to move one of them.
 */
export function relativeTime(
  iso: string | null | undefined,
  locale?: string,
  fallback = '',
): string {
  if (!iso) return fallback;
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return fallback;
  const diff = then - Date.now();
  const abs = Math.abs(diff);
  const rtf = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
  for (const [unit, ms] of RELATIVE_UNITS) {
    if (abs >= ms || unit === 'second') return rtf.format(Math.round(diff / ms), unit);
  }
  return fallback;
}

/** Date and time to the minute. Used wherever the exact moment matters more than the distance. */
export function dateTime(iso: string | null | undefined, locale?: string, fallback = '—'): string {
  if (!iso) return fallback;
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return fallback;
  return at.toLocaleString(locale, {
    year: 'numeric',
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

/**
 * A fill's state. The thresholds are the ones the platform's own alerts use — 80% is the
 * warning, 90% the page — so the colour here and the alert that wakes someone agree.
 */
export type MeterState = 'nominal' | 'watch' | 'critical';

export function meterState(fraction: number): MeterState {
  if (fraction >= 0.9) return 'critical';
  if (fraction >= 0.8) return 'watch';
  return 'nominal';
}
