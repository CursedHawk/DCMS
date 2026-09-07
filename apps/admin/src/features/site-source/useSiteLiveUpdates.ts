import type { HubConnection } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { getAccessToken } from '../../auth';
import { runtimeConfig } from '../../runtime-config';
import { getCurrentTenantSlug } from '../../tenants';
import type { GitBuild } from './git';

export interface BuildUpdate extends GitBuild {
  siteId: string;
  actorUserId: string | null;
}

export interface DraftUpdate {
  siteId: string;
  branch: string;
  version: number;
  paths: string[];
  actorUserId: string | null;
  occurredAt: string;
}

export interface CommitUpdate {
  siteId: string;
  branch: string;
  sha: string;
  shortSha: string;
  message: string | null;
  author: string | null;
  actorUserId: string | null;
  occurredAt: string;
}

/**
 * Keeps a site's workspace live: its deployments and the commits landing on its branches.
 *
 * Two things were broken and this fixes both.
 *
 * The first is that the IDE could not see its own publish. Publishing from a feature branch
 * merges into `release`, and the build that follows is created asynchronously by the push
 * webhook — *after* the merge request has already returned. The Deployments panel invalidated
 * its query at that moment, got back a list that did not yet contain the new build, concluded
 * that nothing was in flight, and therefore did not start polling. The publish ran to
 * completion with the panel showing the previous deployment the whole way.
 *
 * The second is that two people on one site knew nothing about each other. A colleague's
 * publish, or their commit to the branch you have open in the editor, was invisible until you
 * reloaded — and on the branch you are editing that is not a nicety: your draft is now based on
 * a commit that is no longer HEAD, and the first you would hear of it was a 409 when you tried
 * to commit your own work.
 *
 * Everything that arrives is treated as a hint, never as truth: each message writes what it
 * knows into the cache so the panel moves immediately, and then invalidates so the authoritative
 * REST read — the one with the permission checks and the tenant filter behind it — settles it.
 * A hub that is down costs liveness and nothing else; the panel's own polling still runs.
 */
