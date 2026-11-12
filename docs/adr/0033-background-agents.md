# ADR 0033: Background agents

**Status:** accepted (2026-11-12) · kanban epic #632 · amends D1 and D4 of [`docs/ai-agent.md`](../ai-agent.md)

## Context

The trigger was a Mode B site agent conversation on 2026-11-12: "Create HR portal…", 230 messages across 5 runs. It went wrong in four ways.

1. **Reads never saw the run's own edits.**
   - The session took one snapshot of the workspace when a run started. `read_file`, `search` and `check_build` read that snapshot; edits went to the live store.
   - So the model read a 743-line App.tsx while editing a 470-line one.
   - It deleted lines by stale numbers twice and cut the file in half.
2. **Two loops ran on one conversation.**
   - The agent loop was a React hook inside the editor's panel, and nothing aborted it when the panel unmounted.
   - The orphaned loop kept running with nothing on screen to show or stop it.
   - A remounted panel resumed the stored conversation and started a second loop. Runs 2 and 3 overlapped from 12:50 to 12:53 and interleaved one transcript.
   - After a site switch, an orphan wrote into the editor store, which holds one site at a time: **another site's** files. Autosave then flushed them into that site's draft.
3. **A run that reached its 25-turn ceiling recorded itself as `completed`.**
   - The work was half done and never validated.
   - The person had to type "continue" and discover the damage themselves.
4. **`git_status` / `git_diff` reported "No changes" right after edits.**
   - The run held autosave, so the server draft lagged behind.
   - The model concluded its work was lost and redid it as new files.

Separately, the person wanted the opposite of "abort on unmount". They wanted agents that keep working while they go elsewhere in the console, agents on different sites at the same time, and every run visible and steerable from one place. They also wanted the console assistant to be able to ask for site changes.

## Decision

1. **Runs live in an agent host, not in a component.**
   - The host is a module-level store that lasts as long as the tab (`features/agent/hostStore.ts` for state and commands, `host.ts` for the run itself).
   - The editor's panel and the dock's Agents tab are views of it.
   - D1 holds: the loop is in the browser, and **closing the tab ends every run**. The tab asks first while one is running, as it does for unsaved work.
   - Navigating anywhere inside the console does not end a run.
2. **One active run per site (and branch); runs on different sites are independent.**
   - Anything else asked of a busy site queues behind the current run.
   - This is what D4 now means: not a swarm on one site, but one agent per site.
3. **A run works on its own copy of its site** (`features/agent/siteDraft.ts`).
   - **Seeding:** from the editor's store, after the editor saves what is pending, when the editor holds that site. Otherwise from the draft API.
   - **Saving:** the run saves its own writes through the same draft API, with `origin: 'agent'` and a client id of `<tab id>:agent:<run id>`. Writes are debounced and serialised.
   - **Merging back:** the run merges what it saved into the editor's store (`applyAgentWrites`) only while that store holds its site. The tab treats the hub echo of those saves as its own.
   - **The author's text:** reads take the author's latest text for every path the run is not itself writing.
   - **Conflicts:** a 409 is per file. The contested file takes the other version, and the model is told on its next tool result.
   - **Each tool call gets a fresh workspace,** so a run always reads what it wrote.
   - **Autosave hold removed:** the editor's autosave no longer pauses while a run writes. `VfsState.agentRuns` is gone.
4. **Checks belong to the run, not the page** (`features/agent/runChecks.ts`).
   - **Build:** each run starts its own bundler worker for `check_build` and the gate.
   - **Types:** a TypeScript language-service worker (`typecheck.worker.ts`) checks the run's files with the IDE's compiler options and palette types.
   - **Why:** Monaco's worker only knew the open editor's models. A run off screen got an empty, clean-looking program.
   - **Unavailable checks:** a bundler that cannot start reports "unavailable", read as not checked.
   - **Preview tools:** these answer only while the open preview shows the run's own site.
5. **Each kind's tools and gate are decided from the kind alone** (`features/agent/siteKits.ts`), not assembled by the page. A run started from an editor and one delegated by the assistant get the same agent.
6. **The turn ceiling ends a run as `paused`, not `completed`.**
   - Before pausing, the run validates what it wrote. A failing build is left in the transcript for "continue" to start from.
   - The run record stores it as `stopped`, the server's word for a run that did not finish, so the backend is unchanged.
7. **The console assistant never edits a site.**
   - It asks the site's own agent with `request_site_change` and follows the request with `site_change_status`.
   - Asking is `dangerous` risk, so it takes one approval.
   - The request runs in its own conversation on the site's IDE surface.
   - These tools are deliberately not in `ASSISTANT_TOOLS`, which site agents also receive: an agent able to delegate to agents is a loop.
8. **The assistant has one session per tab** (`features/assistant/session.tsx`).
   - The dock and the full page are views of it. "Open full view" used to start a second loop on the same conversation, and leaving the full page orphaned its loop.
9. **Visibility:**
   - The assistant button (topbar and focus-mode header) carries a spinning ring while the assistant or any site agent works, and a dot while one waits for a decision.
   - The dock has an Agents tab listing every site agent in the tab: site, kind, task, status, transcript, its pending approval, Stop, and Open in editor.
   - Each editor's agent rail icon shows its own site's state.

## Consequences

- A run survives navigation but not a reload or a closed tab. The transcript and the open run record survive as before.
- **Rejected:** a server-side loop, which would also survive a closed tab. It needs three things DCMS does not have:
  - delegated user authority for tool calls (ADR 0032's agent grants, paused);
  - a type-check and build of a draft that does not deploy;
  - a port of the runtime.
- Delegated requests live in the tab: `site_change_status` cannot see a request from before a reload.
- The type-check worker is about 6.6 MB (TypeScript plus its lib files). It loads only when a run type-checks, and Monaco's own TS worker is the same size.
- Two agents on one draft are prevented in one tab, not across tabs. Per-file base hashes still keep two tabs from overwriting each other's text.
