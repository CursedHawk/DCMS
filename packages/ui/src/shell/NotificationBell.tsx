import { Bell, CheckCheck, CircleAlert, CircleCheck, Info, TriangleAlert, X } from 'lucide-react';
import { useState } from 'react';
import { relativeTime } from '@dcms/core';
import { cn } from '../cn';
import { Button } from '../ui/button';
import { Popover, PopoverContent, PopoverTrigger } from '../ui/popover';
import { Tooltip, TooltipContent, TooltipTrigger } from '../ui/tooltip';

export type NotificationSeverity = 'Info' | 'Success' | 'Warning' | 'Error';

/**
 * The part of a notification the bell draws.
 *
 * Everything else about the two models differs — the tenant one carries i18n keys and a params
 * bag, the platform one a discriminator and a facts object — so the text arrives through
 * `renderItem` rather than being read off the row.
 */
export interface BellNotification {
  id: string;
  severity: NotificationSeverity;
  createdAt: string;
  readAt: string | null;
}

export interface BellLabels {
  title: string;
  markAllRead: string;
  empty: string;
  viewAll: string;
  dismiss: string;
  loading: string;
  /** Given the unread count, e.g. "3 unread notifications". Used as the button's label. */
  unread: (count: number) => string;
}

const severityIcon: Record<NotificationSeverity, typeof Info> = {
  Info,
  Success: CircleCheck,
  Warning: TriangleAlert,
  Error: CircleAlert,
};

const severityTone: Record<NotificationSeverity, string> = {
  Info: 'text-muted-foreground',
  Success: 'text-[hsl(var(--success))]',
  Warning: 'text-[hsl(var(--warning))]',
  Error: 'text-destructive',
};

/**
 * The bell, shared by both consoles.
 *
 * <p><b>Opening it marks nothing read.</b> The badge counts things that went wrong, and it has
 * to survive somebody glancing at the bell and closing it again. It clears on three deliberate
 * acts — opening an item, dismissing one, or "mark all read" — and a glance is none of them.
 * That rule came from the platform console; the tenant bell had it too, and they were two
 * implementations agreeing by coincidence.</p>
 */
export function NotificationBell<T extends BellNotification>({
  enabled = true,
  items,
  unreadCount,
  isLoading,
  labels,
  locale,
  renderItem,
  onOpenItem,
  onDismiss,
  onMarkAllRead,
  onViewAll,
}: {
  enabled?: boolean;
  items: readonly T[];
  unreadCount: number;
  isLoading?: boolean;
  labels: BellLabels;
  locale?: string;
  renderItem: (item: T) => { title: string; body?: string };
  onOpenItem: (item: T) => void;
  onDismiss: (item: T) => void;
  onMarkAllRead: () => void;
  onViewAll?: () => void;
}) {
  const [open, setOpen] = useState(false);
  if (!enabled) return null;

  return (
    <Popover open={open} onOpenChange={setOpen}>
      {/*
       * Tooltip and Popover triggers are both `asChild` Radix Slots on the same Button, so
       * their pointer handlers compose. Wrapping the Button in a plain element here instead
       * swallows the popover's handlers and it never opens — the same trap that once broke
       * the theme switch.
       */}
      <Tooltip>
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <Button
              variant="ghost"
              size="icon"
              className="relative"
              aria-label={unreadCount > 0 ? labels.unread(unreadCount) : labels.title}
            >
              <Bell className="h-4 w-4" aria-hidden />
              {unreadCount > 0 ? (
                <span
                  aria-hidden
                  className="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-destructive px-1 text-[10px] font-semibold leading-none text-destructive-foreground"
                >
                  {unreadCount > 99 ? '99+' : unreadCount}
                </span>
              ) : null}
            </Button>
          </PopoverTrigger>
        </TooltipTrigger>
        <TooltipContent>{labels.title}</TooltipContent>
      </Tooltip>

      <PopoverContent align="end" className="w-[min(24rem,calc(100vw-1.5rem))] p-0">
        <div className="flex items-center justify-between border-b px-3 py-2">
          <span className="text-sm font-semibold">{labels.title}</span>
          {unreadCount > 0 ? (
            <button
              type="button"
              onClick={onMarkAllRead}
              className="inline-flex items-center gap-1 text-xs text-muted-foreground transition-colors hover:text-foreground"
            >
              <CheckCheck className="h-3.5 w-3.5" aria-hidden />
              {labels.markAllRead}
            </button>
          ) : null}
        </div>

        <div className="max-h-96 overflow-y-auto">
          {isLoading ? (
            <p className="px-3 py-8 text-center text-sm text-muted-foreground">{labels.loading}</p>
          ) : items.length === 0 ? (
            <p className="px-3 py-8 text-center text-sm text-muted-foreground">{labels.empty}</p>
          ) : (
            items.map((item) => (
              <NotificationRow
                key={item.id}
                item={item}
                text={renderItem(item)}
                locale={locale}
                dismissLabel={labels.dismiss}
                onOpen={() => {
                  setOpen(false);
                  onOpenItem(item);
                }}
                onDismiss={() => onDismiss(item)}
              />
            ))
          )}
        </div>

        {onViewAll ? (
          <div className="border-t px-3 py-2">
            <button
              type="button"
              onClick={() => {
                setOpen(false);
                onViewAll();
              }}
              className="w-full text-center text-xs text-muted-foreground transition-colors hover:text-foreground"
            >
              {labels.viewAll}
            </button>
          </div>
        ) : null}
      </PopoverContent>
    </Popover>
  );
}

export function NotificationRow({
  item,
  text,
  locale,
  dismissLabel,
  onOpen,
  onDismiss,
}: {
  item: BellNotification;
  text: { title: string; body?: string };
  locale?: string;
  dismissLabel: string;
  onOpen: () => void;
  onDismiss: () => void;
}) {
  const Icon = severityIcon[item.severity] ?? Info;

  return (
    <div
      className={cn(
        'group flex items-start gap-2 border-b px-3 py-2.5 last:border-b-0',
        !item.readAt && 'bg-primary/[0.04]',
      )}
    >
      <Icon className={cn('mt-0.5 h-4 w-4 shrink-0', severityTone[item.severity])} aria-hidden />

      <button type="button" onClick={onOpen} className="min-w-0 flex-1 text-left">
        <p className={cn('truncate text-sm', !item.readAt && 'font-medium')}>{text.title}</p>
        {/* The short description the bell exists to show; the full text is behind the link. */}
        {text.body ? (
          <p className="mt-0.5 line-clamp-2 text-xs text-muted-foreground">{text.body}</p>
        ) : null}
        <p className="mt-1 text-[11px] text-muted-foreground/70">
          {relativeTime(item.createdAt, locale)}
        </p>
      </button>

      <button
        type="button"
        onClick={onDismiss}
        aria-label={dismissLabel}
        className="rounded opacity-0 transition-opacity hover:text-foreground focus-visible:opacity-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring group-hover:opacity-100"
      >
        <X className="h-3.5 w-3.5 text-muted-foreground" aria-hidden />
      </button>
    </div>
  );
}
