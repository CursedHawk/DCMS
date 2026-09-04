/**
 * A bare SVG area, no chart library.
 *
 * recharts is 310 KB gzipped in the admin SPA's bundle and exists there to draw axes, legends,
 * tooltips and a dimension picker. What this console needs from a growth series is the shape
 * of the last ninety days at a glance — one path, no axes. Pulling in a charting runtime for
 * that would be most of the console's download budget spent on decoration.
 */
export function Sparkline({
  values,
  label,
  className,
}: {
  values: number[];
  label: string;
  className?: string;
}) {
  const width = 240;
  const height = 36;

  if (values.length === 0) {
    return <div className={className} aria-hidden style={{ height }} />;
  }

  // A series that is entirely zero has no shape to show. Drawn in the primary colour it read
  // as a horizontal rule — a divider between sections rather than a measurement of nothing —
  // so an empty period is drawn muted and unfilled. The number beside it still says zero.
  const empty = values.every((v) => v === 0);
  const max = Math.max(...values, 1);
  const step = values.length > 1 ? width / (values.length - 1) : width;
  const points = values.map((v, i) => [i * step, height - (v / max) * (height - 2)] as const);

  const line = points.map(([x, y], i) => `${i === 0 ? 'M' : 'L'}${x.toFixed(1)},${y.toFixed(1)}`).join(' ');
  const area = `${line} L${width},${height} L0,${height} Z`;
  const total = values.reduce((a, b) => a + b, 0);

  return (
    <svg
      viewBox={`0 0 ${width} ${height}`}
      preserveAspectRatio="none"
      className={className}
      role="img"
      aria-label={`${label}: ${total} over the period shown`}
    >
      {!empty && <path d={area} fill="hsl(var(--primary) / 0.12)" />}
      <path
        d={line}
        fill="none"
        stroke={empty ? 'hsl(var(--muted-foreground) / 0.35)' : 'hsl(var(--primary))'}
        strokeWidth={empty ? 1 : 1.5}
        strokeDasharray={empty ? '3 3' : undefined}
        vectorEffect="non-scaling-stroke"
      />
    </svg>
  );
}
