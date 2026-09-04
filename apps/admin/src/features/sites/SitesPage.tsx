import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { PanelsTopLeft, Plus, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  Card,
  CenteredSpinner,
  ConfirmDeleteDialog,
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
  EmptyState,
  Input,
  Label,
  Page,
  PageHeader,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  toastApiError,
} from '@dcms/admin-ui';
import { api } from '../../lib/api';
interface SiteSummary {
  id: string;
  name: string;
  renderMode: string;
  activeBuildId?: string;
}

export function SitesPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState('');
  const [mode, setMode] = useState('StaticPrerender');
  const [pendingDelete, setPendingDelete] = useState<SiteSummary | null>(null);

  const sites = useQuery({ queryKey: ['sites'], queryFn: () => api.get<SiteSummary[]>('/admin/sites') });

  const create = useMutation({
    mutationFn: () => api.post<{ id: string }>('/admin/sites', { name: name.trim(), renderMode: mode }),
    onSuccess: async ({ id }) => {
      setOpen(false);
      setName('');
      await qc.invalidateQueries({ queryKey: ['sites'] });
      void navigate({ to: '/sites/$siteId', params: { siteId: id } });
    },
    onError: () => toast.error(t('errors.generic')),
  });

  const remove = useMutation({
    mutationFn: (id: string) => api.del<{ storageFailed: boolean }>(`/admin/sites/${id}`),
    // Storage and Forgejo are cleaned up best-effort after the rows are gone, so a
    // delete can succeed while leaving files behind — say so rather than claiming
    // a clean sweep.
    onSuccess: async (result) => {
      setPendingDelete(null);
      toast[result?.storageFailed ? 'warning' : 'success'](
        t(result?.storageFailed ? 'sites.deletedPartial' : 'sites.deleted'),
      );
      await qc.invalidateQueries({ queryKey: ['sites'] });
      await qc.invalidateQueries({ queryKey: ['domains'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  return (
    <Page>
      <PageHeader
        title={t('sites.title')}
        actions={
          <Dialog open={open} onOpenChange={setOpen}>
            <DialogTrigger asChild>
              <Button>
                <Plus className="h-4 w-4" /> {t('sites.newSite')}
              </Button>
            </DialogTrigger>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>{t('sites.newSite')}</DialogTitle>
              </DialogHeader>
              <div className="space-y-3">
                <div className="space-y-1.5">
                  <Label>{t('common.name')}</Label>
                  <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Marketing site" />
                </div>
                <div className="space-y-1.5">
                  <Label>{t('sites.renderMode')}</Label>
                  <Select value={mode} onValueChange={setMode}>
                    <SelectTrigger>
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="StaticPrerender">{t('sites.modeStaticPrerender')}</SelectItem>
                      <SelectItem value="ReactApp">{t('sites.modeReactApp')}</SelectItem>
                      <SelectItem value="StaticFiles">{t('sites.modeStaticFiles')}</SelectItem>
                    </SelectContent>
                  </Select>
                </div>
              </div>
              <DialogFooter>
                <Button variant="outline" onClick={() => setOpen(false)}>
                  {t('actions.cancel')}
                </Button>
                <Button disabled={!name.trim() || create.isPending} onClick={() => create.mutate()}>
                  {t('actions.create')}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        }
      />

      {sites.isLoading ? (
        <CenteredSpinner />
      ) : sites.data && sites.data.length > 0 ? (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {sites.data.map((s) => (
            <div key={s.id} className="group relative">
              <Link to="/sites/$siteId" params={{ siteId: s.id }}>
                <Card className="h-full p-5 transition-all hover:-translate-y-0.5 hover:shadow-md">
                  <div className="mb-3 flex h-10 w-10 items-center justify-center rounded-lg bg-accent text-accent-foreground">
                    <PanelsTopLeft className="h-5 w-5" />
                  </div>
                  <p className="font-medium">{s.name}</p>
                  <div className="mt-2 flex items-center gap-2">
                    <Badge tone="secondary">{s.renderMode}</Badge>
                    <Badge tone={s.activeBuildId ? 'success' : 'warning'}>
                      {s.activeBuildId ? t('content.published') : t('content.draft')}
                    </Badge>
                  </div>
                </Card>
              </Link>
              {/*
                Outside the Link, not inside it: nesting a button in an anchor makes
                every click navigate as well as act. Sits above the card and is
                revealed on hover/focus so the grid stays calm.
              */}
              <Button
                size="icon"
                variant="ghost"
                title={t('sites.delete')}
                aria-label={t('sites.delete')}
                className="absolute right-2 top-2 opacity-0 transition-opacity focus-visible:opacity-100 group-hover:opacity-100"
                onClick={() => setPendingDelete(s)}
              >
                <Trash2 className="h-4 w-4" />
              </Button>
            </div>
          ))}
        </div>
      ) : (
        <EmptyState
          icon={PanelsTopLeft}
          title={t('sites.title')}
          description={t('app.tagline')}
          action={
            <Button onClick={() => setOpen(true)}>
              <Plus className="h-4 w-4" /> {t('sites.newSite')}
            </Button>
          }
        />
      )}

      <ConfirmDeleteDialog
        open={pendingDelete !== null}
        onOpenChange={(next) => {
          if (!next) setPendingDelete(null);
        }}
        title={t('sites.deleteTitle', { name: pendingDelete?.name ?? '' })}
        description={t('sites.deleteDescription')}
        consequences={[
          t('sites.deleteConsequenceBuilds'),
          t('sites.deleteConsequenceDomains'),
          ...(pendingDelete?.renderMode === 'StaticFiles' ? [] : [t('sites.deleteConsequenceRepo')]),
        ]}
        confirmationValue={pendingDelete?.name ?? ''}
        confirmLabel={t('sites.delete')}
        pending={remove.isPending}
        onConfirm={() => pendingDelete && remove.mutate(pendingDelete.id)}
      />
    </Page>
  );
}
