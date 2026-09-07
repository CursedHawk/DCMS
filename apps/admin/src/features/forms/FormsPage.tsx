import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Check, Inbox, Mail, Search, Trash2, Undo2 } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  CenteredSpinner,
  cn,
  Dialog,
  DialogBody,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  EmptyState,
  Input,
  Page,
  PageHeader,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Table,
  TBody,
  TD,
  TH,
  THead,
  TR,
} from '@dcms/ui';
import { api } from '../../lib/api';
import { Perm, can, useMyPermissions } from '../../lib/permissions';
import {
  type FormDef,
  type HandledFilter,
  PAGE_SIZE,
  type Submission,
  extraKeys,
  formatValue,
  useFormsInstances,
  useSubmissions,
} from './api';

/** Field columns shown inline; the rest of the payload lives in the detail dialog. */
const INLINE_COLUMNS = 3;

interface Selection {
  instanceId: string;
  formName: string;
}

export function FormsPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const { data: me } = useMyPermissions(true);
  const canWrite = can(me, Perm.ContentWrite);

  const instances = useFormsInstances();
  const [selected, setSelected] = useState<Selection | null>(null);
  const [handled, setHandled] = useState<HandledFilter>('all');
  const [page, setPage] = useState(1);
  const [query, setQuery] = useState('');
  // Held by id, not by value, so the open dialog reflects a mark-handled toggle
  // as soon as the list refetches.
  const [detailId, setDetailId] = useState<string | null>(null);

  // Land on the first form so the page is never an empty shell when there is
  // something to read.
  useEffect(() => {
    if (selected || !instances.data) return;
    for (const instance of instances.data) {
      const first = instance.forms[0];
      if (first) {
        setSelected({ instanceId: instance.instanceId, formName: first.name });
        return;
      }
    }
  }, [instances.data, selected]);

  const form: FormDef | undefined = useMemo(() => {
    if (!selected || !instances.data) return undefined;
    return instances.data
      .find((i) => i.instanceId === selected.instanceId)
      ?.forms.find((f) => f.name === selected.formName);
  }, [instances.data, selected]);

  const submissions = useSubmissions(selected?.instanceId, selected?.formName, handled, page);

  const refresh = async () => {
    await Promise.all([
      qc.invalidateQueries({ queryKey: ['form-submissions'] }),
      qc.invalidateQueries({ queryKey: ['forms-instances'] }),
    ]);
  };

  const toggleHandled = useMutation({
    mutationFn: (id: string) => api.post(`/admin/forms/submissions/${id}/handled`),
    onSuccess: refresh,
    onError: () => toast.error(t('errors.generic')),
  });

  const remove = useMutation({
    mutationFn: (id: string) => api.del(`/admin/forms/submissions/${id}`),
    onSuccess: async () => {
      setDetailId(null);
      await refresh();
    },
    onError: () => toast.error(t('errors.generic')),
  });

  const select = (next: Selection) => {
    setSelected(next);
    setPage(1);
    setQuery('');
  };

  const yes = t('common.yes');
  const no = t('common.no');

  // Free-text filter over the loaded page: enough to pick a submission out of a
  // screenful without paging the whole form's history through the server.
  const q = query.trim().toLowerCase();
  const items = submissions.data?.items ?? [];
  const detail = items.find((s) => s.id === detailId) ?? null;
  const rows = items.filter(
    (s) =>
      !q ||
      Object.values(s.data).some((v) => formatValue(v, yes, no).toLowerCase().includes(q)),
  );

  const columns = (form?.fields ?? []).slice(0, INLINE_COLUMNS);
  const total = submissions.data?.totalCount ?? 0;
  const lastPage = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const hasAnyForm = (instances.data ?? []).some((i) => i.forms.length > 0);

  if (instances.isLoading) {
    return (
      <Page>
        <PageHeader title={t('forms.title')} description={t('forms.subtitle')} />
        <CenteredSpinner />
      </Page>
    );
  }

  if (!hasAnyForm) {
    return (
      <Page>
        <PageHeader title={t('forms.title')} description={t('forms.subtitle')} />
        <EmptyState icon={Inbox} title={t('forms.noForms')} description={t('forms.noFormsHint')} />
      </Page>
    );
  }

  return (
    <Page className="max-w-7xl">
      <PageHeader
        title={t('forms.title')}
        description={t('forms.subtitle')}
        actions={
          <>
            <div className="relative w-56 max-w-full">
              <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
              <Input
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder={t('forms.searchPlaceholder')}
                className="pl-8"
              />
            </div>
            <Select
              value={handled}
              onValueChange={(v) => {
                setHandled(v as HandledFilter);
                setPage(1);
              }}
            >
              <SelectTrigger className="w-40">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="all">{t('forms.filterAll')}</SelectItem>
                <SelectItem value="unhandled">{t('forms.filterUnhandled')}</SelectItem>
                <SelectItem value="handled">{t('forms.filterHandled')}</SelectItem>
              </SelectContent>
            </Select>
          </>
        }
      />

      <div className="grid gap-6 lg:grid-cols-[16rem_minmax(0,1fr)]">
        <nav className="space-y-4">
          {(instances.data ?? [])
            .filter((instance) => instance.forms.length > 0)
            .map((instance) => (
              <div key={instance.instanceId} className="space-y-1">
                <p className="px-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  {instance.name}
                </p>
                {instance.forms.map((f) => {
                  const active =
                    selected?.instanceId === instance.instanceId && selected.formName === f.name;
                  return (
                    <button
                      key={f.name}
                      type="button"
                      onClick={() => select({ instanceId: instance.instanceId, formName: f.name })}
                      className={cn(
                        'flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm transition-colors',
                        active ? 'bg-accent text-accent-foreground' : 'hover:bg-accent/50',
                      )}
                    >
                      <span className="min-w-0 flex-1 truncate">{f.title}</span>
                      {f.notifyRecipients.length > 0 ? (
                        <Mail
                          className="h-3.5 w-3.5 shrink-0 text-muted-foreground"
                          aria-label={t('forms.notifyOn')}
                        />
                      ) : null}
                      {f.unhandledCount > 0 ? (
                        <Badge tone="default">{f.unhandledCount}</Badge>
                      ) : (
                        <span className="text-xs text-muted-foreground">{f.totalCount}</span>
                      )}
                    </button>
                  );
                })}
              </div>
            ))}
        </nav>

        <div className="min-w-0 space-y-3">
          {form ? (
            <p className="text-xs text-muted-foreground">
              {form.notifyRecipients.length > 0
                ? t('forms.notifyingTo', { recipients: form.notifyRecipients.join(', ') })
                : t('forms.notifyOffHint')}
            </p>
          ) : null}

          {/* `!form` covers the first render, before the effect picks a form. */}
          {submissions.isLoading || !form ? (
            <CenteredSpinner />
          ) : rows.length === 0 ? (
            <EmptyState
              icon={Inbox}
              title={t('forms.noSubmissions')}
              description={q ? t('common.noResults') : t('forms.noSubmissionsHint')}
            />
          ) : (
            <>
              <Table>
                <THead>
                  <TR>
                    <TH className="w-44">{t('forms.submittedAt')}</TH>
                    {columns.map((c) => (
                      <TH key={c.name}>{c.label}</TH>
                    ))}
                    <TH className="w-28">{t('common.status')}</TH>
                    <TH className="w-24 text-right">{t('common.actions')}</TH>
                  </TR>
                </THead>
                <TBody>
                  {rows.map((s) => (
                    <TR
                      key={s.id}
                      className="cursor-pointer"
                      onClick={() => setDetailId(s.id)}
                      title={t('forms.viewDetail')}
                    >
                      <TD className="whitespace-nowrap text-muted-foreground">
                        {new Date(s.submittedAt).toLocaleString()}
                      </TD>
                      {columns.map((c) => (
                        <TD key={c.name} className="max-w-[16rem] truncate">
                          {formatValue(s.data[c.name], yes, no)}
                        </TD>
                      ))}
                      <TD>
                        <Badge tone={s.handledAt ? 'success' : 'warning'}>
                          {s.handledAt ? t('forms.handled') : t('forms.new')}
                        </Badge>
                      </TD>
                      <TD className="text-right" onClick={(e) => e.stopPropagation()}>
                        {canWrite ? (
                          <div className="flex justify-end gap-1">
                            <Button
                              size="icon"
                              variant="ghost"
                              title={s.handledAt ? t('forms.markUnhandled') : t('forms.markHandled')}
                              onClick={() => toggleHandled.mutate(s.id)}
                            >
                              {s.handledAt ? (
                                <Undo2 className="h-4 w-4" />
                              ) : (
                                <Check className="h-4 w-4" />
                              )}
                            </Button>
                            <Button
                              size="icon"
                              variant="ghost"
                              title={t('actions.delete')}
                              onClick={() => {
                                if (window.confirm(t('forms.deleteConfirm'))) remove.mutate(s.id);
                              }}
                            >
                              <Trash2 className="h-4 w-4 text-destructive" />
                            </Button>
                          </div>
                        ) : null}
                      </TD>
                    </TR>
                  ))}
                </TBody>
              </Table>

              <div className="flex items-center justify-between text-xs text-muted-foreground">
                <span>{t('forms.pageCount', { page, lastPage, total })}</span>
                <div className="flex gap-2">
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={page <= 1}
                    onClick={() => setPage((p) => p - 1)}
                  >
                    {t('forms.previous')}
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={page >= lastPage}
                    onClick={() => setPage((p) => p + 1)}
                  >
                    {t('forms.next')}
                  </Button>
                </div>
              </div>
            </>
          )}
        </div>
      </div>

      {detail && form ? (
        <SubmissionDialog
          submission={detail}
          form={form}
          canWrite={canWrite}
          onClose={() => setDetailId(null)}
          onToggleHandled={() => toggleHandled.mutate(detail.id)}
          onDelete={() => {
            if (window.confirm(t('forms.deleteConfirm'))) remove.mutate(detail.id);
          }}
        />
      ) : null}
    </Page>
  );
}

