import { Suspense, lazy, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { CenteredSpinner, DesktopRequired } from '@dcms/ui';
import { EditorHeader } from '../sites/EditorHeader';
import type { RepoTarget } from './IdePage';

const IdePage = lazy(() => import('./IdePage').then((m) => ({ default: m.IdePage })));

export interface RepoSearch {
  name?: string;
  mode?: string;
  back?: string;
  /** The module the code imports (`@dcms/functions`) and the admin path answering its `{ typings }`. */
  module?: string;
  types?: string;
}

/** Only a path inside this console: a link from a plugin screen never sends anyone elsewhere. */
const local = (path?: string) => (path && path.startsWith('/') && !path.startsWith('//') ? path : undefined);

/**
 * /repos/$repoId/code: a plugin's own repository (ADR 0029) in the IDE, in focus mode like a
 * site's. The plugin's screen links here with what to call it and where to go back; who may
 * open, edit or ship it is the repository's binding's, enforced by the server.
 */
export function RepoWorkspace({ repoId, search }: { repoId: string; search: RepoSearch }) {
  const { t } = useTranslation();
  const repo = useMemo<RepoTarget>(() => {
    const types = local(search.types);
    return {
      name: search.name ?? t('ide.repository'),
      mode: search.mode ?? t('ide.repository'),
      back: local(search.back) ?? '/',
      types: types?.startsWith('/admin/plugins/') && search.module ? { module: search.module, path: types } : undefined,
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
