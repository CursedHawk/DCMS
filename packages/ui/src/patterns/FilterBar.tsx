import { Search, X } from 'lucide-react';
import { cn } from '../cn';
import { Badge } from '../ui/badge';
import { Button } from '../ui/button';
import { Input } from '../ui/input';

/**
 * The row of controls above a list.
 *
 * <p>A container rather than a component that owns the filters, because what a list filters on
 * is entirely its own business. What is shared, and what every screen was reimplementing
 * slightly differently, is the shape: a search box that grows, controls that wrap onto a second
 * line instead of overflowing, and a count of what is currently narrowing the results with one
 * click to clear it.</p>
 *
 * <p>The active-filter count matters more than it looks. A list that appears empty because of a
 * filter set three minutes ago and since forgotten is the most common "the data is gone" report
 * there is.</p>
 */
export function FilterBar({
  search,
  onSearchChange,
  searchPlaceholder = 'Search',
  activeCount = 0,
  onClear,
  clearLabel = 'Clear filters',
  children,
  actions,
  className,
}: {
  search?: string;
  onSearchChange?: (value: string) => void;
  searchPlaceholder?: string;
  /** How many filters are narrowing the list right now, excluding the search box. */
  activeCount?: number;
  onClear?: () => void;
  clearLabel?: string;
  /** The filter controls themselves — selects, toggles, date ranges. */
  children?: React.ReactNode;
  /** Right-aligned actions: "New", "Export", bulk operations. */
  actions?: React.ReactNode;
  className?: string;
}) {
  return (
    <div className={cn('mb-4 flex flex-wrap items-center gap-2', className)}>
      {onSearchChange ? (
        <div className="relative min-w-48 flex-1 sm:max-w-xs">
          <Search
            className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
            aria-hidden
          />
          <Input
            type="search"
            value={search ?? ''}
            onChange={(e) => onSearchChange(e.target.value)}
            placeholder={searchPlaceholder}
            aria-label={searchPlaceholder}
            className="pl-8"
          />
        </div>
      ) : null}

      {children}

      {activeCount > 0 && onClear ? (
        <Button variant="ghost" size="sm" onClick={onClear} className="gap-1.5">
          <Badge tone="default">{activeCount}</Badge>
          {clearLabel}
          <X className="h-3.5 w-3.5" aria-hidden />
        </Button>
      ) : null}

      {actions ? <div className="ml-auto flex items-center gap-2">{actions}</div> : null}
    </div>
  );
}
