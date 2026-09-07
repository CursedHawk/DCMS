import { useMemo, useState } from 'react';
import { useNavigate } from '@tanstack/react-router';
import { Check, Package, Plus, ShieldCheck, Store } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import {
  Badge,
  Button,
  EmptyState,
  FilterBar,
  InfoHint,
  Page,
  PageHeader,
  Sheet,
  SheetBody,
  SheetContent,
  SheetFooter,
  SheetHeader,
  SheetTitle,
  Skeleton,
  cn,
} from '@dcms/ui';
import { iconByName } from '../../app/navApi';
import { useMarketplace, type MarketplaceItem } from './api';

/**
 * The shopfront.
 *
 * <p>Plugins are compiled in, so this is discovery and enablement rather than installing
 * third-party code — but the discovery problem is real either way: eighteen plugins presented
 * as a flat list of names tells a tenant admin nothing about which of them solves their
 * problem.</p>
 *
 * <p>The detail panel states <b>what a plugin will ask permission to do, and whether it adds a
 * menu entry, before it is added</b>. Those are the two things a workspace owner is actually
 * deciding, and both used to be discoverable only afterwards — one in the roles matrix, the
 * other by noticing the sidebar had changed.</p>
 */
export function MarketplacePage() {
  const { t } = useTranslation();
  const marketplace = useMarketplace();
  const [search, setSearch] = useState('');
  const [category, setCategory] = useState<string | null>(null);
  const [selected, setSelected] = useState<MarketplaceItem | null>(null);

  const items = marketplace.data?.items ?? [];

  const categories = useMemo(
    () => [...new Set(items.map((i) => i.category))].sort(),
    [items],
  );

  const visible = useMemo(() => {
    const needle = search.trim().toLowerCase();
    return items.filter((item) => {
      if (category && item.category !== category) return false;
      if (!needle) return true;
      return (
        item.name.toLowerCase().includes(needle) ||
        item.summary.toLowerCase().includes(needle) ||
        item.tags.some((tag) => tag.toLowerCase().includes(needle))
      );
    });
  }, [items, search, category]);

  return (
    <Page>
      <PageHeader
        title={t('nav.marketplace')}
        description={t('marketplace.description')}
      />

      <FilterBar
        search={search}
        onSearchChange={setSearch}
        searchPlaceholder={t('marketplace.searchPlaceholder')}
        activeCount={category ? 1 : 0}
        onClear={() => setCategory(null)}
        clearLabel={t('marketplace.clearCategory')}
      >
        <div className="flex flex-wrap gap-1">
          {categories.map((name) => (
            <button
              key={name}
              type="button"
              aria-pressed={category === name}
              onClick={() => setCategory((current) => (current === name ? null : name))}
              className={cn(
                'rounded-full border px-2.5 py-1 text-xs transition-colors',
                'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                category === name
                  ? 'border-primary bg-primary/10 text-primary'
                  : 'text-muted-foreground hover:bg-accent',
              )}
            >
              {name}
            </button>
          ))}
        </div>
      </FilterBar>

      {marketplace.isLoading ? (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {Array.from({ length: 6 }, (_, i) => (
            <Skeleton key={i} className="h-32 rounded-lg" />
          ))}
        </div>
      ) : visible.length === 0 ? (
        <EmptyState
          icon={Store}
          title={t('marketplace.noneTitle')}
          description={t('marketplace.noneDescription')}
        />
      ) : (
        <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {visible.map((item) => (
            <li key={item.id}>
              <PluginCard item={item} onOpen={() => setSelected(item)} />
            </li>
          ))}
        </ul>
      )}

      <Sheet open={!!selected} onOpenChange={(open) => !open && setSelected(null)}>
        <SheetContent side="right" width="w-[28rem]">
          {selected ? <PluginDetail item={selected} /> : null}
        </SheetContent>
      </Sheet>
    </Page>
  );
}

