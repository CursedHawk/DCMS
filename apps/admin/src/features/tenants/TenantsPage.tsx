import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Boxes, Plus } from 'lucide-react';
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
import { CenteredSpinner } from '../../components/ui/spinner';
import { TBody, TD, TH, THead, TR, Table } from '../../components/ui/table';
import { ApiError, api } from '../../lib/api';
import { setCurrentTenantSlug } from '../../tenants';

interface TenantRow {
  tenantId: string;
  slug: string;
  name: string;
  status?: string;
}

export function TenantsPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const [slug, setSlug] = useState('');
  const [name, setName] = useState('');

  const tenants = useQuery({
    queryKey: ['tenants'],
    queryFn: () => api.get<TenantRow[]>('/admin/tenants'),
  });

  const create = useMutation({
    mutationFn: () => api.post<{ slug: string }>('/admin/tenants', { slug: slug.trim(), name: name.trim() || slug.trim() }),
    onSuccess: async () => {
      setCurrentTenantSlug(slug.trim());
      toast.success(t('common.saved'));
      setOpen(false);
      setSlug('');
      setName('');
      await qc.invalidateQueries({ queryKey: ['tenants'] });
      await qc.invalidateQueries({ queryKey: ['me-tenants'] });
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : t('errors.generic')),
  });

  return (
    <Page>
      <PageHeader
        title={t('nav.tenants')}
        description={t('tenant.selectFirst')}
        actions={
          <Dialog open={open} onOpenChange={setOpen}>
            <DialogTrigger asChild>
              <Button>
                <Plus className="h-4 w-4" /> {t('tenant.createTenant')}
              </Button>
            </DialogTrigger>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>{t('tenant.createTenant')}</DialogTitle>
              </DialogHeader>
              <div className="space-y-3">
                <div className="space-y-1.5">
                  <Label>{t('common.slug')}</Label>
                  <Input value={slug} onChange={(e) => setSlug(e.target.value)} placeholder="acme-co" />
                </div>
                <div className="space-y-1.5">
                  <Label>{t('tenant.displayName')}</Label>
                  <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Acme Co." />
                </div>
              </div>
              <DialogFooter>
                <Button variant="outline" onClick={() => setOpen(false)}>
                  {t('actions.cancel')}
                </Button>
                <Button disabled={!slug.trim() || create.isPending} onClick={() => create.mutate()}>
                  {t('actions.create')}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        }
      />

      {tenants.isLoading ? (
        <CenteredSpinner />
      ) : tenants.isError ? (
        <EmptyState icon={Boxes} title={t('tenant.superAdminOnly')} />
      ) : tenants.data && tenants.data.length > 0 ? (
        <Table>
          <THead>
            <TR>
              <TH>{t('common.name')}</TH>
              <TH>{t('common.slug')}</TH>
              <TH>{t('common.status')}</TH>
              <TH className="text-right">{t('common.actions')}</TH>
            </TR>
          </THead>
          <TBody>
            {tenants.data.map((tn) => (
              <TR key={tn.tenantId}>
                <TD className="font-medium">{tn.name}</TD>
                <TD className="text-muted-foreground">{tn.slug}</TD>
                <TD>
                  <Badge tone="secondary">{tn.status ?? '—'}</Badge>
                </TD>
                <TD className="text-right">
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => {
                      setCurrentTenantSlug(tn.slug);
                      window.location.reload();
                    }}
                  >
                    {t('tenant.use')}
                  </Button>
                </TD>
              </TR>
            ))}
          </TBody>
        </Table>
      ) : (
        <EmptyState icon={Boxes} title={t('common.noResults')} />
      )}
    </Page>
  );
}
