import { useQuery } from '@tanstack/react-query';
import { Suspense, lazy } from 'react';
import { useTranslation } from 'react-i18next';
import { CenteredSpinner } from '../../components/ui/spinner';
import { api } from '../../lib/api';
import { StaticSitePage } from './StaticSitePage';

// The visual builder and the code IDE are both heavy (GrapesJS and Monaco
// respectively); keep them lazy and load only the one this render mode needs.
const BuilderPage = lazy(() => import('../builder/BuilderPage').then((m) => ({ default: m.BuilderPage })));
const IdePage = lazy(() => import('../ide/IdePage').then((m) => ({ default: m.IdePage })));

/**
 * Entry point for /sites/$siteId. Static-files sites get the upload/publish
 * surface; ReactApp sites get the code IDE (React project); StaticPrerender
 * sites get the visual builder over their HTML/CSS source.
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
        <BuilderPage siteId={siteId} />
      )}
    </Suspense>
  );
}
