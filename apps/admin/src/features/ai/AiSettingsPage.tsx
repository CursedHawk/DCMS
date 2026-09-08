import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Bot, KeyRound, Sparkles } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CenteredSpinner,
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
} from '@dcms/ui';
import { api } from '../../lib/api';
interface AiSettings {
  provider: string;
  model?: string;
  baseUrl?: string;
  hasApiKey: boolean;
}

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
  { value: 'Inherit', label: 'Platform default' },
  { value: 'Anthropic', label: 'Anthropic' },
  { value: 'OpenAi', label: 'OpenAI-compatible' },
  { value: 'Ollama', label: 'Ollama (local)' },
  { value: 'LmStudio', label: 'LM Studio (local)' },
];

export function AiSettingsPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const settings = useQuery({ queryKey: ['ai-settings'], queryFn: () => api.get<AiSettings>('/admin/ai/settings') });

  const [provider, setProvider] = useState('Inherit');
  const [model, setModel] = useState('');
  const [baseUrl, setBaseUrl] = useState('');
  const [apiKey, setApiKey] = useState('');

  useEffect(() => {
    if (settings.data) {
      setProvider(settings.data.provider);
      setModel(settings.data.model ?? '');
      setBaseUrl(settings.data.baseUrl ?? '');
    }
  }, [settings.data]);

  const save = useMutation({
    mutationFn: () =>
      api.put('/admin/ai/settings', {
        provider,
        model: model || null,
        baseUrl: baseUrl || null,
        apiKey: apiKey || null,
      }),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      setApiKey('');
      await qc.invalidateQueries({ queryKey: ['ai-settings'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const clearKey = useMutation({
    mutationFn: () => api.put('/admin/ai/settings', { provider, model: model || null, baseUrl: baseUrl || null, clearApiKey: true }),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      await qc.invalidateQueries({ queryKey: ['ai-settings'] });
    },
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

  if (settings.isLoading) {
    return (
      <Page>
        <PageHeader title={t('ai.title')} />
        <CenteredSpinner />
      </Page>
    );
  }

  return (
    <Page className="max-w-2xl">
      <PageHeader title={t('ai.title')} />
      <Card>
        <CardContent className="space-y-4 p-6">
          <div className="flex items-center gap-2 text-sm text-muted-foreground">
            <Bot className="h-4 w-4" /> {t('app.tagline')}
          </div>

          <div className="space-y-1.5">
            <Label>{t('ai.provider')}</Label>
            <Select value={provider} onValueChange={setProvider}>
              <SelectTrigger>
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

          <div className="grid grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label>{t('ai.model')}</Label>
              <Input value={model} onChange={(e) => setModel(e.target.value)} placeholder="claude-opus-4-8" />
            </div>
            <div className="space-y-1.5">
              <Label>{t('ai.baseUrl')}</Label>
              <Input
                value={baseUrl}
                onChange={(e) => setBaseUrl(e.target.value)}
                placeholder="https://openrouter.ai/api/v1"
              />
            </div>
          </div>

          <p className="text-xs text-muted-foreground">{t('ai.compatibleHint')}</p>

          <div className="space-y-1.5">
            <Label className="flex items-center gap-2">
              <KeyRound className="h-4 w-4" /> {t('ai.apiKey')}
              {settings.data?.hasApiKey ? <Badge tone="success">{t('ai.apiKeySet')}</Badge> : null}
            </Label>
            <Input
              type="password"
              value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
              placeholder="••••••••••••"
            />
            <p className="text-xs text-muted-foreground">
              Stored encrypted (Vault Transit); never returned by the API.
            </p>
          </div>

          <div className="flex justify-between pt-2">
            {settings.data?.hasApiKey ? (
              <Button variant="outline" onClick={() => clearKey.mutate()}>
                {t('ai.clearKey')}
              </Button>
            ) : (
              <span />
            )}
            <Button disabled={save.isPending} onClick={() => save.mutate()}>
              {t('actions.save')}
            </Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardContent className="space-y-4 p-6">
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

          <div className="grid grid-cols-2 gap-3">
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
                {userCred.data?.hasApiKey ? <Badge tone="success">{t('ai.connected')}</Badge> : null}
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

          <div className="flex justify-between pt-2">
            {userCred.data?.hasApiKey || userCred.data?.provider !== 'Inherit' ? (
              <Button variant="outline" onClick={() => disconnectUser.mutate()}>
                {t('ai.disconnect')}
              </Button>
            ) : (
              <span />
            )}
            <Button
              disabled={connectUser.isPending || (userNeedsKey && !userKey.trim() && !userCred.data?.hasApiKey)}
              onClick={() => connectUser.mutate()}
            >
              {t('actions.save')}
            </Button>
          </div>
        </CardContent>
      </Card>
    </Page>
  );
}