export function useSiteLiveUpdates({
  siteId,
  branch,
  myUserId,
  onCommit,
  onDraftChanged,
  draftVersion,
}: {
  siteId: string;
  /** The branch this user is editing, so a commit onto it can be called out specifically. */
  branch?: string;
  myUserId?: string;
  /** Called for a commit somebody ELSE made to the branch this user is editing. */
  onCommit?: (commit: CommitUpdate) => void;
  /**
   * Called when this account's working draft was written from somewhere else — a second tab,
   * or the AI agent. See the `DraftChanged` handler for why this is not "somebody else".
   */
  onDraftChanged?: (draft: DraftUpdate) => void;
  /** The draft version this editor currently holds, for suppressing the echo of its own saves. */
  draftVersion?: number;
}) {
  const qc = useQueryClient();
  const { t } = useTranslation();
  const [connected, setConnected] = useState(false);
  const connRef = useRef<HubConnection | null>(null);

  // Read through refs inside the socket handlers. Re-creating the connection whenever the
  // branch changes or a callback identity changes would drop and reopen the WebSocket on
  // every keystroke-driven render, which is the one thing a live connection must not do.
  const branchRef = useRef(branch);
  branchRef.current = branch;
  const myUserIdRef = useRef(myUserId);
  myUserIdRef.current = myUserId;
  const onCommitRef = useRef(onCommit);
  onCommitRef.current = onCommit;
  const onDraftChangedRef = useRef(onDraftChanged);
  onDraftChangedRef.current = onDraftChanged;
  const draftVersionRef = useRef(draftVersion);
  draftVersionRef.current = draftVersion;
  const tRef = useRef(t);
  tRef.current = t;

  // Commits shas already announced on this connection.
  //
  // One commit can legitimately arrive twice: publishing announces the merge it just made, and
  // Forgejo's push webhook announces the same commit again a moment later when it lands on
  // `release`. Both are correct — neither knows about the other — so the client is where the
  // two become one piece of news. Keyed by sha, which is exactly the identity of "the same
  // commit", and bounded so a long editing session cannot grow it without limit.
  const seenCommits = useRef(new Set<string>());

  const slug = getCurrentTenantSlug();

  useEffect(() => {
    if (!siteId || !slug) return;
    let disposed = false;

    void (async () => {
      // Lazily imported for the same reason the notification hub is: the signalr chunk is
      // ~55 KB and the IDE route is already the heaviest in the app.
      const { HttpTransportType, HubConnectionBuilder, LogLevel } = await import('@microsoft/signalr');
      if (disposed) return;

      const connection = new HubConnectionBuilder()
        // skipNegotiation + WebSockets, matching the notification hub: nothing at the edge
        // pins the negotiate POST and the transport connect to the same admin-api replica,
        // so a scaled deployment would otherwise drop connections at random.
        .withUrl(
          `${runtimeConfig.adminApiBase}/hub/sites?tenant=${encodeURIComponent(slug)}&siteId=${encodeURIComponent(siteId)}`,
          {
            accessTokenFactory: async () => (await getAccessToken()) ?? '',
            skipNegotiation: true,
            transport: HttpTransportType.WebSockets,
          },
        )
        .withAutomaticReconnect()
        .configureLogging(LogLevel.Warning)
        .build();

      connection.on('BuildChanged', (update: BuildUpdate) => {
        if (update.siteId !== siteId) return;
        mergeBuild(qc, siteId, update);
        void qc.invalidateQueries({ queryKey: ['site-builds', siteId] });
        // A finished build changes which one is live, so the site row is stale too.
        if (update.status === 'Succeeded') void qc.invalidateQueries({ queryKey: ['site', siteId] });

        // Toast only for somebody else's build. Your own publish already told you it was
        // queued, and saying so twice is how a useful notification becomes noise.
        if (update.actorUserId && update.actorUserId === myUserIdRef.current) return;
        if (update.status === 'Queued') toast.info(tRef.current('ide.deploy.liveStarted'));
        else if (update.status === 'Succeeded') toast.success(tRef.current('ide.deploy.liveSucceeded'));
        else if (update.status === 'Failed') toast.error(tRef.current('ide.deploy.liveFailed'));
      });

      /*
       * Somebody wrote to a working draft.
       *
       * A draft is per account and per branch, so unlike a commit this is only ever about
       * YOUR OWN draft — the interesting cases are a second tab and the AI agent, both of
       * which save through the same endpoint. Anyone else's draft is none of this editor's
       * business, so a message naming a different actor is dropped.
       *
       * The version check is the echo suppressor. This editor's own saves come back over the
       * hub like anyone's, and a banner reading "your draft changed elsewhere" every time you
       * stop typing would be worse than the problem it was added to solve. Anything at or
       * below the version already held is this editor's own work arriving back.
       */
      connection.on('DraftChanged', (draft: DraftUpdate) => {
        if (draft.siteId !== siteId) return;
        if (branchRef.current && draft.branch !== branchRef.current) return;
        if (!draft.actorUserId || draft.actorUserId !== myUserIdRef.current) return;
        if (draftVersionRef.current !== undefined && draft.version <= draftVersionRef.current) return;
        onDraftChangedRef.current?.(draft);
      });

      connection.on('CommitPushed', (commit: CommitUpdate) => {
        if (commit.siteId !== siteId) return;
        if (seenCommits.current.has(commit.sha)) return;
        if (seenCommits.current.size >= 200) seenCommits.current.clear();
        seenCommits.current.add(commit.sha);

        void qc.invalidateQueries({ queryKey: ['git-history', siteId] });
        void qc.invalidateQueries({ queryKey: ['git-branches', siteId] });

        const mine = !!commit.actorUserId && commit.actorUserId === myUserIdRef.current;
        if (mine) return;

        if (commit.branch === branchRef.current) {
          // The branch under this user's editor moved. Their draft is now based on an older
          // commit, so this is a warning, not a note — and the caller gets it too, so the page
          // can offer to reload rather than leaving them to discover it at commit time.
          void qc.invalidateQueries({ queryKey: ['git-changes', siteId] });
          toast.warning(
            tRef.current('ide.git.liveBranchMoved', {
              branch: commit.branch,
              author: commit.author ?? tRef.current('ide.git.someone'),
            }),
          );
          onCommitRef.current?.(commit);
        } else {
          toast.info(
            tRef.current('ide.git.liveCommitted', {
              branch: commit.branch,
              author: commit.author ?? tRef.current('ide.git.someone'),
            }),
          );
        }
      });

      connection.onreconnected(() => {
        setConnected(true);
        // Anything that happened while the socket was down never arrived. Refetch rather than
        // leaving a panel confidently showing a build that finished ten minutes ago.
        void qc.invalidateQueries({ queryKey: ['site-builds', siteId] });
        void qc.invalidateQueries({ queryKey: ['git-history', siteId] });
      });
      connection.onclose(() => setConnected(false));

      try {
        await connection.start();
        if (disposed) {
          void connection.stop();
          return;
        }
        connRef.current = connection;
        setConnected(true);
      } catch (err) {
        // A workspace that cannot reach the hub is a less live workspace, not a broken one:
        // the panel still polls and the REST reads still work. Log rather than toast.
        console.warn('Site hub connection failed.', err);
      }
    })();

    return () => {
      disposed = true;
      void connRef.current?.stop();
      connRef.current = null;
    };
  }, [siteId, slug, qc]);

  return { connected };
}

/**
 * Writes an update into the cached build list so the panel moves on the message rather than on
 * the refetch that follows it. Newest-first ordering is preserved, and an id already present is
 * replaced rather than appended — a build is one row whose status changes, not three rows.
 */
function mergeBuild(
  qc: ReturnType<typeof useQueryClient>,
  siteId: string,
  update: BuildUpdate,
): void {
  qc.setQueryData<GitBuild[]>(['site-builds', siteId], (current) => {
    if (!current) return current;
    const { siteId: _siteId, actorUserId: _actor, ...build } = update;
    const index = current.findIndex((b) => b.id === build.id);
    if (index === -1) return [build, ...current];
    const next = [...current];
    next[index] = { ...next[index], ...build };
    return next;
  });
}
