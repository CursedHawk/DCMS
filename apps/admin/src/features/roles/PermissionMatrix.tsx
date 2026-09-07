import { Link } from '@tanstack/react-router';
import { ExternalLink, Search } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Checkbox, Input } from '@dcms/ui';
import { type PermissionDef, groupPermissions } from '../rbac/api';

/**
 * The permission picker for a role.
 *
 * Two things it has to answer that a flat checkbox list could not: *what does
 * this actually affect* (each entry names the plugin instances or the site it
 * covers, and links to it), and *which of these matter here* — a plugin that
 * ships in the binary but has no enabled instance contributes permissions that
 * grant access to nothing, and those are folded away by default rather than
 * padding the list with noise.
 */
export function PermissionMatrix({
  catalog,
  selected,
  onToggle,
  onSetMany,
}: {
  catalog: PermissionDef[];
  selected: Set<string>;
  onToggle: (key: string, on: boolean) => void;
  /** Bulk set, for the per-group "all / none" controls. */
  onSetMany: (keys: string[], on: boolean) => void;
}) {
  const { t } = useTranslation();
  const [query, setQuery] = useState('');
  const [showUnused, setShowUnused] = useState(false);

  const visible = useMemo(() => {
    const q = query.trim().toLowerCase();
    return catalog.filter((p) => {
      // A permission the role already holds is always shown, even if its feature
      // is gone — otherwise it could never be revoked from this screen.
      if (!p.feature.inUse && !showUnused && !selected.has(p.key)) return false;
      if (!q) return true;
      return (
        p.displayName.toLowerCase().includes(q) ||
        p.key.toLowerCase().includes(q) ||
        p.group.toLowerCase().includes(q) ||
        p.feature.name.toLowerCase().includes(q) ||
        p.feature.instances.some((i) => i.name.toLowerCase().includes(q))
      );
    });
  }, [catalog, query, showUnused, selected]);

  const groups = groupPermissions(visible);
  const unusedCount = catalog.filter((p) => !p.feature.inUse && !selected.has(p.key)).length;

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <div className="relative min-w-0 flex-1">
          <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t('roles.searchPermissions')}
            className="h-9 pl-8"
          />
        </div>
        <span className="text-xs text-muted-foreground">
          {t('roles.selectedCount', { count: selected.size })}
        </span>
        {unusedCount > 0 ? (
          <button
            type="button"
            className="text-xs text-muted-foreground underline-offset-2 hover:underline"
            onClick={() => setShowUnused((v) => !v)}
          >
            {showUnused
              ? t('roles.hideUnused')
              : t('roles.showUnused', { count: unusedCount })}
          </button>
        ) : null}
      </div>

      <div className="max-h-[55vh] space-y-4 overflow-y-auto pr-1">
        {groups.map(([group, perms]) => {
          const keys = perms.map((p) => p.key);
          const allOn = keys.every((k) => selected.has(k));
          return (
            <div key={group}>
              <div className="mb-2 flex items-center gap-2">
                <p className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                  {group}
                </p>
                <div className="flex-1" />
                <button
                  type="button"
                  className="text-xs text-muted-foreground underline-offset-2 hover:underline"
                  onClick={() => onSetMany(keys, !allOn)}
                >
                  {allOn ? t('roles.selectNone') : t('roles.selectAll')}
                </button>
              </div>
              <div className="grid grid-cols-1 gap-1.5 sm:grid-cols-2">
                {perms.map((p) => (
                  <label
                    key={p.key}
                    className="flex cursor-pointer items-start gap-2 rounded-md border px-3 py-2 text-sm hover:bg-accent/50"
                  >
                    <Checkbox
                      className="mt-0.5"
                      checked={selected.has(p.key)}
                      onCheckedChange={(v) => onToggle(p.key, v === true)}
                    />
                    <span className="min-w-0 flex-1">
                      <span className="block">{p.displayName}</span>
                      <FeatureLine feature={p.feature} />
                    </span>
                  </label>
                ))}
              </div>
            </div>
          );
        })}
        {groups.length === 0 ? (
          <p className="text-sm text-muted-foreground">{t('common.noResults')}</p>
        ) : null}
      </div>
    </div>
  );
}

/** The "what this affects" caption under a permission. */
function FeatureLine({ feature }: { feature: PermissionDef['feature'] }) {
  const { t } = useTranslation();

  if (!feature.inUse) {
    return (
      <span className="mt-0.5 block text-[11px] text-muted-foreground">
        <Badge tone="outline" className="text-[10px]">
          {t('roles.notInUse')}
        </Badge>
      </span>
    );
  }

  // Plugin permissions are only meaningful through the instances the tenant has
  // enabled, so name them; a bare plugin name would not say which gallery.
  const detail =
    feature.kind === 'plugin' && feature.instances.length > 0
      ? feature.instances.map((i) => i.name).join(', ')
      : feature.kind === 'platform'
        ? null
        : feature.name;

  if (!detail && !feature.route) return null;

  return (
    <span className="mt-0.5 flex flex-wrap items-center gap-1 text-[11px] text-muted-foreground">
      {detail ? <span className="truncate">{detail}</span> : null}
      {feature.route ? (
        <Link
          to={feature.route}
          className="inline-flex items-center gap-0.5 underline-offset-2 hover:underline"
          // The matrix lives inside a <label>; without this the click toggles the
          // checkbox on the way to the link.
          onClick={(e) => e.stopPropagation()}
        >
          {t('roles.openFeature')}
          <ExternalLink className="h-3 w-3" />
        </Link>
      ) : null}
    </span>
  );
}
