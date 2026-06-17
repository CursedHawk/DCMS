import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Bot, KeyRound } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Page, PageHeader } from '../../components/Page';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Card, CardContent } from '../../components/ui/card';
import { Input } from '../../components/ui/input';
import { Label } from '../../components/ui/label';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '../../components/ui/select';
import { CenteredSpinner } from '../../components/ui/spinner';
import { ApiError, api } from '../../lib/api';

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
    onError: (e) => toast.error(e instanceof ApiError ? e.message : t('errors.generic')),
  });

  const clearKey = useMutation({
    mutationFn: () => api.put('/admin/ai/settings', { provider, model: model || null, baseUrl: baseUrl || null, clearApiKey: true }),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      await qc.invalidateQueries({ queryKey: ['ai-settings'] });
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
    </Page>
  );
}
