import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Check, X } from 'lucide-react';
import { Button, CenteredSpinner, EmptyState, toast, toastApiError } from '@dcms/ui';
import {
  instancePath,
  useCan,
  usePluginApi,
  usePluginT,
  type PluginScreenProps,
} from '@dcms/plugin-ui';
import { useEntries, useGuestbookApi, type EntryRow, type StatusFilter } from './api';
import { ACCENTS, accentOf } from './accents';

const FILTERS: StatusFilter[] = ['pending', 'approved', 'rejected'];

/**
 * An instance screen (manifest: "moderation", scope Instance): a tab of each guestbook's page,
 * /plugins/{slug}/moderation. The entries are a wall of notes in the guestbook's own accent, so
 * reading them feels like reading the book; approving one is one click.
 */
export function ModerationScreen({ instance }: PluginScreenProps) {
  const { t, i18n } = usePluginT();
  const api = usePluginApi();
  const qc = useQueryClient();
  const actions = useGuestbookApi();
  const mayModerate = useCan('moderate');
  const [status, setStatus] = useState<StatusFilter>('pending');
  const slug = instance!.slug;

  const appearance = useQuery({
    queryKey: ['guestbook', 'appearance', slug],
    queryFn: () => api.get<{ title: string | null; accent: string | null }>(instancePath(slug, '/appearance')),
  });
  const entries = useEntries(slug, status);
  const accent = ACCENTS[accentOf(appearance.data?.accent)];

  const refresh = () => qc.invalidateQueries({ queryKey: ['guestbook'] });
  const approve = useMutation({
    mutationFn: (id: string) => actions.approve(slug, id),
    onSuccess: async () => {
      toast.success(t('moderation.approved'));
      await refresh();
    },
    onError: (e) => toastApiError(e, t),
  });
  const reject = useMutation({
    mutationFn: (id: string) => actions.reject(slug, id),
    onSuccess: async () => {
      toast.success(t('moderation.rejected'));
      await refresh();
    },
    onError: (e) => toastApiError(e, t),
  });

  return (
    <div className="gb-moderation" style={{ ['--gb-ribbon' as string]: accent.ribbon, ['--gb-paper' as string]: accent.paper }}>
      {!instance!.enabled ? <p className="gb-off">{t('moderation.off')}</p> : null}

      <div role="tablist" aria-label={t('moderation.title')} className="gb-filter">
        {FILTERS.map((f) => (
          <button key={f} role="tab" type="button" aria-selected={f === status} onClick={() => setStatus(f)}>
            {t(`moderation.filters.${f}`)}
          </button>
        ))}
      </div>

      {entries.isLoading ? (
        <CenteredSpinner />
      ) : (entries.data?.rows.length ?? 0) === 0 ? (
        <EmptyState title={t(`moderation.empty.${status}`)} />
      ) : (
        <ul className="gb-wall">
          {entries.data!.rows.map((row) => (
            <Note
              key={row.key}
              row={row}
              language={i18n.language}
              actions={
                mayModerate && status === 'pending' ? (
                  <>
                    <Button size="sm" onClick={() => approve.mutate(row.key)} disabled={approve.isPending}>
                      <Check className="h-4 w-4" aria-hidden />
                      {t('moderation.approve')}
                    </Button>
                    <Button size="sm" variant="ghost" onClick={() => reject.mutate(row.key)} disabled={reject.isPending}>
                      <X className="h-4 w-4" aria-hidden />
                      {t('moderation.reject')}
                    </Button>
                  </>
                ) : null
              }
              replyLabel={t('moderation.reply')}
            />
          ))}
        </ul>
      )}
    </div>
  );
}

function Note({
  row,
  language,
  actions,
  replyLabel,
}: {
  row: EntryRow;
  language: string;
  actions: React.ReactNode;
  replyLabel: string;
}) {
  const v = row.values;
  return (
    <li className="gb-note">
      <blockquote className="gb-message">{v.message}</blockquote>
      <p className="gb-signature">
        <span className="gb-name">{v.name}</span>
        <time dateTime={v.signedAt}>
          {new Date(v.signedAt).toLocaleDateString(language, { day: 'numeric', month: 'long', year: 'numeric' })}
        </time>
      </p>
      {v.reply ? (
        <p className="gb-reply">
          <span className="gb-reply-label">{replyLabel}</span> {v.reply}
        </p>
      ) : null}
      {actions ? <div className="gb-actions">{actions}</div> : null}
    </li>
  );
}
