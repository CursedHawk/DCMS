/**
 * A screenshot of the admin console, in the page's theme. Both images render and the `.dark`
 * class picks one (same reason as the theme toggle: no React state, so SSR and hydration agree);
 * `loading="lazy"` keeps the hidden one from downloading. Shots come from
 * scripts/capture-shots.mjs.
 */
export function Shot({
  name,
  alt,
  className = '',
  hidden = false,
}: {
  name: string;
  alt: string;
  className?: string;
  /** Out of the accessibility tree: a stacked shot that is not the one showing. */
  hidden?: boolean;
}) {
  const img = 'block h-auto w-full';
  return (
    <div
      aria-hidden={hidden || undefined}
      className={`overflow-hidden rounded-xl bg-card shadow-[0_30px_80px_-30px_hsl(var(--foreground)/0.35)] ring-1 ring-foreground/10 dark:shadow-[0_30px_80px_-30px_hsl(var(--primary)/0.35)] ${className}`}
    >
      <img
        src={`/shots/${name}-light.webp`}
        alt={alt}
        width={2160}
        height={1350}
        loading="lazy"
        decoding="async"
        className={`${img} dark:hidden`}
      />
      <img
        src={`/shots/${name}-dark.webp`}
        alt={alt}
        width={2160}
        height={1350}
        loading="lazy"
        decoding="async"
        className={`${img} hidden dark:block`}
      />
    </div>
  );
}
