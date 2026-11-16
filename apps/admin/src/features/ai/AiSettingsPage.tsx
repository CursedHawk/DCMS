import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { BarChart3, Bot, KeyRound, Pencil, Plus, Sparkles, Star, Trash2 } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CenteredSpinner,
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  EmptyState,
  Input,
  Label,
  Page,
  PageHeader,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  toastApiError,
  LoadFailed,
  useConfirm,
} from '@dcms/ui';
import { Link } from '@tanstack/react-router';
import { api } from '../../lib/api';
import { providerChoicesKey } from '../agent/ProviderPicker';

/** One of the workspace's AI providers, as the settings page reads it. */
interface WorkspaceProvider {
  id: string;
  name: string;
  provider: string;
  model: string | null;
  baseUrl: string | null;
  hasApiKey: boolean;
  isDefault: boolean;
}

const PROVIDERS_KEY = ['ai-providers'] as const;

interface UserCredentials {
  provider: string;
  model?: string | null;
  baseUrl?: string | null;
  hasApiKey: boolean;
}

/** Providers that run on the operator's own machine and are never sent a credential. */
const LOCAL_PROVIDERS = ['Ollama', 'LmStudio'];

/*
 * Four entries cover every provider anyone actually uses.
 *
 * "OpenAI-compatible" is the one that does the work: OpenRouter, Groq, Together, Fireworks,
 * DeepSeek, Mistral, Azure OpenAI and any self-hosted vLLM or LiteLLM all speak the same Chat
 * Completions API and differ only in the base URL — so they are that entry with a URL, not four
 * more rows in this list and four more branches on the server. Ollama and LM Studio are named
 * separately because they are the two that run on the operator's own machine, which is what
 * decides whether a plaintext, private base URL is allowed.
 */
const PROVIDERS = [
  { value: 'Inherit', label: 'Platform’s provider' },
  { value: 'Anthropic', label: 'Anthropic' },
  { value: 'OpenAi', label: 'OpenAI-compatible' },
  { value: 'Ollama', label: 'Ollama (local)' },
  { value: 'LmStudio', label: 'LM Studio (local)' },
];

