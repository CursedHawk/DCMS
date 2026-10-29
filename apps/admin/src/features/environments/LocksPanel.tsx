import { Lock, LockOpen, Plus, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Badge,
  Button,
  Card,
  CardContent,
  Input,
  LoadFailed,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  SectionHeader,
  cn,
  toastApiError,
} from '@dcms/ui';
import { CONFIG_KINDS, POLICY_KINDS, usePolicies, useSavePolicies, type Ownership, type Policy } from './api';

const MODES: readonly Ownership[] = ['Mixed', 'Local', 'Managed'];

/**
 * Who changes each kind of configuration in this environment: here and by deployments, here only,
 * or deployments only (locked). Item exceptions override the kind for one item -- production's
 * legal page kept its own while the rest of its content comes from the pipeline.
 */
export function LocksPanel({ environmentId }: { environmentId: string }) {
  const { t } = useTranslation();
  const policies = usePolicies(environmentId);
  const save = useSavePolicies(environmentId);
  const list = policies.data ?? [];
  const write = (next: Policy[]) => save.mutate(next, { onError: (err) => toastApiError(err, t) });
  const modeOf = (kind: string): Ownership => list.find((p) => p.kind === kind && p.itemKey == null)?.mode ?? 'Mixed';
  const setKind = (kind: string, mode: Ownership) =>
    write([...list.filter((p) => !(p.kind === kind && p.itemKey == null)), { kind, itemKey: null, mode }]);
  const preset = (mode: Ownership) =>
    write([...list.filter((p) => p.itemKey != null), ...CONFIG_KINDS.map((kind): Policy => ({ kind, itemKey: null, mode }))]);
  const exceptions = list.filter((p) => p.itemKey != null);
  const [kind, setKindDraft] = useState<string>(POLICY_KINDS[0]);
  const [key, setKey] = useState('');
  const [mode, setMode] = useState<Ownership>('Local');

  if (policies.isError) return <LoadFailed onRetry={() => void policies.refetch()} />;
  const busy = !policies.data || save.isPending;
  return (
    <div className="flex flex-col gap-6">
      <section className="flex flex-col gap-3">
        <SectionHeader title={t('environments.locks.title')} description={t('environments.locks.description')} />
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" size="sm" disabled={busy} onClick={() => preset('Managed')}>
            <Lock className="h-4 w-4" /> {t('environments.lockToPipeline')}
          </Button>
          <Button variant="outline" size="sm" disabled={busy} onClick={() => preset('Mixed')}>
            <LockOpen className="h-4 w-4" /> {t('environments.locks.unlockAll')}
          </Button>
        </div>
        <Card>
          <CardContent className="divide-y p-0">
            {POLICY_KINDS.map((k) => (
              <div key={k} className="flex flex-col gap-2 p-3 sm:flex-row sm:items-center sm:gap-4">
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-medium">{t(`deployments.kinds.${k}`)}</p>
                  <p className="text-xs text-muted-foreground">{t(`environments.locks.kinds.${k}`)}</p>
                </div>
                <div role="radiogroup" aria-label={t('environments.ownershipOf', { kind: t(`deployments.kinds.${k}`) })} className="grid w-full shrink-0 grid-cols-3 rounded-lg bg-muted p-1 sm:inline-flex sm:w-auto">
                  {MODES.map((m) => {
                    const on = modeOf(k) === m;
                    return (
                      <button
                        key={m}
                        type="button"
                        role="radio"
                        aria-checked={on}
                        disabled={busy}
                        onClick={() => !on && setKind(k, m)}
                        className={cn(
                          'flex items-center justify-center gap-1 rounded-md px-2 py-1 text-center text-xs font-medium',
                          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                          on ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground',
                        )}
                      >
                        {m === 'Managed' ? <Lock className="h-3 w-3" aria-hidden /> : null}
                        {t(`environments.modes.${m}`)}
                      </button>
                    );
                  })}
                </div>
              </div>
            ))}
          </CardContent>
        </Card>
      </section>

      <section className="flex flex-col gap-3">
        <SectionHeader title={t('environments.exceptions')} description={t('environments.locks.exceptionsHint')} />
        {exceptions.length > 0 ? (
          <Card>
            <CardContent className="divide-y p-0">
              {exceptions.map((p) => (
                <div key={`${p.kind}/${p.itemKey}`} className="flex flex-wrap items-center gap-2 p-3 text-sm">
                  <Badge tone="outline">{t(`deployments.kinds.${p.kind}`, { defaultValue: p.kind })}</Badge>
                  <span className="min-w-0 font-mono text-xs [overflow-wrap:anywhere]">{p.itemKey}</span>
                  <span className="text-muted-foreground">{t(`environments.modes.${p.mode}`)}</span>
                  <Button
                    variant="ghost"
                    size="icon"
                    className="ml-auto"
                    disabled={busy}
                    aria-label={t('environments.removeException', { item: p.itemKey })}
                    onClick={() => write(list.filter((x) => !(x.kind === p.kind && x.itemKey === p.itemKey)))}
                  >
                    <Trash2 className="h-4 w-4" />
                  </Button>
                </div>
              ))}
            </CardContent>
          </Card>
        ) : null}
        <form
          className="flex flex-col gap-2 sm:flex-row sm:items-center"
          onSubmit={(e) => {
            e.preventDefault();
            const item = key.trim();
            write([...list.filter((p) => !(p.kind === kind && p.itemKey === item)), { kind, itemKey: item, mode }]);
            setKey('');
          }}
        >
          <Select value={kind} onValueChange={setKindDraft}>
            <SelectTrigger className="sm:w-44" aria-label={t('environments.exceptionKind')}>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {POLICY_KINDS.map((k) => (
                <SelectItem key={k} value={k}>
                  {t(`deployments.kinds.${k}`)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Input
            aria-label={t('environments.exceptionKey')}
            placeholder={t(`environments.locks.keyHints.${kind}`, { defaultValue: t('environments.exceptionKeyHint') })}
            value={key}
            onChange={(e) => setKey(e.target.value)}
          />
          <Select value={mode} onValueChange={(m) => setMode(m as Ownership)}>
            <SelectTrigger className="sm:w-44" aria-label={t('environments.exceptionMode')}>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {MODES.map((m) => (
                <SelectItem key={m} value={m}>
                  {t(`environments.modes.${m}`)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Button type="submit" variant="outline" disabled={!key.trim() || busy}>
            <Plus className="h-4 w-4" /> {t('environments.addException')}
          </Button>
        </form>
      </section>
    </div>
  );
}
