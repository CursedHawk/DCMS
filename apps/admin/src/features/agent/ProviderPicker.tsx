import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/ui';
import { api } from '../../lib/api';

export interface ProviderChoice {
  id: string;
  name: string;
  provider: string;
  model: string | null;
  isDefault: boolean;
}

export interface ProviderChoices {
  providers: ProviderChoice[];
  /** The member's own AI settings, when they override the workspace's. */
  own: { provider: string; model: string | null; hasApiKey: boolean } | null;
}

export const providerChoicesKey = ['ai-provider-choices'] as const;

export function useProviderChoices() {
  return useQuery({
    queryKey: providerChoicesKey,
    queryFn: () => api.get<ProviderChoices>('/admin/ai/provider-choices'),
    staleTime: 60_000,
  });
}

/**
 * Which of the workspace's AI providers a conversation runs on, picked before it starts.
 *
 * <p>The empty value is "no pick": the member's own key when they set one, the workspace's default
 * otherwise — so with no own key the default provider is not listed twice. Shown only when there
 * is a choice; locked once the conversation has started, because a turn's thinking is bound to the
 * model that wrote it.</p>
 */
export function ProviderPicker({
  value,
  onChange,
  locked,
  className,
}: {
  value: string | null;
  onChange: (id: string | null) => void;
  locked: boolean;
  className?: string;
}) {
  const { t } = useTranslation();
  const { data } = useProviderChoices();
  if (!data) return null;

  const fallback = data.providers.find((p) => p.isDefault);
  const others = data.own ? data.providers : data.providers.filter((p) => !p.isDefault);
  if (others.length === 0) return null;

  const unpicked = data.own
    ? t('ai.picker.own')
    : fallback
      ? t('ai.picker.default', { name: fallback.name })
      : t('ai.picker.platform');

  return (
    <select
      value={value ?? ''}
      onChange={(ev) => onChange(ev.target.value || null)}
      disabled={locked}
      title={locked ? t('ai.picker.locked') : t('ai.picker.hint')}
      aria-label={t('ai.picker.label')}
      className={cn(
        'max-w-40 truncate rounded border-0 bg-transparent px-1 py-0.5 text-2xs font-medium text-muted-foreground outline-none hover:bg-accent focus:ring-1 focus:ring-primary disabled:opacity-70 disabled:hover:bg-transparent',
        className,
      )}
    >
      <option value="">{unpicked}</option>
      {others.map((p) => (
        <option key={p.id} value={p.id}>
          {p.name}
        </option>
      ))}
    </select>
  );
}
