import { useTranslation } from 'react-i18next';
import { Callout } from '@dcms/ui';
import { getCurrentEnvironment } from '../../tenants';
import { useEnvironments, usePolicies } from './api';

/**
 * Says so on a screen whose configuration -- or one item of it -- this environment leaves to deployments (ADR 0028):
 * changing it here is refused, and where to change it instead. Nothing while the environment owns
 * the kind, or the workspace has production alone.
 */
export function ManagedNotice({ kind, itemKey }: { kind: string; itemKey?: string | null }) {
  const { t } = useTranslation();
  const environments = useEnvironments();
  const current = getCurrentEnvironment() ?? 'production';
  const all = environments.data?.environments ?? [];
  const here = environments.data?.enabled ? all.find((e) => e.name === current) : undefined;
  const policies = usePolicies(here?.id);
  // The item's own policy decides when it has one; the kind's otherwise.
  const own = itemKey ? policies.data?.find((p) => p.kind === kind && p.itemKey === itemKey) : undefined;
  const managed = (own ?? policies.data?.find((p) => p.kind === kind && p.itemKey == null))?.mode === 'Managed';
  if (!here || !managed) return null;
  const sources = all.filter((e) => here.promotesFrom.includes(e.id)).map((e) => e.name);
  return (
    <Callout tone="warning" title={t('environments.managedTitle')}>
      {sources.length > 0
        ? t('environments.managedFrom', { names: sources.join(', ') })
        : t('environments.managedNoSource')}
    </Callout>
  );
}
