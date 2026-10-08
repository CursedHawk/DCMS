import { useState } from 'react';
import { Badge, Button, CopyButton } from '@dcms/ui';
import { usePluginHost, usePluginT } from '@dcms/plugin-ui';
import { useApplyChanges, usePublicModel, type AppConfig, type PublicAccess, type RowRule, type TableDef } from './api';
import { CheckField, SelectField, TextField } from './model/dialogs';

/**
 * What the public site may do with each table, and the API that results. Access is part of the
 * configuration (it goes live with a publish, like everything else); the API below is what is
 * live now.
 */
export function AccessTab({ slug, config, editable, published }: { slug: string; config: AppConfig | null; editable: boolean; published: boolean }) {
  const { t } = usePluginT();
  const host = usePluginHost();
  const live = usePublicModel(slug, published);
  const base = `${host.contentApiBase.replace(/\/$/, '')}/api/${slug}`;

  return (
    <div className="space-y-6 pt-2">
      <section className="space-y-2">
        <h3 className="text-sm font-semibold">{t('access.title')}</h3>
        <p className="text-sm text-muted-foreground">{t('access.hint')}</p>
        <div className="divide-y rounded-md border">
          {(config?.tables ?? []).map((table) => (
            <TableAccess key={`${table.id}:${JSON.stringify(table.public)}`} slug={slug} table={table} config={config!} editable={editable} />
          ))}
        </div>
      </section>

      <section className="space-y-2">
        <h3 className="text-sm font-semibold">{t('access.apiTitle')}</h3>
        {!published ? <p className="text-sm text-muted-foreground">{t('access.notPublished')}</p> : live.data ? (
          <>
            <p className="text-sm text-muted-foreground">{t('access.apiHint', { revision: live.data.revision })}</p>
            {live.data.tables.length === 0 ? <p className="text-sm text-muted-foreground">{t('access.nothingPublic')}</p> : (
              <ul className="space-y-2 text-sm">
                {live.data.tables.map((x) => (
                  <li key={x.apiName} className="rounded-md border p-2">
                    <div className="flex items-center gap-2">
                      <code className="truncate">{`${base}/data/${x.apiName}`}</code>
                      <CopyButton value={`${base}/data/${x.apiName}`} />
                    </div>
                    <div className="mt-1 flex flex-wrap gap-1">
                      {x.access.read !== 'none' ? <Badge tone="secondary">{t(`access.read.${x.access.read}`)}</Badge> : null}
                      {x.access.create ? <Badge tone="outline">{t('access.create')}</Badge> : null}
                      {x.access.updateOwn ? <Badge tone="outline">{t('access.updateOwn')}</Badge> : null}
                      {x.access.deleteOwn ? <Badge tone="outline">{t('access.deleteOwn')}</Badge> : null}
                      {x.access.rowRules ? <Badge tone="outline">{t('access.rules.badge')}</Badge> : null}
                    </div>
                    <p className="mt-1 text-xs text-muted-foreground">{x.fields.map((f) => `${f.apiName}:${f.type}`).join(' · ')}</p>
                  </li>
                ))}
              </ul>
            )}
            <a className="text-sm underline" href={`${host.contentApiBase.replace(/\/$/, '')}/api/openapi`} target="_blank" rel="noreferrer">
              {t('access.openapi')}
            </a>
          </>
        ) : null}
      </section>
    </div>
  );
}

function TableAccess({ slug, table, config, editable }: { slug: string; table: TableDef; config: AppConfig; editable: boolean }) {
  const { t } = usePluginT();
  const apply = useApplyChanges(slug);
  const [access, setAccess] = useState<PublicAccess>({ ...table.public, rules: table.public.rules ?? [] });
  const dirty = JSON.stringify(access) !== JSON.stringify({ ...table.public, rules: table.public.rules ?? [] });
  const rules = access.rules ?? [];
  const setRules = (next: RowRule[]) => setAccess((a) => ({ ...a, rules: next }));
  const set = (key: keyof PublicAccess) => (value: boolean) => setAccess((a) => ({ ...a, [key]: value }));
  return (
    <div className="grid items-end gap-3 p-3 sm:grid-cols-[12rem_10rem_1fr_auto]">
      <div>
        <div className="font-medium">{table.displayName}</div>
        <div className="font-mono text-xs text-muted-foreground">{table.apiName}</div>
      </div>
      <SelectField id={`read-${table.id}`} label={t('access.readLabel')} value={access.read}
        onChange={(read) => setAccess((a) => ({ ...a, read }))}
        options={(['none', 'all', 'own'] as const).map((value) => ({ value, label: t(`access.read.${value}`) }))} />
      <div className="flex flex-wrap gap-4 pb-2">
        <CheckField id={`create-${table.id}`} label={t('access.create')} checked={access.create} onChange={set('create')} />
        <CheckField id={`update-${table.id}`} label={t('access.updateOwn')} checked={access.updateOwn} onChange={set('updateOwn')} />
        <CheckField id={`delete-${table.id}`} label={t('access.deleteOwn')} checked={access.deleteOwn} onChange={set('deleteOwn')} />
      </div>
      {editable ? (
        <Button size="sm" disabled={!dirty || apply.isPending}
          onClick={() => apply.mutate({ operations: [{ op: 'update', type: 'table', target: table.id, value: { public: access } }] })}>
          {t('actions.save')}
        </Button>
      ) : <span />}
      <div className="space-y-2 sm:col-span-4">
        <div className="text-sm font-medium">{t('access.rules.title')}</div>
        <p className="text-xs text-muted-foreground">{t('access.rules.hint')}</p>
        {rules.map((rule, i) => (
          <RuleEditor key={i} id={`${table.id}-${i}`} table={table} config={config} rule={rule}
            onChange={(r) => setRules(rules.map((x, j) => (j === i ? r : x)))}
            onRemove={() => setRules(rules.filter((_, j) => j !== i))} />
        ))}
        {editable && rules.length < MAX_RULES ? (
          <Button size="sm" variant="outline"
            onClick={() => setRules([...rules, { path: [], field: '', matches: 'user.email', read: true, update: false, delete: false }])}>
            {t('access.rules.add')}
          </Button>
        ) : null}
      </div>
    </div>
  );
}

