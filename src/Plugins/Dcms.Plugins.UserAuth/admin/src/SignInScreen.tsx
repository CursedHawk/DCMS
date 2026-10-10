import { AlertTriangle, KeyRound, Plus, Trash2 } from 'lucide-react';
import { useState } from 'react';
import {
  Badge,
  Button,
  Card,
  CopyButton,
  Dialog,
  DialogBody,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Switch,
  TagsInput,
} from '@dcms/ui';
import { useCan, usePluginApi, usePluginT } from '@dcms/plugin-ui';
import { type Provider, type ProviderKind, type ProviderWrite, useGroups, usePath, useProviders, useRealm, useWrite } from './api';
import { GroupChecklist } from './UsersScreen';

const KINDS: ProviderKind[] = ['google', 'entra', 'oidc', 'dcms'];

/**
 * How people sign in to the tenant's sites (manifest screen "sign-in"): the realm's own
 * passwords and the identity providers it trusts. Client secrets are write-only: identity keeps
 * them encrypted and never sends one back.
 */
export function SignInScreen() {
  const { t } = usePluginT();
  const api = usePluginApi();
  const path = usePath();
  const canEdit = useCan('providers-manage');
  const realm = useRealm();
  const providers = useProviders();
  const [editing, setEditing] = useState<Provider | 'new' | null>(null);
  const setPasswords = useWrite((passwordEnabled: boolean) => api.put(path('/realm'), { passwordEnabled }));
  const remove = useWrite((p: Provider) => api.del(path(`/providers/${encodeURIComponent(p.key)}`)));

  return (
    <div className="space-y-4">
      {realm.data && !realm.data.clientReady ? (
        <div className="flex items-start gap-2 rounded-md border border-[hsl(var(--warning)/0.4)] bg-[hsl(var(--warning)/0.08)] p-3 text-sm">
          <AlertTriangle className="mt-0.5 size-4 shrink-0 text-[hsl(var(--warning))]" aria-hidden />
          {t('signIn.notReady')}
        </div>
      ) : null}
      <Card className="space-y-3 p-4">
        <div className="flex items-center justify-between gap-3">
          <div>
            <div className="font-medium">{t('signIn.passwords')}</div>
            <p className="text-sm text-muted-foreground">{t('signIn.passwordsHint')}</p>
          </div>
          <Switch
            checked={realm.data?.passwordEnabled ?? false}
            disabled={!canEdit || !realm.data || setPasswords.isPending}
            onCheckedChange={(on) => setPasswords.mutate(on)}
            aria-label={t('signIn.passwords')}
          />
        </div>
        {realm.data ? (
          <div className="text-sm text-muted-foreground">
            {realm.data.hosts.length ? t('signIn.hosts', { hosts: realm.data.hosts.join(', ') }) : t('signIn.noHosts')}
          </div>
        ) : null}
      </Card>
      <div className="flex items-center justify-between gap-2">
        <h2 className="text-base font-semibold">{t('signIn.providers')}</h2>
        {canEdit ? (
          <Button onClick={() => setEditing('new')}>
            <Plus className="size-4" /> {t('signIn.add')}
          </Button>
        ) : null}
      </div>
      {providers.data?.length === 0 ? <p className="text-sm text-muted-foreground">{t('signIn.none')}</p> : null}
      <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
        {providers.data?.map((p) => (
          <Card key={p.key} className="space-y-2 p-4">
            <div className="flex items-start justify-between gap-2">
              <div>
                <div className="flex items-center gap-2 font-medium">
                  {p.displayName}
                  <Badge tone={p.enabled ? 'success' : 'secondary'}>{p.enabled ? t('signIn.on') : t('signIn.off')}</Badge>
                </div>
                <div className="text-xs text-muted-foreground">
                  {t(`signIn.kinds.${p.kind}`)} · {t(`signIn.provisioning.${p.provisioning}`)}
                </div>
              </div>
              {canEdit ? (
                <div className="flex gap-1">
                  <Button variant="ghost" size="sm" onClick={() => setEditing(p)}>
                    {t('signIn.edit')}
                  </Button>
                  <Button
                    variant="ghost"
                    size="icon"
                    aria-label={t('signIn.delete')}
                    onClick={() => {
                      if (window.confirm(t('signIn.confirmDelete', { name: p.displayName }))) remove.mutate(p);
                    }}
                  >
                    <Trash2 className="size-4" />
                  </Button>
                </div>
              ) : null}
            </div>
            {p.callbackUrl ? (
              <div className="space-y-1">
                <div className="text-xs text-muted-foreground">{t('signIn.callback')}</div>
                <div className="flex items-center gap-2 rounded-md border bg-muted/40 p-1.5">
                  <code className="min-w-0 flex-1 truncate text-xs">{p.callbackUrl}</code>
                  <CopyButton value={p.callbackUrl} size="sm" />
                </div>
              </div>
            ) : null}
          </Card>
        ))}
      </div>
      {editing ? <ProviderDialog provider={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
    </div>
  );
}

function ProviderDialog({ provider, onClose }: { provider: Provider | null; onClose: () => void }) {
  const { t } = usePluginT();
  const api = usePluginApi();
  const path = usePath();
  const groups = useGroups();
  const [kind, setKind] = useState<ProviderKind>(provider?.kind ?? 'google');
  const [key, setKey] = useState(provider?.key ?? 'google');
  const [form, setForm] = useState<ProviderWrite>({
    kind: provider?.kind ?? 'google',
    displayName: provider?.displayName ?? t('signIn.kinds.google'),
    enabled: provider?.enabled ?? true,
    clientId: provider?.clientId ?? '',
    clientSecret: '',
    issuer: provider?.issuer ?? '',
    entraTenant: provider?.entraTenant ?? '',
    hostedDomain: provider?.hostedDomain ?? '',
    provisioning: provider?.provisioning ?? 'inviteOnly',
    allowedDomains: provider?.allowedDomains ?? [],
    defaultGroups: provider?.defaultGroups ?? [],
  });
  const set = (patch: Partial<ProviderWrite>) => setForm({ ...form, ...patch });
  const external = kind !== 'dcms';
  const save = useWrite(
    () =>
      api.put(path(`/providers/${encodeURIComponent(key)}`), {
        ...form,
        kind,
        // Blank keeps the stored secret.
        clientSecret: form.clientSecret?.trim() || null,
        clientId: external ? form.clientId?.trim() || null : null,
        issuer: kind === 'oidc' ? form.issuer?.trim() || null : null,
        entraTenant: kind === 'entra' ? form.entraTenant?.trim() || null : null,
        hostedDomain: kind === 'google' ? form.hostedDomain?.trim() || null : null,
      }),
    onClose,
  );

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-w-xl">
        <DialogHeader>
          <DialogTitle>{provider ? t('signIn.editTitle', { name: provider.displayName }) : t('signIn.addTitle')}</DialogTitle>
        </DialogHeader>
        <DialogBody className="max-h-[70vh] space-y-4 overflow-y-auto">
          {provider ? null : (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <div className="space-y-1.5">
                <Label>{t('signIn.kind')}</Label>
                <Select
                  value={kind}
                  onValueChange={(value) => {
                    const next = value as ProviderKind;
                    setKind(next);
                    setKey(next);
                    set({ kind: next, displayName: t(`signIn.kinds.${next}`) });
                  }}
                >
                  <SelectTrigger>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {KINDS.map((k) => (
                      <SelectItem key={k} value={k}>
                        {t(`signIn.kinds.${k}`)}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="ua-provider-key">{t('signIn.key')}</Label>
                <Input id="ua-provider-key" className="font-mono" value={key} onChange={(e) => setKey(e.target.value)} />
              </div>
            </div>
          )}
          <div className="flex items-end gap-3">
            <div className="flex-1 space-y-1.5">
              <Label htmlFor="ua-provider-name">{t('signIn.displayName')}</Label>
              <Input id="ua-provider-name" value={form.displayName} onChange={(e) => set({ displayName: e.target.value })} />
            </div>
            <label className="flex items-center gap-2 pb-2 text-sm">
              <Switch checked={form.enabled} onCheckedChange={(enabled) => set({ enabled })} /> {t('signIn.enabled')}
            </label>
          </div>
          {kind === 'oidc' ? (
            <Field id="ua-provider-issuer" label={t('signIn.issuer')} value={form.issuer ?? ''} onChange={(issuer) => set({ issuer })} placeholder="https://login.example.com" />
          ) : null}
          {kind === 'entra' ? (
            <Field id="ua-provider-entra" label={t('signIn.entraTenant')} hint={t('signIn.entraHint')} value={form.entraTenant ?? ''} onChange={(entraTenant) => set({ entraTenant })} />
          ) : null}
          {kind === 'google' ? (
            <Field id="ua-provider-hd" label={t('signIn.hostedDomain')} hint={t('signIn.hostedDomainHint')} value={form.hostedDomain ?? ''} onChange={(hostedDomain) => set({ hostedDomain })} placeholder="corp.example" />
          ) : null}
          {external ? (
            <>
              <Field id="ua-provider-client" label={t('signIn.clientId')} value={form.clientId ?? ''} onChange={(clientId) => set({ clientId })} />
              <Field
                id="ua-provider-secret"
                type="password"
                label={t('signIn.clientSecret')}
                hint={provider?.hasSecret ? t('signIn.secretKept') : undefined}
                value={form.clientSecret ?? ''}
                onChange={(clientSecret) => set({ clientSecret })}
              />
            </>
          ) : (
            <p className="text-sm text-muted-foreground">{t('signIn.dcmsHint')}</p>
          )}
          <div className="space-y-1.5">
            <Label>{t('signIn.whoGetsIn')}</Label>
            <Select value={form.provisioning} onValueChange={(provisioning) => set({ provisioning })}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="inviteOnly">{t('signIn.provisioning.inviteOnly')}</SelectItem>
                <SelectItem value="allowedDomains">{t('signIn.provisioning.allowedDomains')}</SelectItem>
              </SelectContent>
            </Select>
          </div>
          {form.provisioning === 'allowedDomains' ? (
            <>
              <div className="space-y-1.5">
                <Label>{t('signIn.allowedDomains')}</Label>
                <TagsInput value={form.allowedDomains ?? []} onChange={(allowedDomains) => set({ allowedDomains })} placeholder="corp.example" />
              </div>
              {groups.data?.length ? (
                <fieldset className="space-y-2">
                  <legend className="text-sm font-medium">{t('signIn.defaultGroups')}</legend>
                  <GroupChecklist groups={groups.data} value={form.defaultGroups ?? []} onChange={(defaultGroups) => set({ defaultGroups })} />
                </fieldset>
              ) : null}
            </>
          ) : null}
        </DialogBody>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            {t('common.cancel', { defaultValue: 'Cancel' })}
          </Button>
          <Button disabled={!form.displayName.trim() || !key || save.isPending} onClick={() => save.mutate(undefined)}>
            <KeyRound className="size-4" /> {t('common.save', { defaultValue: 'Save' })}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function Field({
  id,
  label,
  hint,
  value,
  onChange,
  type = 'text',
  placeholder,
}: {
  id: string;
  label: string;
  hint?: string;
  value: string;
  onChange: (value: string) => void;
  type?: string;
  placeholder?: string;
}) {
  return (
    <div className="space-y-1.5">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} type={type} autoComplete="off" value={value} placeholder={placeholder} onChange={(e) => onChange(e.target.value)} />
      {hint ? <p className="text-xs text-muted-foreground">{hint}</p> : null}
    </div>
  );
}
