import { useMutation } from '@tanstack/react-query';
import { BookOpen, Download } from 'lucide-react';
import { Button, CenteredSpinner, EmptyState, Page, PageHeader, toast, toastApiError } from '@dcms/ui';
import { useCan, usePluginHost, usePluginT, type PluginScreenProps } from '@dcms/plugin-ui';
import { useGuestbookApi, useOverview } from './api';

/**
 * A plugin-wide screen (manifest: "overview", scope Plugin, with a menu entry): every guestbook
 * in the workspace, at /app/sample-guestbook/overview. It draws the whole page; the console only
 * decided it may be drawn (the `read` permission, an enabled instance).
 */
export function OverviewScreen({ instances }: PluginScreenProps) {
  const { t } = usePluginT();
  const host = usePluginHost();
  const overview = useOverview();
  const mayModerate = useCan('moderate');
  const mayExport = useCan('export');
  const actions = useGuestbookApi();
  const exportOne = useMutation({
    mutationFn: (slug: string) => actions.exportEntries(slug),
    onSuccess: () => toast.success(t('overview.exportQueued')),
    onError: (e) => toastApiError(e, t),
  });

  // The console's own data-set view, embedded: the newest entries of the first guestbook.
  const DataSet = host.components.DataSet;
  const first = instances.find((i) => i.enabled);

  return (
    <Page>
      <PageHeader title={t('overview.title')} description={t('overview.subtitle')} />
      {overview.isLoading ? (
        <CenteredSpinner />
      ) : (overview.data?.guestbooks.length ?? 0) === 0 ? (
        <EmptyState icon={BookOpen} title={t('overview.empty')} description={t('overview.emptyHint')} />
      ) : (
        <ul className="gb-books">
          {overview.data!.guestbooks.map((g) => (
            <li key={g.instanceId} className="gb-book">
              <div className="gb-book-name">
                <span>{g.name}</span>
                <code>/{g.slug}</code>
              </div>
              <span className={g.pending > 0 ? 'gb-waiting' : 'gb-quiet'}>
                {g.pending > 0 ? t('overview.waiting', { count: g.pending }) : t('overview.nothingWaiting')}
              </span>
              <span className="gb-quiet">{t('overview.shown', { count: g.approved })}</span>
              <span className="gb-book-actions">
                {mayModerate ? (
                  <Button size="sm" variant={g.pending > 0 ? 'default' : 'outline'} onClick={() => host.navigate(`/plugins/${g.slug}/moderation`)}>
                    {t('overview.moderate')}
                  </Button>
                ) : null}
                {mayExport ? (
                  <Button size="sm" variant="ghost" onClick={() => exportOne.mutate(g.slug)} disabled={exportOne.isPending}>
                    <Download className="h-4 w-4" aria-hidden />
                    {t('overview.export')}
                  </Button>
                ) : null}
              </span>
            </li>
          ))}
        </ul>
      )}
      {overview.data && overview.data.blogPostsSeen > 0 ? (
        <p className="gb-quiet gb-blog">{t('overview.blog', { count: overview.data.blogPostsSeen })}</p>
      ) : null}

      {first ? (
        <section className="gb-latest">
          <h2>{t('overview.latest', { name: first.name })}</h2>
          <DataSet slug={first.slug} set="entries" />
        </section>
      ) : null}
    </Page>
  );
}