const MAX_RULES = 10;
const MAX_PATH = 3;
const RULE_FIELD_TYPES = ['text', 'email', 'choice', 'multiChoice'];
const STOP = '-';
const ATTRIBUTE = 'user.attribute.';

/** The relationships a table's records can be followed through, by the name used from this side. */
function navigations(config: AppConfig, tableId: string): { name: string; tableId: string }[] {
  return config.relationships.flatMap((r) => [
    ...(r.sourceTableId === tableId ? [{ name: r.apiName, tableId: r.targetTableId }] : []),
    ...(r.targetTableId === tableId && r.inverseApiName ? [{ name: r.inverseApiName, tableId: r.sourceTableId }] : []),
  ]);
}

/** One row rule: a path of relationships from the record, a field there, and what of the signed-in user it must hold. */
function RuleEditor({ id, table, config, rule, onChange, onRemove }: {
  id: string; table: TableDef; config: AppConfig; rule: RowRule; onChange: (rule: RowRule) => void; onRemove: () => void;
}) {
  const { t } = usePluginT();
  // The table reached after each step; a step that no longer resolves ends the path there.
  const reached: TableDef[] = [table];
  for (const step of rule.path) {
    const next = navigations(config, reached[reached.length - 1].id).find((n) => n.name === step);
    const nextTable = next && config.tables.find((x) => x.id === next.tableId);
    if (!nextTable) break;
    reached.push(nextTable);
  }
  const end = reached[reached.length - 1];
  const steps = rule.path.slice(0, reached.length - 1);
  const subject = rule.matches.startsWith(ATTRIBUTE) ? ATTRIBUTE : rule.matches;
  const setStep = (i: number, name: string) =>
    onChange({ ...rule, path: name === STOP ? steps.slice(0, i) : [...steps.slice(0, i), name], field: '' });

  return (
    <div className="space-y-2 rounded-md border p-2">
      <div className="flex flex-wrap items-end gap-2">
        <div className="text-xs text-muted-foreground pb-2">{table.apiName}</div>
        {[...steps, ...(steps.length < MAX_PATH ? [STOP] : [])].map((step, i) => (
          <div key={i} className="w-40">
            <SelectField id={`${id}-step-${i}`} label={t('access.rules.through')} value={step}
              onChange={(name) => setStep(i, name)}
              options={[{ value: STOP, label: t('access.rules.here') },
                ...navigations(config, reached[i].id).map((n) => ({ value: n.name, label: `${n.name} → ${config.tables.find((x) => x.id === n.tableId)?.apiName ?? '?'}` }))]} />
          </div>
        ))}
        <div className="w-40">
          <SelectField id={`${id}-field`} label={t('access.rules.field', { table: end.apiName })} value={rule.field}
            onChange={(field) => onChange({ ...rule, field })}
            options={end.fields.filter((f) => RULE_FIELD_TYPES.includes(f.type)).map((f) => ({ value: f.apiName, label: f.apiName }))} />
        </div>
        <div className="w-44">
          <SelectField id={`${id}-matches`} label={t('access.rules.matches')} value={subject}
            onChange={(m) => onChange({ ...rule, matches: m === ATTRIBUTE ? ATTRIBUTE : m })}
            options={(['user.id', 'user.email', 'user.groups', ATTRIBUTE] as const).map((m) => ({ value: m, label: t(`access.rules.subject.${m.replace(/\./g, '_').replace(/_$/, '')}`) }))} />
        </div>
        {subject === ATTRIBUTE ? (
          <div className="w-36">
            <TextField id={`${id}-attribute`} label={t('access.rules.attribute')} mono value={rule.matches.slice(ATTRIBUTE.length)}
              onChange={(key) => onChange({ ...rule, matches: ATTRIBUTE + key })} />
          </div>
        ) : null}
      </div>
      <div className="flex flex-wrap items-center gap-4">
        <CheckField id={`${id}-read`} label={t('access.rules.read')} checked={rule.read} onChange={(read) => onChange({ ...rule, read })} />
        <CheckField id={`${id}-update`} label={t('access.rules.update')} checked={rule.update} onChange={(update) => onChange({ ...rule, update })} />
        <CheckField id={`${id}-delete`} label={t('access.rules.delete')} checked={rule.delete} onChange={(del) => onChange({ ...rule, delete: del })} />
        <Button size="sm" variant="ghost" onClick={onRemove}>{t('access.rules.remove')}</Button>
      </div>
    </div>
  );
}
