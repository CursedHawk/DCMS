import { useQuery } from '@tanstack/react-query';
import { Suspense, lazy } from 'react';
import { useTranslation } from 'react-i18next';
import { CenteredSpinner } from '../../components/ui/spinner';
import { api } from '../../lib/api';
import { StaticSitePage } from './StaticSitePage';

// The canvas editor and the code IDE are both heavy; keep them lazy and load
// only the one this site's render mode needs.
const EditorPage = lazy(() => import('../editor/EditorPage').then((m) => ({ default: m.EditorPage })));
const IdePage = lazy(() => import('../ide/IdePage').then((m) => ({ default: m.IdePage })));

/**
 * Entry point for /sites/$siteId. Static-files sites get the upload/publish
 * surface; ReactApp sites get the code IDE (file map editor); StaticPrerender
 * sites get the free-canvas visual editor.
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
      {site.data?.renderMode === 'ReactApp' ? (
        <IdePage siteId={siteId} />
      ) : (
        <EditorPage siteId={siteId} />
      )}
    </Suspense>
  );
}
