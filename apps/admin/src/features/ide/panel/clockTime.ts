/**
 * `14:32:05`, in the viewer's locale.
 *
 * <p>Seconds, because every surface that uses this is a log and the question asked of a log is
 * ordering — which of these two things happened first, and how far apart. Minutes alone collapse
 * a burst of builds into one timestamp.</p>
 */
export function clockTime(at: number): string {
  return new Date(at).toLocaleTimeString(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
}
