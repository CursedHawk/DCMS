import { ApiReferenceReact } from '@scalar/api-reference-react';
import '@scalar/api-reference-react/style.css';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { CenteredSpinner, EmptyState, useTheme } from '@dcms/ui';
import { api } from '../../lib/api';

/**
 * The tenant's delivery API, for developer components (P7): the same spec the generated client
 * in src/api is built from, rendered as in Settings › API. Loaded lazily — Scalar is large and
 * most builder sessions never open it.
 */
export default function ApiExplorer() {
  const { t } = useTranslation();
  const { resolved } = useTheme();
  const spec = useQuery({
    queryKey: ['openapi-spec'],
    queryFn: () => api.get<Record<string, unknown>>('/admin/openapi.json'),
  });
  if (spec.isLoading) return <CenteredSpinner />;
  if (!spec.data) return <EmptyState title={t('visual.api.unavailable')} />;
  return (
    <div className="h-full overflow-y-auto">
      <ApiReferenceReact
        key={resolved}
        configuration={{
          content: spec.data,
          forceDarkModeState: resolved,
          hideDarkModeToggle: true,
          hideClientButton: true,
          // Scalar's hosted "Ask AI" would ship the tenant's spec to Scalar.
          agent: { disabled: true },
        }}
      />
    </div>
  );
}
