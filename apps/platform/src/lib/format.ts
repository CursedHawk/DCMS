/**
 * The console's formatters now live in `@dcms/core` — the admin SPA had grown two more
 * relative-time formatters of its own, and three answers to "how long ago" is two too many.
 *
 * This file stays as the console's local vocabulary: `date` and `since` are the names the
 * pages here read well with, and `railState` names a capacity rail rather than a generic meter.
 */
export { bytes, ofLimit, count, plural } from '@dcms/core';
import { dateTime, meterState, relativeTime, type MeterState } from '@dcms/core';

export type RailState = MeterState;
export const railState = meterState;

/** Relative time, for "checked 12s ago". Absolute dates are shown where the exact moment matters. */
export const since = (iso: string | null | undefined): string => relativeTime(iso, undefined, 'never');

export const date = (iso: string | null | undefined): string => dateTime(iso);
