import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { GitBranch, Globe, Plus, RotateCw, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Button,
  HostLink,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  cn,
  toastApiError,
  useCan,
} from '@dcms/ui';
import { api } from '../../lib/api';
import { Perm } from '../../lib/permissions';
import { RELEASE_BRANCH } from './constants';
import { gitApi } from './git';

/**
 * Where each branch of the site is live (#277): release on the site's domains, and every branch
 * added here on its own — a developer's `dev` on dev.example.com. A commit to one of them deploys
 * it. Adding and removing takes Publish sites; a subdomain for a branch takes Manage domains.
 */
export function PublishBranchesPanel({ siteId }: { siteId: string }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const canPublish = useCan(Perm.SitePublish);
  const canDomains = useCan(Perm.DomainsManage);
  const [picked, setPicked] = useState('');

  const targets = useQuery({
    queryKey: ['publish-branches', siteId],
    queryFn: () => gitApi.publishBranches(siteId),
  });
  const branches = useQuery({
    queryKey: ['git-branches', siteId],
    queryFn: () => gitApi.branches(siteId),
    enabled: canPublish,
  });

  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ['publish-branches', siteId] });
    void qc.invalidateQueries({ queryKey: ['site-builds', siteId] });
    void qc.invalidateQueries({ queryKey: ['domains'] });
  };
  const add = useMutation({
    mutationFn: (branch: string) => gitApi.addPublishBranch(siteId, branch),
    onSuccess: () => {
      setPicked('');
      toast.success(t('ide.deploy.branchAdded'));
      refresh();
    },
    onError: (e) => toastApiError(e, t),
  });
  const remove = useMutation({
    mutationFn: (branch: string) => gitApi.removePublishBranch(siteId, branch),
    onSuccess: refresh,
    onError: (e) => toastApiError(e, t),
  });
  const rebuild = useMutation({
    mutationFn: (branch: string) => gitApi.rebuild(siteId, branch),
    onSuccess: () => {
      toast.success(t('ide.git.rebuildQueued'));
      refresh();
    },
    onError: (e) => toastApiError(e, t),
  });
  // One click, no DNS: a platform subdomain, linked to the branch.
  const subdomain = useMutation({
    mutationFn: async (branch: string) => {
      const { id } = await api.post<{ id: string }>('/admin/domains/provisioned');
      await api.post(`/admin/domains/${id}/site`, { siteId, branch });
    },
    onSuccess: refresh,
    onError: (e) => toastApiError(e, t),
  });

  const rows = targets.data ?? [];
  const publishing = new Set(rows.map((r) => r.branch));
  const candidates = (branches.data ?? []).map((b) => b.name).filter((n) => n !== RELEASE_BRANCH && !publishing.has(n));

  return (
    <section className="shrink-0 border-b" aria-labelledby={`publish-branches-${siteId}`}>
      <h3
        id={`publish-branches-${siteId}`}
        className="px-2 pt-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground"
      >
        {t('ide.deploy.branchesTitle')}
      </h3>
      <ul className="py-1">
        {rows.map((row) => (
          <li key={row.branch} className="flex min-w-0 items-start gap-2 px-2 py-1.5">
            <GitBranch className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground" />
            <div className="min-w-0 flex-1">
              <p className="flex min-w-0 items-center gap-1.5 text-xs">
                <span className="min-w-0 font-medium [overflow-wrap:anywhere]">{row.branch}</span>
                {row.release && (
                  <span className="shrink-0 rounded bg-primary/10 px-1 py-px text-[9px] font-semibold uppercase tracking-wide text-primary">
                    {t('ide.git.production')}
                  </span>
                )}
              </p>
              {row.domains.length > 0 ? (
                <ul className="mt-0.5 space-y-0.5">
                  {row.domains.map((d) => (
                    <li key={d.id} className="flex min-w-0 items-center gap-1 text-[11px]">
                      <Globe className="h-3 w-3 shrink-0 text-muted-foreground" />
                      <HostLink host={d.hostname} className="min-w-0 [overflow-wrap:anywhere]" />
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="mt-0.5 text-[11px] text-muted-foreground">{t('ide.deploy.noDomain')}</p>
              )}
              {!row.release && row.domains.length === 0 && canDomains && (
                <button
                  type="button"
                  className="text-[11px] text-primary underline-offset-2 hover:underline disabled:opacity-50"
                  disabled={subdomain.isPending}
                  onClick={() => subdomain.mutate(row.branch)}
                >
                  {t('ide.deploy.addSubdomain')}
                </button>
              )}
            </div>
            {!row.release && canPublish && (
              <div className="flex shrink-0 items-center">
                <Button
                  size="icon"
                  variant="ghost"
                  className="h-6 w-6"
                  aria-label={t('ide.deploy.redeployBranch', { branch: row.branch })}
                  title={t('ide.deploy.redeployBranch', { branch: row.branch })}
                  disabled={rebuild.isPending}
                  onClick={() => rebuild.mutate(row.branch)}
                >
                  <RotateCw className={cn('h-3.5 w-3.5', rebuild.isPending && 'animate-spin')} />
                </Button>
                <Button
                  size="icon"
                  variant="ghost"
                  className="h-6 w-6"
                  aria-label={t('ide.deploy.removeBranch', { branch: row.branch })}
                  title={t('ide.deploy.removeBranch', { branch: row.branch })}
                  disabled={remove.isPending}
                  onClick={() => {
                    if (window.confirm(t('ide.deploy.removeBranchConfirm', { branch: row.branch })))
                      remove.mutate(row.branch);
                  }}
                >
                  <Trash2 className="h-3.5 w-3.5" />
                </Button>
              </div>
            )}
          </li>
        ))}
      </ul>
      {canPublish && (
        <div className="flex min-w-0 items-center gap-1.5 px-2 pb-2">
          <Select value={picked} onValueChange={setPicked} disabled={candidates.length === 0}>
            <SelectTrigger className="h-7 min-w-0 flex-1 text-xs" aria-label={t('ide.deploy.pickBranch')}>
              <SelectValue
                placeholder={candidates.length === 0 ? t('ide.deploy.noOtherBranches') : t('ide.deploy.pickBranch')}
              />
            </SelectTrigger>
            <SelectContent>
              {candidates.map((name) => (
                <SelectItem key={name} value={name}>
                  {name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Button size="sm" variant="outline" className="h-7" disabled={!picked || add.isPending} onClick={() => add.mutate(picked)}>
            <Plus className="h-3.5 w-3.5" /> {t('ide.deploy.publishBranch')}
          </Button>
        </div>
      )}
      {canPublish && <p className="px-2 pb-2 text-[10px] text-muted-foreground">{t('ide.deploy.branchesHint')}</p>}
    </section>
  );
}