function SubmissionDialog({
  submission,
  form,
  canWrite,
  onClose,
  onToggleHandled,
  onDelete,
}: {
  submission: Submission;
  form: FormDef;
  canWrite: boolean;
  onClose: () => void;
  onToggleHandled: () => void;
  onDelete: () => void;
}) {
  const { t } = useTranslation();
  const yes = t('common.yes');
  const no = t('common.no');
  // Fields the form no longer declares still have stored values; showing them
  // keeps an edited form from silently hiding what a visitor actually sent.
  const orphans = extraKeys(submission, form.fields);

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{form.title}</DialogTitle>
        </DialogHeader>
        <DialogBody className="space-y-4">
          <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
            <span>{new Date(submission.submittedAt).toLocaleString()}</span>
            <Badge tone={submission.handledAt ? 'success' : 'warning'}>
              {submission.handledAt ? t('forms.handled') : t('forms.new')}
            </Badge>
          </div>

          <dl className="space-y-3">
            {form.fields.map((f) => (
              <div key={f.name}>
                <dt className="text-xs uppercase tracking-wide text-muted-foreground">{f.label}</dt>
                <dd className="whitespace-pre-wrap break-words text-sm">
                  {formatValue(submission.data[f.name], yes, no)}
                </dd>
              </div>
            ))}
            {orphans.map((key) => (
              <div key={key}>
                <dt className="text-xs uppercase tracking-wide text-muted-foreground">
                  {key} · {t('forms.removedField')}
                </dt>
                <dd className="whitespace-pre-wrap break-words text-sm">
                  {formatValue(submission.data[key], yes, no)}
                </dd>
              </div>
            ))}
          </dl>

          {submission.userAgent ? (
            <p className="break-words text-xs text-muted-foreground">
              {t('forms.userAgent')}: {submission.userAgent}
            </p>
          ) : null}
        </DialogBody>
        <DialogFooter>
          {canWrite ? (
            <>
              <Button variant="outline" onClick={onDelete}>
                <Trash2 className="h-4 w-4" />
                {t('actions.delete')}
              </Button>
              <Button onClick={onToggleHandled}>
                {submission.handledAt ? t('forms.markUnhandled') : t('forms.markHandled')}
              </Button>
            </>
          ) : (
            <Button variant="outline" onClick={onClose}>
              {t('actions.close')}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
