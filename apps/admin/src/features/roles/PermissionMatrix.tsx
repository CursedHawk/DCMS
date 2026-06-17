import { useTranslation } from 'react-i18next';
import { Checkbox } from '../../components/ui/checkbox';
import { type PermissionDef, groupPermissions } from '../rbac/api';

export function PermissionMatrix({
  catalog,
  selected,
  onToggle,
}: {
  catalog: PermissionDef[];
  selected: Set<string>;
  onToggle: (key: string, on: boolean) => void;
}) {
  const { t } = useTranslation();
  const groups = groupPermissions(catalog);

  return (
    <div className="max-h-[55vh] space-y-4 overflow-y-auto pr-1">
      {groups.map(([group, perms]) => (
        <div key={group}>
          <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
            {group}
          </p>
          <div className="grid grid-cols-1 gap-1.5 sm:grid-cols-2">
            {perms.map((p) => (
              <label
                key={p.key}
                className="flex cursor-pointer items-center gap-2 rounded-md border px-3 py-2 text-sm hover:bg-accent/50"
              >
                <Checkbox
                  checked={selected.has(p.key)}
                  onCheckedChange={(v) => onToggle(p.key, v === true)}
                />
                <span>{p.displayName}</span>
              </label>
            ))}
          </div>
        </div>
      ))}
      {groups.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t('common.noResults')}</p>
      ) : null}
    </div>
  );
}
