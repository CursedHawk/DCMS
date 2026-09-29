import { useTranslation } from 'react-i18next';
import type { WidgetProps } from '@rjsf/utils';
import { usePluginCatalog, usePluginInstances } from './api';

/**
 * RJSF widget for a config field annotated `"x-dcms-contract-binding": "<contractId>"`:
 * the field chooses which instance of another plugin this one uses (Events → its Roster).
 *
 * Lists the enabled instances of every plugin that provides the contract and stores the
 * chosen instance's slug — a binding may name its provider by slug or id, and the slug is
 * also what a public site needs to build links, so one value serves both.
 */
export function ContractBindingWidget({ id, value, onChange, options, disabled, readonly }: WidgetProps) {
  const { t } = useTranslation();
  const catalog = usePluginCatalog();
  const instances = usePluginInstances();
  const contract = typeof options.contract === 'string' ? options.contract : '';

  const providers = new Set(
    (catalog.data ?? []).filter((m) => m.provides?.includes(contract)).map((m) => m.id),
  );
  const candidates = (instances.data ?? []).filter((i) => providers.has(i.pluginId));
  const current = typeof value === 'string' ? value : '';
  // A value naming an instance that no longer exists (or is disabled) stays visible, so
  // saving the form never silently rewrites it.
  const orphan = current !== '' && !candidates.some((i) => i.slug === current);

  if (!catalog.isLoading && !instances.isLoading && candidates.length === 0 && !orphan) {
    return (
      <p className="text-sm text-muted-foreground" data-testid="contract-binding-empty">
        {t('plugins.binding.none', { contract })}
      </p>
    );
  }

  return (
    <select
      id={id}
      className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm"
      value={current}
      disabled={disabled || readonly}
      onChange={(e) => onChange(e.target.value === '' ? undefined : e.target.value)}
    >
      <option value="">{t('plugins.binding.unset')}</option>
      {candidates.map((i) => (
        <option key={i.id} value={i.slug} disabled={!i.enabled}>
          {i.name} ({i.slug}){i.enabled ? '' : ` — ${t('plugins.binding.disabled')}`}
        </option>
      ))}
      {orphan ? <option value={current}>{t('plugins.binding.missing', { slug: current })}</option> : null}
    </select>
  );
}
