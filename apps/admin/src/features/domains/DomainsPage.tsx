import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { BadgeCheck, Globe, Link2, Plus, ShieldAlert } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { CopyButton } from '../../components/CopyButton';
import { Page, PageHeader } from '../../components/Page';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Card, CardContent } from '../../components/ui/card';
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
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
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../../components/ui/tabs';
import { ApiError, api } from '../../lib/api';

interface Domain {
  id: string;
  hostname: string;
  verified: boolean;
  isPrimary: boolean;
  managed: boolean;
  txtRecord: string;
  txtValue: string;
}
interface SiteSummary {
  id: string;
  name: string;
}

export function DomainsPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const domains = useQuery({ queryKey: ['domains'], queryFn: () => api.get<Domain[]>('/admin/domains') });
  const sites = useQuery({ queryKey: ['sites'], queryFn: () => api.get<SiteSummary[]>('/admin/sites') });
  const [open, setOpen] = useState(false);
  const [hostname, setHostname] = useState('');

  const add = useMutation({
    mutationFn: () => api.post('/admin/domains', { hostname: hostname.trim() }),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      setOpen(false);
      setHostname('');
      await qc.invalidateQueries({ queryKey: ['domains'] });
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : t('errors.generic')),
  });

  const provision = useMutation({
    mutationFn: () => api.post('/admin/domains/provisioned'),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      setOpen(false);
      await qc.invalidateQueries({ queryKey: ['domains'] });
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : t('errors.generic')),
  });

  const verify = useMutation({
    mutationFn: (id: string) => api.post<{ verified: boolean }>(`/admin/domains/${id}/verify`),
    onSuccess: async (r) => {
      if (r.verified) toast.success(t('domains.verified'));
      await qc.invalidateQueries({ queryKey: ['domains'] });
    },
    onError: (e) => toast.error(e instanceof ApiError ? e.message : t('errors.generic')),
  });

  const linkSite = useMutation({
    mutationFn: ({ id, siteId }: { id: string; siteId: string }) =>
      api.post(`/admin/domains/${id}/site`, { siteId }),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      await qc.invalidateQueries({ queryKey: ['domains'] });
    },
    onError: () => toast.error(t('errors.generic')),
  });

  return (
    <Page>
      <PageHeader
        title={t('domains.title')}
        actions={
          <Button onClick={() => setOpen(true)}>
            <Plus className="h-4 w-4" /> {t('domains.addDomain')}
          </Button>
        }
      />

      {domains.isLoading ? (
        <CenteredSpinner />
      ) : domains.data && domains.data.length > 0 ? (
        <div className="space-y-4">
          {domains.data.map((d) => (
            <Card key={d.id}>
              <CardContent className="space-y-4 p-5">
                <div className="flex flex-wrap items-center gap-2">
                  <Globe className="h-4 w-4 text-muted-foreground" />
                  <span className="font-medium">{d.hostname}</span>
                  {d.verified ? (
                    <Badge tone="success">
                      <BadgeCheck className="h-3 w-3" /> {t('domains.verified')}
                    </Badge>
                  ) : (
                    <Badge tone="warning">
                      <ShieldAlert className="h-3 w-3" /> {t('domains.pending')}
                    </Badge>
                  )}
                  {d.isPrimary ? <Badge tone="default">{t('domains.primary')}</Badge> : null}
                  {d.managed ? <Badge tone="secondary">{t('domains.providedBadge')}</Badge> : null}
                  <div className="flex-1" />
                  {!d.verified ? (
                    <Button size="sm" disabled={verify.isPending} onClick={() => verify.mutate(d.id)}>
                      {t('actions.verify')}
                    </Button>
                  ) : null}
                </div>

                {!d.verified ? (
                  <div className="rounded-md border bg-muted/30 p-3 text-sm">
                    <p className="mb-2 text-muted-foreground">{t('domains.txtRecordHint')}</p>
                    <div className="grid gap-2">
                      <Field label={t('domains.recordName')} value={d.txtRecord} />
                      <Field label={t('domains.recordValue')} value={d.txtValue} />
                    </div>
                  </div>
                ) : (
                  <div className="flex items-center gap-2">
                    <Link2 className="h-4 w-4 text-muted-foreground" />
                    <span className="text-sm text-muted-foreground">{t('domains.linkSite')}:</span>
                    <Select onValueChange={(siteId) => linkSite.mutate({ id: d.id, siteId })}>
                      <SelectTrigger className="w-60">
                        <SelectValue placeholder={t('sites.title')} />
                      </SelectTrigger>
                      <SelectContent>
                        {(sites.data ?? []).map((s) => (
                          <SelectItem key={s.id} value={s.id}>
                            {s.name}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </div>
                )}
              </CardContent>
            </Card>
          ))}
        </div>
      ) : (
        <EmptyState
          icon={Globe}
          title={t('domains.title')}
          action={
            <Button onClick={() => setOpen(true)}>
              <Plus className="h-4 w-4" /> {t('domains.addDomain')}
            </Button>
          }
        />
      )}

      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('domains.addDomain')}</DialogTitle>
          </DialogHeader>
          <Tabs defaultValue="provided">
            <TabsList className="w-full">
              <TabsTrigger value="provided" className="flex-1">
                {t('domains.provided')}
              </TabsTrigger>
              <TabsTrigger value="custom" className="flex-1">
                {t('domains.custom')}
              </TabsTrigger>
            </TabsList>

            <TabsContent value="provided" className="space-y-4">
              <p className="text-sm text-muted-foreground">{t('domains.providedHint')}</p>
              <DialogFooter>
                <Button variant="outline" onClick={() => setOpen(false)}>
                  {t('actions.cancel')}
                </Button>
                <Button disabled={provision.isPending} onClick={() => provision.mutate()}>
                  <Globe className="h-4 w-4" /> {t('domains.getSubdomain')}
                </Button>
              </DialogFooter>
            </TabsContent>

            <TabsContent value="custom" className="space-y-4">
              <div className="space-y-1.5">
                <Label>{t('domains.hostname')}</Label>
                <Input
                  value={hostname}
                  onChange={(e) => setHostname(e.target.value)}
                  placeholder="www.example.com"
                />
              </div>
              <DialogFooter>
                <Button variant="outline" onClick={() => setOpen(false)}>
                  {t('actions.cancel')}
                </Button>
                <Button disabled={!hostname.trim() || add.isPending} onClick={() => add.mutate()}>
                  {t('actions.add')}
                </Button>
              </DialogFooter>
            </TabsContent>
          </Tabs>
        </DialogContent>
      </Dialog>
    </Page>
  );
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-center gap-2">
      <span className="w-28 shrink-0 text-xs font-medium text-muted-foreground">{label}</span>
      <code className="flex-1 overflow-x-auto rounded bg-background px-2 py-1 text-xs">{value}</code>
      <CopyButton value={value} />
    </div>
  );
}
