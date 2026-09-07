import { useTheme } from '@dcms/ui';

/**
 * The chart palette.
 *
 * <p><b>These hexes are validated, not chosen by eye.</b> Each was run through the dataviz
 * validator against the surface it actually sits on — the light steps against white, the dark
 * steps against the dark card (`#0f1521`) — for the lightness band, the chroma floor and 3:1
 * contrast. The dark steps are *selected*, not an automatic lightening of the light ones: the
 * dark-mode lightness band is L 0.48–0.67, and the obvious choice (`#818cf8`, indigo-400) sits
 * at 0.68, just outside it.</p>
 *
 * <p>Only <b>one</b> series colour, because the charts are small multiples rather than an
 * overlay. Two categorical hues would need to clear a colour-vision separation threshold, and
 * the previous chart had a second problem they would not have fixed: events outnumber visitors
 * by an order of magnitude, so drawing both against one axis flattened visitors into the
 * baseline. Splitting them gives each its own scale and leaves identity to the chart title,
 * where no reader has to decode a colour at all.</p>
 *
 * <p>The comparison series is the same measure a period earlier, so it is deliberately not a
 * second identity: it is the same hue, muted and dashed. The dash is secondary encoding, which
 * is what makes it legible without colour.</p>
 */
export function useChartTheme() {
  const { resolved } = useTheme();
  const dark = resolved === 'dark';

  return {
    /** The measure being charted. */
    series: dark ? '#7c83f5' : '#4f46e5',
    /** The same measure, one period earlier. Same hue, recessive. */
    comparison: dark ? '#7c83f5' : '#4f46e5',
    comparisonOpacity: 0.45,
    comparisonDash: '4 3',
    grid: 'hsl(var(--border))',
    axis: 'hsl(var(--muted-foreground))',
    tooltip: {
      background: 'hsl(var(--popover))',
      border: '1px solid hsl(var(--border))',
      borderRadius: 8,
      fontSize: 12,
      color: 'hsl(var(--popover-foreground))',
    },
  };
}

/**
 * The fractional change between two periods, or null when there is nothing to compare against.
 *
 * <p>Returns null rather than 0 for a previous period of zero. Going from no visitors to forty
 * is not "+0%" and it is not "+∞%" either — it is a first period, and the honest thing is to
 * show no trend at all rather than a number that reads as precise.</p>
 */
export function trend(current: number, previous: number | undefined): number | null {
  if (previous === undefined || previous === 0) return null;
  return (current - previous) / previous;
}

/** One day of the series, as the dashboard endpoint returns it. */
export interface DayPoint {
  day: string;
  events: number;
  visitors: number;
}

export /**
 * Aligns this period's series with the previous one, by position rather than by date.
 *
 * <p>By position is the point: the comparison is "the same day of the previous window", and the
 * two windows have different dates by construction. Joining on the date would match nothing.</p>
 */
function mergeSeries(
  current: { day: string; events: number; visitors: number }[],
  earlier: { day: string; events: number; visitors: number }[] | undefined,
  measure: 'events' | 'visitors',
): { day: string; value: number; comparison: number | null }[] {
  // The previous window is aligned to the END of the series, so the most recent day of each
  // lines up. Aligning from the start puts them out of step whenever one window has a day
  // fewer, which happens on every month boundary.
  const offset = earlier ? earlier.length - current.length : 0;
  return current.map((point, index) => {
    const partner = earlier?.[index + offset];
    return {
      day: point.day,
      value: point[measure],
      comparison: partner ? partner[measure] : null,
    };
  });
}
