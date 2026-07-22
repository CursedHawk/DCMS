import { ArrowDown, ArrowUp, UserPlus, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '../../components/ui/select';
import { Switch } from '../../components/ui/switch';
import { usePluginInstances } from '../plugins/api';
import { useContentItemsOfType } from './api';

/**
 * One entry of a gig's line-up. `performerName` is always written, so a line-up
 * reads on its own; `memberSlug` is set only for performers picked from the
 * linked Roster instance, and is what makes the two sides point at each other.
 */
export interface Performer {
  memberSlug: string | null;
  performerName: string | null;
  performerRole: string;
  flyerOrder: number;
  visible: boolean;
}

/** Coerce the stored value (array, JSON string, or empty) into a line-up. */
function toPerformers(value: unknown): Performer[] {
  const raw =
    Array.isArray(value)
      ? value
      : typeof value === 'string' && value.trim().startsWith('[')
        ? (() => {
            try {
              const parsed = JSON.parse(value);
              return Array.isArray(parsed) ? parsed : [];
            } catch {
              return [];
            }
          })()
        : [];

  return raw
    .filter((p): p is Record<string, unknown> => !!p && typeof p === 'object')
    .map((p, i) => ({
      memberSlug: typeof p.memberSlug === 'string' ? p.memberSlug : null,
      performerName: typeof p.performerName === 'string' ? p.performerName : null,
      performerRole: typeof p.performerRole === 'string' ? p.performerRole : '',
      flyerOrder: typeof p.flyerOrder === 'number' ? p.flyerOrder : i,
      visible: p.visible !== false,
    }))
    .sort((a, b) => a.flyerOrder - b.flyerOrder);
}

/** flyerOrder is the list position, so it is rewritten on every change. */
function renumber(list: Performer[]): Performer[] {
  return list.map((p, i) => ({ ...p, flyerOrder: i }));
}

/**
 * Edits a gig's line-up. Performers are picked from the linked Roster instance;
 * guests who are not on it — and every performer when no roster is linked at all
 * — are added by name.
 */
export function PerformerPicker({
  rosterSlug,
  contentType,
  value,
  onChange,
}: {
  /** Roster instance the events instance points at, or null if it points at none. */
  rosterSlug: string | null;
  contentType: string;
  value: unknown;
  onChange: (v: Performer[]) => void;
}) {
  const { t } = useTranslation();
  const instances = usePluginInstances();
  const roster = instances.data?.find((i) => i.slug === rosterSlug && i.pluginId === 'roster');
  const crew = useContentItemsOfType(roster?.id, contentType);

  const performers = toPerformers(value);
  const crewMembers = (crew.data ?? []).map((item) => ({
    slug: item.slug,
    name: (item.draft?.name as string | undefined)?.trim() || item.slug,
    role: (item.draft?.role as string | undefined) ?? '',
  }));
  const taken = new Set(performers.map((p) => p.memberSlug).filter(Boolean));
  const available = crewMembers.filter((m) => !taken.has(m.slug));

  const update = (i: number, patch: Partial<Performer>) =>
    onChange(renumber(performers.map((p, j) => (j === i ? { ...p, ...patch } : p))));
  const remove = (i: number) => onChange(renumber(performers.filter((_, j) => j !== i)));
  const move = (i: number, dir: -1 | 1) => {
    const j = i + dir;
    if (j < 0 || j >= performers.length) return;
    const next = performers.slice();
    [next[i], next[j]] = [next[j], next[i]];
    onChange(renumber(next));
  };

  const addCrewMember = (slug: string) => {
    const member = crewMembers.find((m) => m.slug === slug);
    if (!member) return;
    onChange(
      renumber([
        ...performers,
        {
          memberSlug: member.slug,
          performerName: member.name,
          performerRole: member.role,
          flyerOrder: performers.length,
          visible: true,
        },
      ]),
    );
  };

  const addGuest = () =>
    onChange(
      renumber([
        ...performers,
        {
          memberSlug: null,
          performerName: '',
          performerRole: '',
          flyerOrder: performers.length,
          visible: true,
        },
      ]),
    );

  return (
    <div className="space-y-2">
      {performers.length === 0 ? (
        <p className="text-xs text-muted-foreground">{t('content.performers.empty')}</p>
      ) : (
        <ul className="space-y-2">
          {performers.map((p, i) => (
            <li
              key={p.memberSlug ?? `guest-${i}`}
              className="flex flex-wrap items-center gap-2 rounded-md border p-2"
            >
              <div className="flex flex-col">
                <button
                  type="button"
                  aria-label="move up"
                  onClick={() => move(i, -1)}
                  disabled={i === 0}
                  className="disabled:opacity-30"
                >
                  <ArrowUp className="h-3.5 w-3.5" />
                </button>
                <button
                  type="button"
                  aria-label="move down"
                  onClick={() => move(i, 1)}
                  disabled={i === performers.length - 1}
                  className="disabled:opacity-30"
                >
                  <ArrowDown className="h-3.5 w-3.5" />
                </button>
              </div>

              {p.memberSlug ? (
                <div className="flex min-w-40 flex-1 items-center gap-2">
                  <span className="text-sm font-medium">{p.performerName ?? p.memberSlug}</span>
                  <Badge tone="secondary">{p.memberSlug}</Badge>
                </div>
              ) : (
                <>
                  <Input
                    className="min-w-40 flex-1"
                    value={p.performerName ?? ''}
                    placeholder={t('content.performers.guestName')}
                    onChange={(e) => update(i, { performerName: e.target.value })}
                  />
                  <Badge tone="secondary">{t('content.performers.guest')}</Badge>
                </>
              )}

              <Input
                className="w-40"
                value={p.performerRole}
                placeholder={t('content.performers.role')}
                onChange={(e) => update(i, { performerRole: e.target.value })}
              />

              <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
                <Switch checked={p.visible} onCheckedChange={(v) => update(i, { visible: v })} />
                {t('content.performers.visible')}
              </label>

              <button type="button" aria-label="remove" onClick={() => remove(i)}>
                <X className="h-4 w-4" />
              </button>
            </li>
          ))}
        </ul>
      )}

      <div className="flex flex-wrap items-center gap-2">
        {/* Value stays empty: picking appends a row rather than selecting one. */}
        <Select value="" onValueChange={addCrewMember} disabled={available.length === 0}>
          <SelectTrigger className="w-56">
            <SelectValue
              placeholder={
                !roster
                  ? t('content.performers.noRoster')
                  : crew.isLoading
                    ? t('common.loading')
                    : available.length === 0
                      ? t('content.performers.noneAvailable')
                      : t('content.performers.addCrewMember')
              }
            />
          </SelectTrigger>
          <SelectContent>
            {available.map((m) => (
              <SelectItem key={m.slug} value={m.slug}>
                {m.role ? `${m.name} — ${m.role}` : m.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>

        <Button type="button" variant="outline" size="sm" onClick={addGuest}>
          <UserPlus className="h-4 w-4" />
          {t('content.performers.addGuest')}
        </Button>
      </div>
    </div>
  );
}
