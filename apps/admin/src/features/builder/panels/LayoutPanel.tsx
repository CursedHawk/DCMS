import {
  makeRegionEntry,
  regionHtmlPath,
  type LayoutEntry,
  type RegionEntry,
  type SiteManifest,
} from '@dcms/gjs-schema';
import { ArrowDownToLine, ArrowUpToLine, LayoutTemplate, Plus, Star, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button, Checkbox, cn, Input } from '@dcms/admin-ui';
import { useVfs } from '../../site-source';
import { useBuilder } from '../store';

/**
 * The site's shared chrome: the regions that appear around pages, and the
 * layouts that decide which pages get which.
 *
 * A region is `regions/<slug>.html` plus an entry in `site.json`; a layout is
 * only an entry, naming regions in order. Keeping the two apart is what lets one
 * header be reused by a layout with a footer and a layout without one, instead
 * of every page carrying its own copy of the navigation — which is exactly the
 * duplication that made a menu change a twelve-file edit.
 */
export function LayoutPanel() {
  const { t } = useTranslation();
  const project = useBuilder((s) => s.project);
  const activeSlug = useBuilder((s) => s.activeSlug);
  const activeKind = useBuilder((s) => s.activeKind);
  const [newRegion, setNewRegion] = useState('');
  const [adding, setAdding] = useState(false);

  if (!project) {
    return <div className="p-4 text-sm text-muted-foreground">{t('builder.noProject')}</div>;
  }

  const manifest = project.manifest;

  const addRegion = (placement: RegionEntry['placement']) => {
    const label = newRegion.trim();
    if (!label) return;
    const entry = makeRegionEntry(manifest, label, crypto.randomUUID(), placement);

    useBuilder.getState().update((current) => {
      // A region nobody shows is invisible work, so the first one creates the
      // layout that shows it and every later one joins the default. Authors who
      // want finer control edit the layouts below; authors who do not never have
      // to learn that layouts exist.
      const layouts = current.manifest.layouts.length
        ? current.manifest.layouts
        : [
            {
              id: crypto.randomUUID(),
              label: t('builder.regions.defaultLayout'),
              regions: [],
              default: true,
            },
          ];
      const targetId = layouts.find((l) => l.default)?.id ?? layouts[0]!.id;

      return {
        ...current,
        manifest: {
          ...current.manifest,
          regions: [...current.manifest.regions, entry],
          layouts: layouts.map((l) =>
            l.id === targetId ? { ...l, regions: [...l.regions, entry.id] } : l,
          ),
        },
        regions: [...current.regions, { entry, html: starterHtml(placement, label) }],
      };
    });

    setNewRegion('');
    setAdding(false);
    useBuilder.getState().setActiveTarget({ kind: 'region', slug: entry.slug });
  };

  const removeRegion = (entry: RegionEntry) => {
    useBuilder.getState().update((current) => ({
      ...current,
      manifest: {
        ...current.manifest,
        regions: current.manifest.regions.filter((r) => r.id !== entry.id),
        // A layout left holding a dangling id would render fine (the resolver
        // filters it out) but would silently re-adopt the region if that id ever
        // came back, so the reference goes with the region.
        layouts: current.manifest.layouts.map((l) => ({
          ...l,
          regions: l.regions.filter((id) => id !== entry.id),
        })),
      },
      regions: current.regions.filter((r) => r.entry.id !== entry.id),
    }));
    // `update` only diffs the files a project maps to, and a removed region no
    // longer maps to any — delete it explicitly so the draft matches.
    useVfs.getState().deleteFile(regionHtmlPath(entry.slug));
    if (activeKind === 'region' && activeSlug === entry.slug) {
      const home = project.pages[0];
      if (home) useBuilder.getState().setActiveSlug(home.entry.slug);
    }
  };

  const setPlacement = (entry: RegionEntry, placement: RegionEntry['placement']) => {
    useBuilder.getState().update((current) => ({
      ...current,
      manifest: {
        ...current.manifest,
        regions: current.manifest.regions.map((r) => (r.id === entry.id ? { ...r, placement } : r)),
      },
      regions: current.regions.map((r) =>
        r.entry.id === entry.id ? { ...r, entry: { ...r.entry, placement } } : r,
      ),
    }));
  };

  const renameRegion = (entry: RegionEntry, label: string) => {
    const trimmed = label.trim();
    if (!trimmed || trimmed === entry.label) return;
    // The slug is the file name; only the label changes, so nothing moves.
    useBuilder.getState().update((current) => ({
      ...current,
      manifest: {
        ...current.manifest,
        regions: current.manifest.regions.map((r) =>
          r.id === entry.id ? { ...r, label: trimmed } : r,
        ),
      },
      regions: current.regions.map((r) =>
        r.entry.id === entry.id ? { ...r, entry: { ...r.entry, label: trimmed } } : r,
      ),
    }));
  };

  const addLayout = () => {
    useBuilder.getState().updateManifest((m) => ({
      ...m,
      layouts: [
        ...m.layouts,
        {
          id: crypto.randomUUID(),
          label: uniqueLabel(m, t('builder.regions.newLayout')),
          regions: [],
          default: m.layouts.length === 0,
        },
      ],
    }));
  };

  const removeLayout = (layout: LayoutEntry) => {
    useBuilder.getState().updateManifest((m) => ({
      ...m,
      layouts: m.layouts.filter((l) => l.id !== layout.id),
      // A page pointing at the layout that just went away falls back to the
      // default, which is what `layoutForPage` already does — but leaving the
      // stale id in the file would let a later layout with a recycled id
      // silently adopt the page.
      pages: m.pages.map((p) => (p.layout === layout.id ? { ...p, layout: undefined } : p)),
    }));
  };

  const toggleRegionInLayout = (layout: LayoutEntry, region: RegionEntry, on: boolean) => {
    useBuilder.getState().updateManifest((m) => ({
      ...m,
      layouts: m.layouts.map((l) =>
        l.id === layout.id
          ? {
              ...l,
              regions: on ? [...l.regions, region.id] : l.regions.filter((id) => id !== region.id),
            }
          : l,
      ),
    }));
  };

  const setDefaultLayout = (layout: LayoutEntry) => {
    useBuilder.getState().updateManifest((m) => ({
      ...m,
      layouts: m.layouts.map((l) => ({ ...l, default: l.id === layout.id })),
    }));
  };

  const renameLayout = (layout: LayoutEntry, label: string) => {
    const trimmed = label.trim();
    if (!trimmed || trimmed === layout.label) return;
    useBuilder.getState().updateManifest((m) => ({
      ...m,
      layouts: m.layouts.map((l) => (l.id === layout.id ? { ...l, label: trimmed } : l)),
    }));
  };

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center justify-between border-b px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          {t('builder.regions.title')}
        </span>
        <Button
          size="icon"
          variant="ghost"
          onClick={() => setAdding((v) => !v)}
          title={t('builder.regions.add')}
        >
          <Plus className="h-4 w-4" />
        </Button>
      </div>

      {adding && (
        <div className="space-y-2 border-b p-2">
          <Input
            autoFocus
            value={newRegion}
            placeholder={t('builder.regions.namePlaceholder')}
            onChange={(e) => setNewRegion(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') addRegion('before');
              if (e.key === 'Escape') setAdding(false);
            }}
          />
          <div className="flex gap-1">
            <Button size="sm" className="flex-1" onClick={() => addRegion('before')}>
              <ArrowUpToLine className="mr-1 h-3.5 w-3.5" />
              {t('builder.regions.abovePage')}
            </Button>
            <Button
              size="sm"
              variant="secondary"
              className="flex-1"
              onClick={() => addRegion('after')}
            >
              <ArrowDownToLine className="mr-1 h-3.5 w-3.5" />
              {t('builder.regions.belowPage')}
            </Button>
          </div>
        </div>
      )}

      <div className="min-h-0 flex-1 overflow-auto">
        <ul className="p-1">
          {manifest.regions.length === 0 && (
            <li className="px-2 py-3 text-xs text-muted-foreground">{t('builder.regions.empty')}</li>
          )}
          {manifest.regions.map((entry) => {
            const open = activeKind === 'region' && activeSlug === entry.slug;
            return (
              <li key={entry.id}>
                <div
                  className={cn(
                    'group flex items-center gap-1 rounded-md px-2 py-1.5 text-sm',
                    open ? 'bg-accent text-accent-foreground' : 'hover:bg-accent/50',
                  )}
                >
                  <button
                    type="button"
                    className="flex min-w-0 flex-1 items-center gap-2 text-left"
                    onClick={() =>
                      useBuilder.getState().setActiveTarget({ kind: 'region', slug: entry.slug })
                    }
                    onDoubleClick={() => {
                      const label = prompt(t('builder.regions.namePlaceholder'), entry.label);
                      if (label !== null) renameRegion(entry, label);
                    }}
                  >
                    <LayoutTemplate className="h-4 w-4 shrink-0 text-muted-foreground" />
                    <span className="min-w-0 flex-1 truncate">{entry.label}</span>
                  </button>
                  <button
                    type="button"
                    title={
                      entry.placement === 'before'
                        ? t('builder.regions.abovePage')
                        : t('builder.regions.belowPage')
                    }
                    onClick={() =>
                      setPlacement(entry, entry.placement === 'before' ? 'after' : 'before')
                    }
                    className="shrink-0 rounded p-1 text-muted-foreground hover:text-foreground"
                  >
                    {entry.placement === 'before' ? (
                      <ArrowUpToLine className="h-3.5 w-3.5" />
                    ) : (
                      <ArrowDownToLine className="h-3.5 w-3.5" />
                    )}
                  </button>
                  <button
                    type="button"
                    title={t('actions.delete')}
                    onClick={() => {
                      if (confirm(t('builder.regions.confirmDelete', { label: entry.label }))) {
                        removeRegion(entry);
                        toast.success(t('builder.regions.deleted', { label: entry.label }));
                      }
                    }}
                    className="shrink-0 rounded p-1 text-muted-foreground opacity-0 hover:text-destructive group-hover:opacity-100"
                  >
                    <Trash2 className="h-3.5 w-3.5" />
                  </button>
                </div>
              </li>
            );
          })}
        </ul>

        <div className="mt-2 flex items-center justify-between border-y px-3 py-2">
          <span className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
            {t('builder.regions.layouts')}
          </span>
          <Button
            size="icon"
            variant="ghost"
            onClick={addLayout}
            title={t('builder.regions.addLayout')}
          >
            <Plus className="h-4 w-4" />
          </Button>
        </div>

        <ul className="p-1">
          {manifest.layouts.length === 0 && (
            <li className="px-2 py-3 text-xs text-muted-foreground">
              {t('builder.regions.noLayouts')}
            </li>
          )}
          {manifest.layouts.map((layout) => (
            <li key={layout.id} className="mb-2 rounded-md border">
              <div className="flex items-center gap-1 border-b px-2 py-1.5">
                <button
                  type="button"
                  className="min-w-0 flex-1 truncate text-left text-sm"
                  onDoubleClick={() => {
                    const label = prompt(t('builder.regions.layoutName'), layout.label);
                    if (label !== null) renameLayout(layout, label);
                  }}
                >
                  {layout.label}
                </button>
                <button
                  type="button"
                  title={t('builder.regions.makeDefault')}
                  onClick={() => setDefaultLayout(layout)}
                  className={cn(
                    'shrink-0 rounded p-1',
                    layout.default ? 'text-primary' : 'text-muted-foreground hover:text-foreground',
                  )}
                >
                  <Star className="h-3.5 w-3.5" />
                </button>
                <button
                  type="button"
                  title={t('actions.delete')}
                  onClick={() => removeLayout(layout)}
                  className="shrink-0 rounded p-1 text-muted-foreground hover:text-destructive"
                >
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              </div>
              <div className="space-y-1 p-2">
                {manifest.regions.length === 0 && (
                  <p className="text-xs text-muted-foreground">{t('builder.regions.empty')}</p>
                )}
                {manifest.regions.map((region) => (
                  <label key={region.id} className="flex items-center gap-2 text-sm">
                    <Checkbox
                      checked={layout.regions.includes(region.id)}
                      onCheckedChange={(v) => toggleRegionInLayout(layout, region, v === true)}
                    />
                    <span className="min-w-0 flex-1 truncate">{region.label}</span>
                    <span className="shrink-0 text-xs text-muted-foreground">
                      {region.placement === 'before'
                        ? t('builder.regions.above')
                        : t('builder.regions.below')}
                    </span>
                  </label>
                ))}
              </div>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}

/**
 * What a new region starts as.
 *
 * An empty region is a zero-height band the author cannot click, so it comes
 * with one visible element — enough to grab, style and replace.
 */
function starterHtml(placement: RegionEntry['placement'], label: string): string {
  const tag = placement === 'before' ? 'header' : 'footer';
  return `<${tag} class="dcms-region-inner">\n  <p>${escapeText(label)}</p>\n</${tag}>\n`;
}

function escapeText(value: string): string {
  return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function uniqueLabel(manifest: SiteManifest, base: string): string {
  const taken = new Set(manifest.layouts.map((l) => l.label));
  if (!taken.has(base)) return base;
  let n = 2;
  while (taken.has(`${base} ${n}`)) n += 1;
  return `${base} ${n}`;
}
