import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { PanelsTopLeft, Plus } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Page, PageHeader } from '../../components/Page';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '../../components/ui/dialog';
import { EmptyState } from '../../components/ui/empty-state';
import { Input } from '../../components/ui/input';
import { Label } from '../../components/ui/label';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '../../components/ui/select';
import { CenteredSpinner } from '../../components/ui/spinner';
import { Card } from '../../components/ui/card';
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
                      <SelectItem value="StaticPrerender">Static prerender</SelectItem>
                      <SelectItem value="ReactApp">React app</SelectItem>
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
            <Link key={s.id} to="/sites/$siteId" params={{ siteId: s.id }}>
              <Card className="group h-full p-5 transition-all hover:-translate-y-0.5 hover:shadow-md">
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
    </Page>
  );
}
