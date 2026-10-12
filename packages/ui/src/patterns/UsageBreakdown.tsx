import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronRight, Database, File, GitBranch } from 'lucide-react';
import { bytes } from '@dcms/core';
import { cn } from '../cn';

export type UsageKind = 'database' | 'files' | 'git';

/** One item of a usage snapshot, as admin-api measures it. */
export interface UsageItem {
  key: string;
  label: string;
  kind: UsageKind;
  bytes: number;
  rows?: number | null;
}

/** One category of a usage snapshot, as admin-api measures it. */
export interface UsageCategory {
  key: string;
  bytes: number;
  items: UsageItem[];
}

/** The snapshot both consoles read: admin's `/api/admin/usage`, the platform's tenant page. */
export interface UsageSnapshot {
  measuredAt: string | null;
  totalBytes: number;
  categories: UsageCategory[];
}

/**
 * One colour per category, the same in both consoles. Tailwind's 500s read on both themes; the
 * name and the number are always written beside the swatch, so colour is never the only signal.
 */
const SWATCH: Record<string, string> = {
  media: 'bg-sky-500',
  sites: 'bg-violet-500',
  dynamicApps: 'bg-emerald-500',
  plugins: 'bg-amber-500',
  ai: 'bg-rose-500',
  audit: 'bg-stone-400',
  workspace: 'bg-cyan-600',
};

const CATEGORY: Record<string, string> = {
  media: 'Media',
  sites: 'Sites',
  plugins: 'Plugins',
  dynamicApps: 'Dynamic Apps',
  ai: 'AI assistant',
  audit: 'Audit log',
  workspace: 'Workspace',
};

const SITE_PART: Record<string, string> = {
  live: '{{name}} (published build)',
  history: '{{name}} (earlier builds)',
  git: '{{name}} (git repository)',
};

const KIND_ICON = { database: Database, files: File, git: GitBranch } as const;
const KIND: Record<UsageKind, string> = { database: 'Database', files: 'Files', git: 'Git repository' };

/**
 * What a tenant stores, as one ruler and a ranked list.
 *
 * <p>The ruler is the whole tenant, each category a stretch of it, so "what is taking the
 * space" is answered before a word is read. Below it the categories in descending order; each
 * opens to its items — a plugin, a site's earlier builds, a table of records — with where the
 * bytes live (database rows, files, a git repository).</p>
 *
 * <p>Labels go through `usage.*` keys with English defaults: the admin translates them, the
 * platform console reads the defaults. Plugin and site names come from the snapshot as they are.</p>
 */
export function UsageBreakdown({
  categories,
  totalBytes,
  className,
}: {
  categories: UsageCategory[];
  totalBytes: number;
  className?: string;
}) {
  const { t } = useTranslation();
  const [open, setOpen] = useState<string | null>(null);
  const scale = Math.max(totalBytes, 1);
  const share = (b: number) => {
    const p = (b / scale) * 100;
    return p > 0 && p < 1 ? '<1%' : `${Math.round(p)}%`;
  };
  const categoryLabel = (key: string) => t(`usage.category.${key}`, { defaultValue: CATEGORY[key] ?? key });
  const itemLabel = (item: UsageItem) => {
    const site = /^site:[^:]+:(live|history|git)$/.exec(item.key);
    if (site) return t(`usage.site.${site[1]}`, { name: item.label, defaultValue: SITE_PART[site[1]] });
    // Plugin items carry the plugin's own name; ':' would also read as an i18next namespace.
    if (item.key === 'plugin:content') return t('usage.item.deletedPlugins', { defaultValue: item.label });
    if (item.key.includes(':')) return item.label;
    return t(`usage.item.${item.key}`, { defaultValue: item.label });
  };

  return (
    <div className={cn('space-y-4', className)}>
      <div className="flex h-3 w-full overflow-hidden rounded-sm bg-muted" aria-hidden>
        {categories.map((c) => (
          <div
            key={c.key}
            className={cn('h-full', SWATCH[c.key] ?? 'bg-muted-foreground/50')}
            style={{ width: `${(c.bytes / scale) * 100}%` }}
            title={`${categoryLabel(c.key)}: ${bytes(c.bytes)}`}
          />
        ))}
      </div>

      <ul className="divide-y rounded-md border">
        {categories.map((c) => {
          const expanded = open === c.key;
          const panelId = `usage-${c.key}`;
          return (
            <li key={c.key}>
              <button
                type="button"
                aria-expanded={expanded}
                aria-controls={panelId}
                onClick={() => setOpen(expanded ? null : c.key)}
                className="grid w-full grid-cols-[auto_1fr_auto] items-center gap-x-3 px-3 py-2.5 text-left text-sm hover:bg-accent/50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
              >
                <span className={cn('h-2.5 w-2.5 rounded-sm', SWATCH[c.key] ?? 'bg-muted-foreground/50')} />
                <span className="flex min-w-0 items-center gap-1.5">
                  <ChevronRight
                    aria-hidden
                    className={cn('h-3.5 w-3.5 shrink-0 text-muted-foreground transition-transform motion-reduce:transition-none', expanded && 'rotate-90')}
                  />
                  <span className="truncate font-medium">{categoryLabel(c.key)}</span>
                </span>
                <span className="flex items-baseline gap-3 tabular-nums">
                  <span className="w-10 text-right text-xs text-muted-foreground">{share(c.bytes)}</span>
                  <span className="w-20 text-right font-medium">{bytes(c.bytes)}</span>
                </span>
              </button>
              {expanded && (
                <ul id={panelId} className="border-t bg-muted/30 px-3 py-1.5">
                  {c.items.map((item) => {
                    const Icon = KIND_ICON[item.kind] ?? File;
                    return (
                      <li
                        key={`${item.key}:${item.kind}`}
                        className="grid grid-cols-[auto_1fr_auto] items-center gap-x-3 py-1.5 pl-5 text-sm"
                      >
                        <Icon
                          role="img"
                          aria-label={t(`usage.kind.${item.kind}`, { defaultValue: KIND[item.kind] })}
                          className="h-3.5 w-3.5 text-muted-foreground"
                        />
                        <span className="min-w-0">
                          <span className="block [overflow-wrap:anywhere]">{itemLabel(item)}</span>
                          {item.rows != null && (
                            <span className="block text-xs text-muted-foreground">
                              {t('usage.rows', {
                                count: item.rows,
                                defaultValue_one: '{{count, number}} row',
                                defaultValue_other: '{{count, number}} rows',
                              })}
                            </span>
                          )}
                        </span>
                        <span className="flex items-baseline gap-3 tabular-nums">
                          <span className="w-10 text-right text-xs text-muted-foreground">{share(item.bytes)}</span>
                          <span className="w-20 text-right">{bytes(item.bytes)}</span>
                        </span>
                      </li>
                    );
                  })}
                </ul>
              )}
            </li>
          );
        })}
      </ul>
    </div>
  );
}
