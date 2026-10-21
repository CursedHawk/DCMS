/** What `<input type="datetime-local">` itself writes: a wall-clock time with no zone. */
const LOCAL = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,3})?)?$/;

/**
 * A stored timestamp as `<input type="datetime-local">` will show it.
 *
 * The input accepts only a zone-less `YYYY-MM-DDTHH:mm[:ss[.sss]]` and renders anything else as
 * empty — which is how every date a sync or the API wrote (`2026-10-07T16:00:23.0000000+00:00`)
 * looked unset in the editor. A value with a zone is shown in the viewer's local time, to the
 * minute unless it has seconds; one the input wrote itself passes through.
 *
 * Mirrored by `localInput()` in site-runtime's recordForm.tsx, which ships to sites and cannot
 * import this package.
 */
export function toDateTimeLocal(value: unknown): string {
  if (typeof value !== 'string' || value === '') return '';
  if (LOCAL.test(value)) return value;
  const at = new Date(value);
  if (Number.isNaN(at.getTime())) return '';
  const local = new Date(at.getTime() - at.getTimezoneOffset() * 60_000).toISOString().slice(0, 19);
  return local.endsWith(':00') ? local.slice(0, 16) : local;
}
