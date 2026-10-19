import { cn } from './cn';

/**
 * Standard page scaffold: padded container with a header row + content.
 *
 * <p>One width for every page. Pages used to pick their own — 7xl, 6xl, 3xl, 2xl, none — so
 * moving between two Settings sections made the content jump sideways. A form that reads better
 * narrow constrains its own card; the page around it stays put.</p>
 */
export function Page({ children, className }: { children: React.ReactNode; className?: string }) {
  return <div className={cn('mx-auto w-full max-w-6xl p-4 sm:p-6', className)}>{children}</div>;
}

/**
 * The top of a page: where it sits, what it is, and what you can do here.
 *
 * <p>`crumbs` is for pages more than one step from the menu — the menu already says where a
 * top-level page is. The title block is `min-w-0` with `overflow-wrap:anywhere` because titles
 * are often names somebody typed (a plugin instance, a site), and an unbroken one used to push
 * the actions off the edge of a phone.</p>
 */
export function PageHeader({
  title,
  description,
  actions,
  crumbs,
}: {
  title: React.ReactNode;
  description?: React.ReactNode;
  actions?: React.ReactNode;
  crumbs?: React.ReactNode;
}) {
  return (
    <div className="mb-6">
      {crumbs}
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 flex-1 [overflow-wrap:anywhere]">
          <h1 className="text-2xl font-bold tracking-tight">{title}</h1>
          {description ? (
            <div className="mt-1 text-sm text-muted-foreground">{description}</div>
          ) : null}
        </div>
        {actions ? (
          <div className="flex max-w-full flex-wrap items-center gap-2">{actions}</div>
        ) : null}
      </div>
    </div>
  );
}

/**
 * A section within a page — the `h2` level of `PageHeader`.
 *
 * <p>Section headings had four styles (`text-sm`, `text-lg`, unsized `font-medium`, muted), so
 * two pages side by side never looked like one product. Actions that belong to a section — "New
 * item" over a list — go in its `actions`, not floating above it.</p>
 */
export function SectionHeader({
  title,
  description,
  actions,
  className,
}: {
  title: React.ReactNode;
  description?: React.ReactNode;
  actions?: React.ReactNode;
  className?: string;
}) {
  return (
    <div className={cn('mb-3 flex flex-wrap items-end justify-between gap-2', className)}>
      <div className="min-w-0 flex-1 [overflow-wrap:anywhere]">
        <h2 className="text-base font-semibold">{title}</h2>
        {description ? <p className="mt-0.5 text-sm text-muted-foreground">{description}</p> : null}
      </div>
      {actions ? (
        <div className="flex max-w-full flex-wrap items-center gap-2">{actions}</div>
      ) : null}
    </div>
  );
}
