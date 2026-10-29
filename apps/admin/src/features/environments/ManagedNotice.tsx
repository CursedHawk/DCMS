import { useTranslation } from 'react-i18next';
import { Callout } from '@dcms/ui';
import { useEnvironments, useManaged } from './api';

/**
 * Says so on a screen whose configuration -- or one item of it -- this environment leaves to deployments (ADR 0028):
 * changing it here is refused, and where to change it instead. Nothing while the environment owns
 * the kind, or the workspace has production alone.
 */
export function ManagedNotice({ kind, itemKey }: { kind: string; itemKey?: string | null }) {
  const { t } = useTranslation();
  const environments = useEnvironments();
  const { here, managed } = useManaged(kind);
  if (!here || !managed(itemKey)) return null;
  const sources = (environments.data?.environments ?? []).filter((e) => here.promotesFrom.includes(e.id)).map((e) => e.name);
  return (
    <Callout tone="warning" title={t('environments.managedTitle')}>
      {sources.length > 0
        ? t('environments.managedFrom', { names: sources.join(', ') })
        : t('environments.managedNoSource')}
    </Callout>
  );
}
