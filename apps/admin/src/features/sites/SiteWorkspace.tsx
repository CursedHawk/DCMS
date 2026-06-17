import { useQuery } from '@tanstack/react-query';
import { Suspense, lazy } from 'react';
import { useTranslation } from 'react-i18next';
import { CenteredSpinner } from '../../components/ui/spinner';
import { api } from '../../lib/api';
import { StaticSitePage } from './StaticSitePage';

// The canvas editor is heavy; keep it lazy and only load it for editor-backed sites.
const EditorPage = lazy(() => import('../editor/EditorPage').then((m) => ({ default: m.EditorPage })));

/**
 * Entry point for /sites/$siteId. Static-files sites get the upload/publish
 * surface; StaticPrerender + ReactApp sites get the free-canvas editor.
 */
export function SiteWorkspace({ siteId }: { siteId: string }) {
  const { t } = useTranslation();
  const site = useQuery({
    queryKey: ['site', siteId],
    queryFn: () => api.get<{ renderMode: string }>(`/admin/sites/${siteId}`),
  });

  if (site.isLoading) return <CenteredSpinner label={t('common.loading')} />;
  if (site.data?.renderMode === 'StaticFiles') return <StaticSitePage siteId={siteId} />;

  return (
    <Suspense fallback={<CenteredSpinner />}>
      <EditorPage siteId={siteId} />
    </Suspense>
  );
}