function PluginCard({ item, onOpen }: { item: MarketplaceItem; onOpen: () => void }) {
  const { t } = useTranslation();
  const Icon = iconByName(item.icon);

  return (
    <button
      type="button"
      onClick={onOpen}
      className={cn(
        'flex h-full w-full flex-col gap-2 rounded-lg border bg-card p-4 text-left transition-colors',
        'hover:border-primary/40 hover:bg-accent/30',
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
      )}
    >
      <div className="flex items-start gap-3">
        <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-accent text-accent-foreground">
          <Icon className="h-4.5 w-4.5" aria-hidden />
        </span>
        <span className="min-w-0 flex-1">
          <span className="block truncate font-medium">{item.name}</span>
          <span className="block text-xs text-muted-foreground">{item.category}</span>
        </span>
        {item.installed ? (
          <Badge tone="success" className="shrink-0">
            <Check className="h-3 w-3" aria-hidden />
            {item.enabledCount > 0
              ? t('marketplace.added', { count: item.enabledCount })
              : t('marketplace.addedDisabled')}
          </Badge>
        ) : null}
      </div>
      <p className="line-clamp-2 text-sm text-muted-foreground">{item.summary}</p>
    </button>
  );
}

function PluginDetail({ item }: { item: MarketplaceItem }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const Icon = iconByName(item.icon);

  return (
    <>
      <SheetHeader>
        <div className="flex items-center gap-2.5">
          <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-accent text-accent-foreground">
            <Icon className="h-4.5 w-4.5" aria-hidden />
          </span>
          <div className="min-w-0">
            <SheetTitle>{item.name}</SheetTitle>
            <p className="text-xs text-muted-foreground">
              {item.category} · {t('marketplace.version', { version: item.version })}
            </p>
          </div>
        </div>
      </SheetHeader>

      <SheetBody className="space-y-5 text-sm">
        <p className="text-muted-foreground">{item.description}</p>

        {/*
          What it will ask for, before it is added.
          This is the half of the decision that used to be invisible: a plugin's permissions
          only showed up later, in the roles matrix, next to everything else's.
        */}
        <section className="space-y-2">
          <h3 className="flex items-center gap-1.5 font-medium">
            <ShieldCheck className="h-4 w-4 text-muted-foreground" aria-hidden />
            {t('marketplace.permissionsTitle')}
            <InfoHint label={t('marketplace.permissionsTitle')}>
              {t('marketplace.permissionsHint')}
            </InfoHint>
          </h3>
          {item.permissions.length === 0 ? (
            <p className="text-muted-foreground">{t('marketplace.noPermissions')}</p>
          ) : (
            <ul className="space-y-1">
              {item.permissions.map((permission) => (
                <li key={permission.key} className="flex flex-wrap items-baseline gap-x-2">
                  <span>{permission.displayName}</span>
                  <code className="rounded bg-muted px-1.5 py-0.5 text-xs text-muted-foreground">
                    {permission.key}
                  </code>
                </li>
              ))}
            </ul>
          )}
        </section>

        {item.contentTypes.length > 0 ? (
          <section className="space-y-2">
            <h3 className="flex items-center gap-1.5 font-medium">
              <Package className="h-4 w-4 text-muted-foreground" aria-hidden />
              {t('marketplace.contentTypesTitle')}
            </h3>
            <div className="flex flex-wrap gap-1">
              {item.contentTypes.map((name) => (
                <Badge key={name} tone="secondary">{name}</Badge>
              ))}
            </div>
          </section>
        ) : null}

        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-muted-foreground">
          <dt>{t('marketplace.instances')}</dt>
          <dd className="text-foreground">
            {item.allowMultiple
              ? t('marketplace.multipleAllowed')
              : t('marketplace.singleInstance')}
          </dd>
          <dt>{t('marketplace.menuEntry')}</dt>
          <dd className="text-foreground">
            {item.addsNavEntry ? t('marketplace.addsMenuEntry') : t('marketplace.noMenuEntry')}
          </dd>
          {item.dependencies.length > 0 ? (
            <>
              <dt>{t('marketplace.requires')}</dt>
              <dd className="text-foreground">
                {item.dependencies
                  .map((d) => (d.optional ? `${d.pluginId} (${t('marketplace.optional')})` : d.pluginId))
                  .join(', ')}
              </dd>
            </>
          ) : null}
        </dl>
      </SheetBody>

      <SheetFooter>
        {/*
          Adding an instance is the plugins page's job — it owns the slug, the name and the
          JSON-Schema config form, and duplicating that here would be a second write path onto
          the same rows. The shopfront's job ends at "this is the one I want".
        */}
        <Button
          onClick={() =>
            void navigate({ to: '/plugins' as string, search: { add: item.id } as never })
          }
        >
          <Plus className="h-4 w-4" aria-hidden />
          {item.installed && !item.allowMultiple ? t('marketplace.manage') : t('marketplace.add')}
        </Button>
      </SheetFooter>
    </>
  );
}
