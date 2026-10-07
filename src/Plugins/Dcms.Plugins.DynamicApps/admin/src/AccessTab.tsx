import { useState } from 'react';
import { Badge, Button, CopyButton } from '@dcms/ui';
import { usePluginHost, usePluginT } from '@dcms/plugin-ui';
import { useApplyChanges, usePublicModel, type AppConfig, type PublicAccess, type TableDef } from './api';
import { CheckField, SelectField } from './model/dialogs';

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
            <TableAccess key={`${table.id}:${JSON.stringify(table.public)}`} slug={slug} table={table} editable={editable} />
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

function TableAccess({ slug, table, editable }: { slug: string; table: TableDef; editable: boolean }) {
  const { t } = usePluginT();
  const apply = useApplyChanges(slug);
  const [access, setAccess] = useState<PublicAccess>(table.public);
  const dirty = JSON.stringify(access) !== JSON.stringify(table.public);
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
    </div>
  );
}
