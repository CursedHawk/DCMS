/**
 * A user-agent string, shortened to the two things that let someone recognise their own device.
 *
 * <p>ponytail: a handful of substring checks rather than a UA-parsing dependency. The list is
 * what the console is actually reached from, the order matters (Edge and Opera both claim to be
 * Chrome, and everything claims to be Safari), and anything unrecognised falls back to the raw
 * string — which is still more useful than "Unknown".</p>
 */
export function describeAgent(userAgent: string | null | undefined): string {
  const ua = userAgent?.trim();
  if (!ua) return '';

  const browser =
    match(ua, [
      ['Edg/', 'Edge'],
      ['OPR/', 'Opera'],
      ['Firefox/', 'Firefox'],
      ['Chrome/', 'Chrome'],
      ['Safari/', 'Safari'],
    ]) ?? '';

  const platform =
    match(ua, [
      // Before "Linux": Android's UA contains both, and the phone is the useful half.
      ['Android', 'Android'],
      ['iPhone', 'iPhone'],
      ['iPad', 'iPad'],
      ['Windows', 'Windows'],
      ['Macintosh', 'macOS'],
      ['Mac OS X', 'macOS'],
      ['Linux', 'Linux'],
    ]) ?? '';

  const parts = [browser, platform].filter(Boolean);
  return parts.length > 0 ? parts.join(' · ') : ua;
}

function match(ua: string, table: [needle: string, label: string][]): string | null {
  for (const [needle, label] of table) {
    if (ua.includes(needle)) return label;
  }
  return null;
}
