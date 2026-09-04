import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { BadgeCheck, Globe, Link2, Plus, ShieldAlert, Star, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CenteredSpinner,
  CopyButton,
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
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
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  toastApiError,
} from '@dcms/admin-ui';
import { api } from '../../lib/api';
interface Domain {
  id: string;
  hostname: string;
  verified: boolean;
  isPrimary: boolean;
  managed: boolean;
  /** The site served on this hostname, or null when the domain is not linked yet. */
  siteId: string | null;
  /** Resolved server-side: listing sites needs SiteEdit, managing domains does not. */
  siteName: string | null;
  txtRecord: string;
  txtValue: string;
}

/*
 * Radix Select rejects an empty string as an item value (it reserves "" for
 * "nothing selected"), so unlinking needs a sentinel that maps back to null.
 */
const UNLINKED = '__unlinked';
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
    onError: (e) => toastApiError(e, t),
  });

  const provision = useMutation({
    mutationFn: () => api.post('/admin/domains/provisioned'),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      setOpen(false);
      await qc.invalidateQueries({ queryKey: ['domains'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const verify = useMutation({
    mutationFn: (id: string) => api.post<{ verified: boolean }>(`/admin/domains/${id}/verify`),
    onSuccess: async (r) => {
      if (r.verified) toast.success(t('domains.verified'));
      await qc.invalidateQueries({ queryKey: ['domains'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const linkSite = useMutation({
    mutationFn: ({ id, siteId }: { id: string; siteId: string | null }) =>
      api.post(`/admin/domains/${id}/site`, { siteId }),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      await qc.invalidateQueries({ queryKey: ['domains'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const setPrimary = useMutation({
    mutationFn: (id: string) => api.post(`/admin/domains/${id}/primary`),
    onSuccess: async () => {
      toast.success(t('domains.primarySet'));
      await qc.invalidateQueries({ queryKey: ['domains'] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const remove = useMutation({
    mutationFn: (id: string) => api.del(`/admin/domains/${id}`),
    onSuccess: async () => {
      toast.success(t('domains.removed'));
      await qc.invalidateQueries({ queryKey: ['domains'] });
    },
    onError: (e) => toastApiError(e, t),
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
                  {/*
                    Only offered where it can succeed: a domain that serves no site
                    has nothing to be canonical for, and the server rejects it.
                  */}
                  {d.verified && d.siteId && !d.isPrimary ? (
                    <Button
                      size="sm"
                      variant="outline"
                      title={t('domains.primaryHint')}
                      disabled={setPrimary.isPending}
                      onClick={() => setPrimary.mutate(d.id)}
                    >
                      <Star className="h-4 w-4" /> {t('domains.makePrimary')}
                    </Button>
                  ) : null}
                  <Button
                    size="icon"
                    variant="ghost"
                    title={t('domains.remove')}
                    aria-label={t('domains.remove')}
                    disabled={remove.isPending}
                    onClick={() => {
                      if (window.confirm(t('domains.removeConfirm', { hostname: d.hostname }))) {
                        remove.mutate(d.id);
                      }
                    }}
                  >
                    <Trash2 className="h-4 w-4" />
                  </Button>
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
                  <div className="flex flex-wrap items-center gap-2">
                    <Link2 className="h-4 w-4 text-muted-foreground" />
                    <span className="text-sm text-muted-foreground">{t('domains.linkSite')}:</span>
                    {/*
                      Controlled by siteId, so the row states which site the domain
                      actually points at — the select used to be uncontrolled and always
                      read as an empty "pick a site" prompt however it was linked.

                      Listing sites needs SiteEdit. Without it there is nothing to pick
                      from, so fall back to the name the domains endpoint resolved.
                    */}
                    {sites.data ? (
                      <Select
                        value={d.siteId ?? UNLINKED}
                        onValueChange={(value) =>
                          linkSite.mutate({ id: d.id, siteId: value === UNLINKED ? null : value })
                        }
                      >
                        <SelectTrigger className="w-60">
                          <SelectValue placeholder={t('domains.notLinked')} />
                        </SelectTrigger>
                        <SelectContent>
                          <SelectItem value={UNLINKED}>{t('domains.unlink')}</SelectItem>
                          {sites.data.map((site) => (
                            <SelectItem key={site.id} value={site.id}>
                              {site.name}
                            </SelectItem>
                          ))}
                        </SelectContent>
                      </Select>
                    ) : (
                      <span className="text-sm">{d.siteName ?? t('domains.notLinked')}</span>
                    )}
                    {/* Linked, but the site itself is gone — say so rather than blank. */}
                    {d.siteId && sites.data && !d.siteName ? (
                      <span className="text-sm text-destructive">{t('domains.siteMissing')}</span>
                    ) : null}
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