export function AiSettingsPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const confirm = useConfirm();
  const providers = useQuery({
    queryKey: PROVIDERS_KEY,
    queryFn: () => api.get<WorkspaceProvider[]>('/admin/ai/providers'),
  });
  const [editing, setEditing] = useState<WorkspaceProvider | 'new' | null>(null);

  const refresh = async () => {
    await qc.invalidateQueries({ queryKey: PROVIDERS_KEY });
    await qc.invalidateQueries({ queryKey: providerChoicesKey });
  };
  const makeDefault = useMutation({
    mutationFn: (p: WorkspaceProvider) =>
      api.put(`/admin/ai/providers/${p.id}`, { name: p.name, provider: p.provider, model: p.model, baseUrl: p.baseUrl, isDefault: true }),
    onSuccess: refresh,
    onError: (e) => toastApiError(e, t),
  });
  const remove = useMutation({
    mutationFn: (id: string) => api.del(`/admin/ai/providers/${id}`),
    onSuccess: async () => {
      toast.success(t('ai.providers.removed'));
      await refresh();
    },
    onError: (e) => toastApiError(e, t),
  });

  /*
   * The per-user credential — "use my own key" — which powers the assistant and is billed to
   * the individual rather than to the workspace.
   *
   * It used to be an Anthropic key and nothing else: the form sent `provider: 'Anthropic'`
   * whatever the workspace was configured for, which pinned Anthropic on the user's row and made
   * every assistant turn ask for an Anthropic key even when the workspace was pointed at its own
   * Ollama. Leaving the provider unset now means "follow the workspace", which is what connecting
   * a key should have meant.
   */
  const userCred = useQuery({
    queryKey: ['ai-user-credentials'],
    queryFn: () => api.get<UserCredentials>('/admin/ai/user-credentials'),
  });
  const [userProvider, setUserProvider] = useState('Inherit');
  const [userModel, setUserModel] = useState('');
  const [userBaseUrl, setUserBaseUrl] = useState('');
  const [userKey, setUserKey] = useState('');

  useEffect(() => {
    if (!userCred.data) return;
    setUserProvider(userCred.data.provider || 'Inherit');
    setUserModel(userCred.data.model ?? '');
    setUserBaseUrl(userCred.data.baseUrl ?? '');
  }, [userCred.data]);

  // A local model authenticates nothing, so the form must not insist on a key for one.
  const userNeedsKey = !LOCAL_PROVIDERS.includes(userProvider);

  const connectUser = useMutation({
    mutationFn: () =>
      api.put('/admin/ai/user-credentials', {
        provider: userProvider === 'Inherit' ? null : userProvider,
        model: userModel || null,
        baseUrl: userBaseUrl || null,
        apiKey: userKey || null,
      }),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      setUserKey('');
      await qc.invalidateQueries({ queryKey: ['ai-user-credentials'] });
    },
    onError: (e) => toastApiError(e, t),
  });
  const disconnectUser = useMutation({
    mutationFn: () => api.del('/admin/ai/user-credentials'),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      await qc.invalidateQueries({ queryKey: ['ai-user-credentials'] });
    },
  });

  if (providers.isLoading || providers.isError) {
    return (
      <Page>
        <PageHeader title={t('ai.title')} />
        {providers.isError ? <LoadFailed onRetry={() => void providers.refetch()} /> : <CenteredSpinner />}
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader title={t('ai.title')} />
      {/* A form reads better narrow; the page keeps the width every other section has. */}
      <div className="max-w-2xl space-y-6">
        <Card>
          <CardContent className="space-y-4">
            <div className="flex flex-wrap items-center gap-2">
              <span className="flex items-center gap-2 text-sm font-medium">
                <Bot className="h-4 w-4 text-primary" /> {t('ai.providers.title')}
              </span>
              <Button size="sm" className="ml-auto" onClick={() => setEditing('new')}>
                <Plus className="h-4 w-4" /> {t('ai.providers.add')}
              </Button>
            </div>
            <p className="text-xs text-muted-foreground">{t('ai.providers.body')}</p>

            {providers.data?.length ? (
              <ul className="divide-y rounded-md border">
                {providers.data.map((p) => (
                  <li key={p.id} className="flex flex-wrap items-center gap-2 px-3 py-2.5">
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="font-medium [overflow-wrap:anywhere]">{p.name}</span>
                        {p.isDefault ? <Badge tone="secondary">{t('ai.providers.default')}</Badge> : null}
                        {p.hasApiKey ? <Badge tone="success">{t('ai.apiKeySet')}</Badge> : null}
                      </div>
                      <div className="text-xs text-muted-foreground [overflow-wrap:anywhere]">
                        {PROVIDERS.find((k) => k.value === p.provider)?.label ?? p.provider}
                        {p.model ? ` · ${p.model}` : ''}
                        {p.baseUrl ? ` · ${p.baseUrl}` : ''}
                      </div>
                    </div>
                    <div className="flex gap-1">
                      {p.isDefault ? null : (
                        <Button
                          size="icon"
                          variant="ghost"
                          title={t('ai.providers.makeDefault')}
                          aria-label={t('ai.providers.makeDefault')}
                          disabled={makeDefault.isPending}
                          onClick={() => makeDefault.mutate(p)}
                        >
                          <Star className="h-4 w-4" />
                        </Button>
                      )}
                      <Button size="icon" variant="ghost" title={t('actions.edit')} aria-label={t('actions.edit')} onClick={() => setEditing(p)}>
                        <Pencil className="h-4 w-4" />
                      </Button>
                      <Button
                        size="icon"
                        variant="ghost"
                        title={t('actions.delete')}
                        aria-label={t('actions.delete')}
                        onClick={() =>
                          void confirm({
                            title: t('ai.providers.confirmDelete', { name: p.name }),
                            description: t('ai.providers.confirmDeleteBody'),
                            destructive: true,
                          }).then((ok) => ok && remove.mutate(p.id))
                        }
                      >
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    </div>
                  </li>
                ))}
              </ul>
            ) : (
              <EmptyState icon={Bot} title={t('ai.providers.none')} description={t('ai.providers.noneHint')} />
            )}

            <Link to={"/settings/ai-usage" as string} className="inline-flex items-center gap-1.5 text-sm text-primary hover:underline">
              <BarChart3 className="h-4 w-4" /> {t('ai.usage.link')}
            </Link>
          </CardContent>
        </Card>

        <Card>
          <CardContent className="space-y-4">
            <div className="flex items-center gap-2 text-sm font-medium">
              <Sparkles className="h-4 w-4 text-primary" />
              {t('ai.userKeyTitle')}
            </div>
            <p className="text-xs text-muted-foreground">{t('ai.userKeyBody')}</p>

            <div className="space-y-1.5">
              <Label>{t('ai.provider')}</Label>
              <Select value={userProvider} onValueChange={setUserProvider}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="Inherit">{t('ai.useWorkspaceProvider')}</SelectItem>
                  {PROVIDERS.filter((p) => p.value !== 'Inherit').map((p) => (
                    <SelectItem key={p.value} value={p.value}>
                      {p.label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>

            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <div className="space-y-1.5">
                <Label>{t('ai.model')}</Label>
                <Input
                  value={userModel}
                  onChange={(e) => setUserModel(e.target.value)}
                  placeholder={t('ai.inheritPlaceholder')}
                />
              </div>
              <div className="space-y-1.5">
                <Label>{t('ai.baseUrl')}</Label>
                <Input
                  value={userBaseUrl}
                  onChange={(e) => setUserBaseUrl(e.target.value)}
                  placeholder={t('ai.inheritPlaceholder')}
                />
              </div>
            </div>

            {userNeedsKey ? (
              <div className="space-y-1.5">
                <Label className="flex items-center gap-2">
                  <KeyRound className="h-4 w-4" /> {t('ai.apiKey')}
                  {userCred.data?.hasApiKey ? (
                    <Badge tone="success">{t('ai.connected')}</Badge>
                  ) : null}
                </Label>
                <Input
                  type="password"
                  value={userKey}
                  onChange={(e) => setUserKey(e.target.value)}
                  placeholder="••••••••••••"
                />
                <p className="text-xs text-muted-foreground">{t('ai.keyStorage')}</p>
              </div>
            ) : (
              // Said rather than left blank: a missing key field looks like a bug unless the page
              // explains that this provider does not have one.
              <p className="text-xs text-muted-foreground">{t('ai.localNeedsNoKey')}</p>
            )}

            <div className="flex flex-wrap justify-between gap-2 pt-2">
              {userCred.data?.hasApiKey || userCred.data?.provider !== 'Inherit' ? (
                <Button variant="outline" onClick={() => disconnectUser.mutate()}>
                  {t('ai.disconnect')}
                </Button>
              ) : (
                <span />
              )}
              <Button
                disabled={
                  connectUser.isPending ||
                  (userNeedsKey && !userKey.trim() && !userCred.data?.hasApiKey)
                }
                onClick={() => connectUser.mutate()}
              >
                {t('actions.save')}
              </Button>
            </div>
          </CardContent>
        </Card>
      </div>
      {editing && (
        <ProviderDialog
          provider={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={refresh}
        />
      )}
    </Page>
  );
}

/** Add a workspace provider, or edit one. The key is write-only: blank keeps the stored one. */
function ProviderDialog({
  provider,
  onClose,
  onSaved,
}: {
  provider: WorkspaceProvider | null;
  onClose: () => void;
  onSaved: () => Promise<void>;
}) {
  const { t } = useTranslation();
  const [name, setName] = useState(provider?.name ?? '');
  const [kind, setKind] = useState(provider?.provider ?? 'Anthropic');
  const [model, setModel] = useState(provider?.model ?? '');
  const [baseUrl, setBaseUrl] = useState(provider?.baseUrl ?? '');
  const [apiKey, setApiKey] = useState('');
  const [clearKey, setClearKey] = useState(false);
  const local = LOCAL_PROVIDERS.includes(kind);

  const save = useMutation({
    mutationFn: () => {
      const body = {
        name: name.trim(),
        provider: kind,
        model: model.trim() || null,
        baseUrl: baseUrl.trim() || null,
        apiKey: local ? null : apiKey || null,
        clearApiKey: local || clearKey,
      };
      return provider ? api.put(`/admin/ai/providers/${provider.id}`, body) : api.post('/admin/ai/providers', body);
    },
    onSuccess: async () => {
      toast.success(t('common.saved'));
      await onSaved();
      onClose();
    },
    onError: (e) => toastApiError(e, t),
  });

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{provider ? t('ai.providers.edit', { name: provider.name }) : t('ai.providers.add')}</DialogTitle>
        </DialogHeader>
        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault();
            save.mutate();
          }}
        >
          <div className="space-y-1.5">
            <Label htmlFor="provider-name">{t('ai.providers.name')}</Label>
            <Input id="provider-name" value={name} maxLength={80} onChange={(e) => setName(e.target.value)} placeholder="Anthropic" />
          </div>
          <div className="space-y-1.5">
            <Label>{t('ai.provider')}</Label>
            <Select value={kind} onValueChange={setKind}>
              <SelectTrigger aria-label={t('ai.provider')}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {PROVIDERS.map((p) => (
                  <SelectItem key={p.value} value={p.value}>
                    {p.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label htmlFor="provider-model">{t('ai.model')}</Label>
              <Input id="provider-model" value={model} onChange={(e) => setModel(e.target.value)} placeholder="claude-opus-4-8" />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="provider-url">{t('ai.baseUrl')}</Label>
              <Input
                id="provider-url"
                value={baseUrl}
                onChange={(e) => setBaseUrl(e.target.value)}
                placeholder="https://openrouter.ai/api/v1"
              />
            </div>
          </div>
          <p className="text-xs text-muted-foreground">{t('ai.compatibleHint')}</p>
          {local ? (
            <p className="text-xs text-muted-foreground">{t('ai.localNeedsNoKey')}</p>
          ) : (
            <div className="space-y-1.5">
              <Label htmlFor="provider-key" className="flex items-center gap-2">
                <KeyRound className="h-4 w-4" /> {t('ai.apiKey')}
                {provider?.hasApiKey && !clearKey ? <Badge tone="success">{t('ai.apiKeySet')}</Badge> : null}
              </Label>
              <Input
                id="provider-key"
                type="password"
                value={apiKey}
                onChange={(e) => {
                  setApiKey(e.target.value);
                  setClearKey(false);
                }}
                placeholder="••••••••••••"
              />
              <p className="text-xs text-muted-foreground">{t('ai.keyStorage')}</p>
              {provider?.hasApiKey && !apiKey ? (
                <label className="flex items-center gap-2 text-xs">
                  <input type="checkbox" checked={clearKey} onChange={(e) => setClearKey(e.target.checked)} />
                  {t('ai.clearKey')}
                </label>
              ) : null}
            </div>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              {t('actions.cancel')}
            </Button>
            <Button type="submit" disabled={save.isPending || !name.trim()}>
              {t('actions.save')}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
