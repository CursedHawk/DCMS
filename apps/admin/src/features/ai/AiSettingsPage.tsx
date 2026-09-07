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

const PROVIDERS = [
  { value: 'Inherit', label: 'Platform default' },
  { value: 'Anthropic', label: 'Anthropic' },
  { value: 'OpenAi', label: 'OpenAI' },
  { value: 'Ollama', label: 'Ollama' },
  { value: 'LmStudio', label: 'LM Studio' },
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

  // Per-user credential ("connect your own Anthropic account") — powers the web-IDE
  // assistant, billed to the individual user. Separate from the tenant settings above.
  const userCred = useQuery({
    queryKey: ['ai-user-credentials'],
    queryFn: () => api.get<{ hasApiKey: boolean }>('/admin/ai/user-credentials'),
  });
  const [userKey, setUserKey] = useState('');
  const connectUser = useMutation({
    mutationFn: () => api.put('/admin/ai/user-credentials', { provider: 'Anthropic', apiKey: userKey }),
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
              <Input value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} placeholder="http://localhost:11434" />
            </div>
          </div>

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
            {t('ai.userKeyTitle', 'Your Anthropic account')}
          </div>
          <p className="text-xs text-muted-foreground">
            {t(
              'ai.userKeyBody',
              'Connect your personal Anthropic API key to power the web-IDE assistant. This is an API key from console.anthropic.com (pay-as-you-go) — not a Claude Pro/Max subscription login. It is stored encrypted (Vault Transit) and used only for your own IDE sessions.',
            )}
          </p>

          <div className="space-y-1.5">
            <Label className="flex items-center gap-2">
              <KeyRound className="h-4 w-4" /> {t('ai.apiKey')}
              {userCred.data?.hasApiKey ? <Badge tone="success">{t('ai.connected', 'Connected')}</Badge> : null}
            </Label>
            <Input
              type="password"
              value={userKey}
              onChange={(e) => setUserKey(e.target.value)}
              placeholder="sk-ant-..."
            />
          </div>

          <div className="flex justify-between pt-2">
            {userCred.data?.hasApiKey ? (
              <Button variant="outline" onClick={() => disconnectUser.mutate()}>
                {t('ai.disconnect', 'Disconnect')}
              </Button>
            ) : (
              <span />
            )}
            <Button disabled={connectUser.isPending || !userKey.trim()} onClick={() => connectUser.mutate()}>
              {t('ai.connect', 'Connect')}
            </Button>
          </div>
        </CardContent>
      </Card>
    </Page>
  );
}
