import { Code, ExternalLink } from 'lucide-react';
import { Badge, Button, Callout, CenteredSpinner, CopyButton, LoadFailed } from '@dcms/ui';
import { instancePath, usePluginHost, usePluginT, useCan, type PluginScreenProps } from '@dcms/plugin-ui';
import { httpPath, useLive, useRepo } from './api';
import { describeTrigger } from './triggers';

/**
 * The app's code: its repository (opened in the console's IDE, with the SDK generated for this
 * environment) and what the live deployment runs.
 */
export function CodeScreen({ instance }: PluginScreenProps) {
  const slug = instance!.slug;
  const { t } = usePluginT();
  const host = usePluginHost();
  const mayEdit = useCan('edit');
  const repo = useRepo(slug);
  const { live, detail } = useLive(slug);

  if (repo.isLoading) return <CenteredSpinner />;
  if (repo.isError || !repo.data) {
    return <LoadFailed title={t('code.loadFailed')} retryLabel={t('common.retry')} onRetry={() => void repo.refetch()} />;
  }
  const open = () => {
    const search = new URLSearchParams({
      name: instance!.name,
      mode: t('code.mode'),
      back: `/apps/${slug}/code`,
      module: '@dcms/functions',
      types: instancePath(slug, '/_functions/sdk'),
    });
    host.navigate(`/repos/${repo.data!.id}/code?${search}`);
  };

  return (
    <div className="space-y-6 pt-2">
      <section className="flex flex-wrap items-start justify-between gap-4 rounded-lg border p-4">
        <div className="min-w-0 space-y-1">
          <h2 className="text-base font-semibold">{t('code.repository')}</h2>
          <p className="text-sm text-muted-foreground">{t('code.repositoryHint')}</p>
          <p className="font-mono text-xs text-muted-foreground">{repo.data.repoFullName}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          {repo.data.httpCloneUrl ? <CopyButton value={repo.data.httpCloneUrl} label={t('code.copyClone')} /> : null}
          {mayEdit ? (
            <Button onClick={open}>
              <Code className="h-4 w-4" /> {t('code.open')}
            </Button>
          ) : null}
        </div>
      </section>

      <section className="space-y-3">
        <h2 className="text-base font-semibold">{t('code.live')}</h2>
        {!live ? (
          <Callout>{t('code.nothingLive')}</Callout>
        ) : (
          <ul className="divide-y rounded-lg border">
            {(detail.data?.functions ?? []).map((f) => (
              <li key={f.name} className="space-y-1 p-3">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-mono text-sm font-medium">{f.name}</span>
                  {f.http.map((who) => <Badge key={who} tone="outline">{t(`access.${who}`)}</Badge>)}
                </div>
                {f.description ? <p className="text-sm text-muted-foreground">{f.description}</p> : null}
                {f.http.length > 0 ? (
                  <p className="flex items-center gap-1 font-mono text-xs text-muted-foreground">
                    <ExternalLink className="h-3 w-3" aria-hidden /> GET|POST {httpPath(slug, f.name)}
                  </p>
                ) : null}
                {f.triggers.length > 0 ? (
                  <p className="text-xs text-muted-foreground">{f.triggers.map((tr) => describeTrigger(tr, t)).join(' · ')}</p>
                ) : null}
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
