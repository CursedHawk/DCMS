import { Suspense, lazy, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { CenteredSpinner, DesktopRequired } from '@dcms/ui';
import { EditorHeader } from '../sites/EditorHeader';
import type { RepoTarget } from './IdePage';

const IdePage = lazy(() => import('./IdePage').then((m) => ({ default: m.IdePage })));

export interface RepoSearch {
  name?: string;
  /** The plugin instance (app) the repository belongs to: where the way back leads. */
  slug?: string;
  /** What the repository holds, when the console knows it: `functions`. */
  kind?: string;
}

/** An app's address as the server issues them; anything else is ignored, not trusted. */
const SLUG = /^[a-z0-9][a-z0-9-]{0,62}$/;

/**
 * /repos/$repoId/code: a plugin's own repository (ADR 0029) in the IDE, in focus mode like a
 * site's. Nothing a link can carry becomes a path: the way back and the SDK's address are built
 * here from a validated app slug, and the module from a known kind. Who may open, edit or ship
 * the repository is its binding's, enforced by the server.
 */
export function RepoWorkspace({ repoId, search }: { repoId: string; search: RepoSearch }) {
  const { t } = useTranslation();
  const repo = useMemo<RepoTarget>(() => {
    const slug = search.slug && SLUG.test(search.slug) ? search.slug : undefined;
    const functions = slug !== undefined && search.kind === 'functions';
    return {
      name: search.name?.slice(0, 100) || t('ide.repository'),
      mode: functions ? t('ide.functions') : t('ide.repository'),
      back: slug ? `/apps/${slug}/code` : '/',
      types: functions ? { module: '@dcms/functions', path: `/admin/plugins/${slug}/_functions/sdk` } : undefined,
    };
  }, [search, t]);

  return (
    <DesktopRequired
      header={<EditorHeader name={repo.name} mode={repo.mode} back={repo.back} />}
      labels={{
        title: t('sites.desktopOnly.title'),
        description: t('sites.desktopOnly.description'),
        continueAnyway: t('sites.desktopOnly.continueAnyway'),
      }}
    >
      <Suspense fallback={<CenteredSpinner />}>
        <IdePage siteId={`repo:${repoId}`} repo={repo} />
      </Suspense>
    </DesktopRequired>
  );
}
