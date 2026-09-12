# DCMS AI Agent + IDE Rework — Plan & Progress

Living document. The plan is in **Phases → Steps**; every step carries a status box and an
acceptance criterion. The **Progress log** at the bottom records what actually happened, newest
first. Source brief: [`TODO/AI AGENT IDE REWORK.md`](TODO/AI%20AGENT%20IDE%20REWORK.md).

**Status legend:** `[ ]` not started · `[~]` in progress · `[x]` done · `[!]` blocked · `[-]` dropped (with reason)

**Overall:** Every phase has been worked; the runtime, the tooling, the IDE shell, persistence, live updates, telemetry, quota, injection posture, e2e and docs are all in. **One item is genuinely still open, and it should not be read as done:**

- **6.6 — blocked, not pending.** The Phase 0 token baseline was never taken, so there is **no measured claim that any of this saved tokens**, and there will not be one until somebody runs it against a real billed key on a real site. Everything this document says about cost is a mechanism, never a measurement.

(10.3 split view declined, with reasons.)
**Tests:** 538 in `apps/admin` (was 204 at the start of this work), 196 in `packages/ui`, 322 in `Dcms.UnitTests`, 73 Playwright e2e, and 25 container-free endpoint-coverage tests in `Dcms.IntegrationTests` (permission + audit). All green; `tsc -b`, `pnpm lint` (0 errors) and `dotnet build` (0 warnings) clean. `Dcms.IntegrationTests` is green too — 366 passed, 1 skipped — **including the RLS change, which is now covered by a real test run rather than only by the startup `AssertCoverage` check**. It had been unrunnable here (and, from 2026-09-12, in CI) because MinIO stopped serving anonymous pulls from Docker Hub; the fixtures now pull the same images from quay.io.

---

## 1. Why

The Mode B IDE agent today is the naive shape the brief warns about: one browser-side loop, five
whole-file tools, the entire file list and the entire tenant OpenAPI glued into every system
prompt, no history, no search, no validation, no persistence. It works as a demo and gets
expensive and slow the moment a site is real.

The target is an agent that is **another client of the same workspace the human uses** — same
draft, same revisions, same conflict rules — reasoning on top of a genuinely capable workspace
API instead of being handed the project on every turn.

---

## 2. Current state (audit, 2026-09-11)

### What already exists and is worth keeping

| Area | Where | Notes |
| --- | --- | --- |
| VFS store | `apps/admin/src/features/site-source/vfs.ts` (500 ln) | Flat `files: Record<path,string>`, per-path `baseHashes`, `dirtyPaths` / `deletedPaths`, `takeDelta()` / `reconcile()`. Already granular — good bones. |
| Draft session / autosave | `.../site-source/useDraftSession.ts` | 1200 ms debounce, delta PATCH, 409 → hard pause. |
| Conflict text | `.../site-source/conflict.ts` + `ConflictResolver.tsx` | git-style markers, manual merge. Solid. |
| Live updates | `.../site-source/useSiteLiveUpdates.ts` | SignalR `BuildChanged` / `DraftChanged` / `CommitPushed` — the hub already exists. |
| Preview bundler | `.../ide/preview/bundler.worker.ts` (367 ln) | esbuild-wasm, in-browser, emits located errors + warnings. |
| Problems model | `.../ide/preview/problems.ts` | `BuildProblem` with file/line/column. Deterministic validation primitive, **not yet reachable by the agent**. |
| Workspace assistant | `apps/admin/src/features/assistant/*` (3.8k ln) | 4 modes (`read`/`careful`/`agent`/`auto`), risk table, 16 tenant tools, server-stored transcripts, approval cards. |
| Conversation store | `src/Services/Dcms.AdminApi/Ai/AiConversationEndpoints.cs` | Anthropic content blocks persisted; scopes `mine` / `workspace` / `all`. |
| Model proxy | `.../Ai/AiAgentEndpoints.cs` + `src/Services/Dcms.AiGateway/*` | BYOK per user+tenant, Vault-encrypted, Anthropic wire format, OpenAI bridge, SSE streaming. |
| Draft API | `src/Services/Dcms.AdminApi/Sites/SiteEndpoints.cs:279` (`GET /ide`), `:352` (`PATCH /ide/files`) | Per (site, user, branch) draft, per-file hash conflict → 409. |
| Git surface | `.../Sites/Git/SiteGitService.cs`, `apps/admin/src/features/site-source/git.ts` | status/branches/history/changes/commit/compare/merge/restore/builds. |
| Sandbox | `.../Sites/SitePreviewEndpoints.cs` (per-tenant `X-Dcms-Sandbox`), `Dcms.SiteBuilder/SandboxOptions.cs` | Preview already writes to a tenant sandbox, never live data. |

### The gaps

1. **No search, no index.** `list_files` dumps every path; `read_file` returns whole files.
2. **No line ranges, no patches, no hash guards** on agent edits.
3. **No workspace abstraction.** `features/ide/agent/tools.ts` reaches straight into the zustand store.
4. **IDE agent has no history.** Reload loses the transcript. The *other* assistant has one.
5. **No context budget, no compression, no working memory, no prompt-cache structure.**
6. **No model routing.** Every turn is `effort: high` + adaptive thinking, even "rename this button".
7. **Agent cannot validate.** The bundler runs beside it and it cannot call it.
8. **No preview introspection** — no console, network, DOM or screenshot tool.
9. **No git tools for the agent**, no diff, no status.
10. **Conflicts are a dead end.** 409 pauses autosave until a full reload; no rebase, no per-file recovery.
11. **Agent runs are tab-local and unrecorded.** Close the tab, lose everything.
12. **GET loops remain**: `DeploymentsView.tsx:23`, `media/api.ts:82,95`, `chat/ChatPage.tsx:38`, plus five in `apps/platform`.
13. **The IDE is not VS Code-like**: no bottom panel, no output/console surface, no agent-diff review, thin activity bar.
14. **Only two agent modes** (`auto` / `manual`) and no access to tenant config from the IDE agent.

---

## 3. Target architecture

The loop stays in the browser (**D1**). That is not the naive shape the brief criticises — the
brief's real argument is *don't hand the project to the model*, which is about context
discipline, not about which process runs the `for` loop. Keeping it client-side buys instant
edits in open tabs, zero-latency search over a file map the browser already holds, and direct
access to the bundler and the preview DOM. What it costs is run durability; §Phase 8 answers
that by streaming the transcript to the server as it happens.

```
┌──────────────────────────── Browser (apps/admin) ─────────────────────────────┐
│                                                                               │
│  Assistant dock ─┐                              ┌─ IDE agent panel            │
│                  ├──►  AgentRuntime (shared)  ◄─┤                             │
│                  │     classify → plan → run   │                              │
│                  │     ContextManager          │                              │
│                  │     ToolRouter · ModelRouter│                              │
│                  └─────────────┬───────────────┘                              │
│                                │                                              │
│                    ┌───────────▼────────────┐                                 │
│                    │     AgentWorkspace     │  ◄── the ONE door to site files │
│                    │  index · search · read │                                 │
│                    │  patch · diff · validate│                                │
│                    └───────────┬────────────┘                                 │
│            ┌───────────────────┼────────────────────┐                         │
│       ┌────▼─────┐      ┌──────▼──────┐      ┌──────▼──────┐                  │
│       │ VFS store│      │ index worker│      │ preview     │                  │
│       │ (zustand)│      │ esbuild/tsc │      │ iframe+console                 │
│       └────┬─────┘      └─────────────┘      └─────────────┘                  │
└────────────┼──────────────────────────────────────────────────────────────────┘
             │ delta PATCH (hash-guarded)      ▲ SSE model stream    ▲ SignalR
             ▼                                 │                     │
┌────────────────────────────── admin-api ─────┴─────────────────────┴──────────┐
│  SiteDraft (site+user+branch, revision, per-file hashes)                       │
│  git · builds · tenant content/plugins/media · sandbox · AiConversation store  │
│  /admin/ai/messages  ──►  ai-gateway  ──►  provider (BYOK, Vault)              │
│  /hub/sites: BuildChanged · DraftChanged · CommitPushed · MediaChanged …       │
└───────────────────────────────────────────────────────────────────────────────┘
```

Two rules decide most of the detail:

- **The agent has no special powers.** It reads and writes the same `SiteDraft` through the same
  hash-guarded delta path the editor uses. Human and agent are two clients of one workspace.
- **Server-known facts cost zero tokens.** Branch, revision, diff, file tree, diagnostics, build
  status, symbols and routes are plain operations (local or REST), never model turns.

**On "SignalR for everything":** the brief asks for SignalR instead of GET loops. Model-response
streaming already arrives push-style over the SSE body of `/admin/ai/messages`, so it is not a
GET loop and stays as it is. Everything that *is* server state — builds, drafts, commits, media,
certificates — moves to the hub in Phase 9.

---

## 4. Decisions

### Locked

- **D1 — The agent loop runs in the browser.** A shared `AgentRuntime` module, not a server
  service. Consequence: a closed tab ends the run; the transcript survives (Phase 8), the run
  does not. Accepted.
- **D2 — Two surfaces, one runtime.** The assistant dock and the IDE agent panel stay distinct
  UIs backed by one runtime, one tool registry, one mode/risk model.
- **D3 — `AgentWorkspace` is the only door to site files.** No tool touches the zustand store,
  the draft REST API or git directly.
- **D4 — One agent, not a swarm.**
- **D5 — Anthropic Messages stays the wire format**; `ai-gateway` keeps translating
  ([`AnthropicOpenAiBridge`](src/Services/Dcms.AiGateway/Providers/AnthropicOpenAiBridge.cs)).
- **D6 — Patch-first editing.** Whole-file writes only for new files and genuine rewrites.
- **D7 — The existing four-mode risk model is the base** (`features/assistant/modes.ts`);
  the IDE's `auto`/`manual` pair is retired into it.
- **D8 — No unrestricted shell.** Curated tools; sandboxed execution is specific tools.
- **D9 — Sandbox = preview sandbox + real SiteBuilder build.** The agent can exercise tenant
  APIs against `X-Dcms-Sandbox` and trigger a real sandboxed build and read its log. No scratch
  branch — it works on the user's draft like the user does.
- **D10 — All `refetchInterval` polling goes.** The hub plus a refetch on reconnect and on window
  focus replaces it, everywhere including `apps/platform`.
- **D11 — The index is a browser worker.** Hand-rolled TS/TSX scanner (imports, exports,
  components, routes, CSS classes/vars), no new dependency; it runs next to the existing esbuild
  worker.

### Open

- ~~**Q1 — Conversation storage shape.**~~ **Settled in Phase 8: extend.** One table with a
  `Surface` column, filtered at every call site. See 8.1 for the reasoning and for the migration
  trap it turned up.

---

## 5. Phases

### Phase 0 — Foundations & decisions

- [x] **0.1** Audit the current agent, VFS, draft, conflict, preview and realtime surfaces. → §2
- [x] **0.2** Write this plan. → this file
- [x] **0.3** Settle the architecture decisions with the user. → §4, D1–D11 locked
- [x] **0.4** Define the internal contracts up front: `AgentEvent`, `ToolSpec` / `ToolCall` /
      `ToolResult`, `WorkspaceRevision`, `ContextBudget`. One TS module shared by both surfaces.
      *Accept:* both surfaces compile against the shared types; no `any` on the boundary. ✔
      Created `features/agent/` as the neutral shared module — the two surfaces were already
      entangled (`assistant/steps.ts` imports from `ide/agent/client`), so a shared home was
      overdue. `contracts.ts` holds `ToolSpec<TContext>`, `ToolOutcome`, `ToolCall`,
      `ToolResult`, the `AgentEvent` union, `WorkspaceRevision`, `FileSlice`, `UnchangedFile`,
      `SearchHit`, `ContextBudget` + `estimateTokens`. `modes.ts` moved here from `assistant/`
      so the contract does not depend on one of its own surfaces. Both registries now bind to
      it: `AssistantTool = Omit<ToolSpec<ToolContext>, 'run'> & {...}` and the IDE's
      `AGENT_TOOLS: readonly ToolDeclaration[]`.
      **Known divergence, named not hidden:** the assistant's tools still return a bare `string`
      where the contract says `ToolOutcome`. Phase 5 converges them.
- [~] **0.5** Baseline measurement harness: record input/output tokens, tool-call count and wall
      time per run so every later phase can prove it helped.
      *Accept:* three fixed tasks ("change a button", "add a page", "fix a build error") produce
      a committed baseline in `benchmarks/ai-agent.json`.
      **Instrument built; baseline not yet taken.** `ai-gateway` already counted tokens per API
      call into Prometheus (`AnthropicUsageScanner` → `DcmsMetrics.AiCall`), which cannot answer
      "did this task get cheaper" — a counter has no notion of a run. Added:
      `client.ts` captures `usage` + `model` off `message_start`/`message_delta`;
      `agent/runMetrics.ts` sums them per run with tool counts and wall time;
      `scripts/ai-bench/` folds records into `benchmarks/ai-agent.json` and compares labelled
      sets. Blind spots are documented in `scripts/ai-bench/README.md` — chiefly that
      **input tokens are null on non-Anthropic providers**, because `AnthropicOpenAiBridge`
      emits only `output_tokens` to the browser. Taking the baseline needs a real run against a
      real key, so it is the next manual step.

### Phase 1 — `AgentWorkspace`: index, search, revision

Lives in `apps/admin/src/features/site-source/workspace/` — beside the VFS, above it, and the
only thing the agent is given.

- [x] **1.1** `AgentWorkspace` facade over the VFS store: `revision` (the VFS `rev`), per-file
      hash + size, and a stable snapshot a run can pin to. ✔ `agent/workspace.ts`.
      Built over a **file-map snapshot rather than the live store**: tools are async and the
      store moves under them, so "the files as they were when this call started" has to be a
      value, not a subscription. Hashing is `agent/hash.ts` (cyrb53, memoised) — deliberately
      *not* the server's SHA-256, which answers a different question ("does this collide with
      what the server stored") and is async, which would push `await` into a store the editor
      touches on every keystroke.
- [x] **1.2** Project index: files, imports/exports, React components, routes, CSS classes and
      variables, `package.json` dependencies. Incremental on write, not rebuilt. ✔
      `agent/projectIndex.ts` + `summarize()`, which replaces the file-list dump in the prompt.
      *Accept:* a 200-file build under 300 ms — **asserted by a test**, not claimed.
      **Deviation: no worker.** The plan called for one by analogy with the esbuild bundler, but
      that needs a worker because WASM compilation genuinely blocks; a regex sweep is single-digit
      milliseconds and the incremental path re-scans one file. A worker would add a message
      boundary and an await to save nothing measurable.
      It is a **scanner, not a parser** — it can miss a definition (costs one extra search) or
      report one inside a string (costs one wasted read). Neither is a correctness risk, because
      nothing it returns decides what gets written; every edit is hash-guarded against real
      content.
- [~] **1.3** `workspace.search` — text mode with compact `path:line` results, `paths` filter,
      `maxResults`, literal-by-default with opt-in regex, binary files skipped.
      **Text mode done**; `mode: "symbol" | "references"` waits on the index (1.2).
      One hit per *line*, not per match — the model is choosing where to look, and saying the
      same line matched four times costs four times as much for the same information.
      `features/site-source/search.ts` stays for now: it backs the human Search panel, which
      needs columns and highlight offsets the agent has no use for. Merging them is not free
      and not yet justified.
- [x] **1.4** `workspace.tree` — directory summary with counts, expandable by prefix, not a path
      dump. ✔ `src/ (42 files)` is one line the model can act on; the same thing as JSON is four.
- [x] **1.5** `workspace.read` with `startLine`/`endLine`, returning `{path, content, hash,
      range, totalLines}`. ✔ Out-of-bounds ranges are **clamped, not rejected** — a stale line
      number from an earlier read should return the nearby code, not an error the model then has
      to recover from. A whole-file read past `MAX_WHOLE_FILE_LINES` (400) returns the head plus
      an explicit instruction to ask for a range, which teaches the narrower request instead of
      silently truncating.
- [x] **1.6** Hash-aware reads: `ifHash` → `{path, hash, unchanged: true}`, killing the
      read-edit-read loop. ✔
- [x] **1.7** Caches: file hashes memoised on content, search memoised per workspace snapshot
      (the revision is in the key, so a cached answer cannot survive an edit), project metadata
      built once and updated incrementally. ✔ Diagnostics caching waits on 5.3, which is where
      diagnostics become a tool.

### Phase 2 — Edit primitives & agent transactions

- [x] **2.1** `edit.patch(path, expectedHash, patch)` — anchored replacement with a hash guard;
      a mismatch is a refusal, never a silent overwrite, **and the refusal names the current
      hash** so the retry is a re-read at the right version rather than a guess. ✔
      Uniqueness is required: replacing "the first occurrence" in a file with repeated structure
      corrupts it silently and still compiles. `replaceAll` is the explicit escape hatch.
- [x] **2.2** `edit.insert_at` / `edit.replace_range` / `edit.delete_range` — line-addressed ops
      for what a textual patch does clumsily. ✔ **Write ranges are refused, not clamped** — the
      opposite of reads. A read that clamps returns nearby code and costs nothing; a write that
      clamps deletes lines the caller never named.
- [x] **2.3** `edit.create` / `edit.delete` / `edit.rename` on the same guard rails. ✔
      `create` refuses to overwrite: a create that silently replaces a file is how a run destroys
      work nobody asked it to touch.
- [x] **2.4** **Agent transaction:** a run's edits land in the VFS immediately (so tabs and the
      preview move) but flush as **one** draft delta. ✔ `agent/transaction.ts` +
      `vfs.agentRuns` hold + the autosave gate in `useDraftSession`.
      The debounce alone did **not** achieve this, which is why the hold exists: it coalesces
      writes within 1.2 s, and a run that reads, thinks and validates between edits routinely
      takes longer than that per file. A counter, not a flag, because the dock and the IDE panel
      are two surfaces over one workspace and the hold must outlast the last of them.
      The change set collapses repeated edits to one entry against **pre-run** content, omits a
      file edited back to where it started, and supports revert-all / revert-one.
- [~] **2.5** Conflict rebase: `rebaseAnchoredPatch` exists in `agent/edits.ts` with the rule —
      retry once against current content, succeed only if the anchor is still unique, because a
      gone or newly-ambiguous anchor means the human's edit genuinely overlaps and re-anchoring
      would be guessing. **Wiring it into the retry path is Phase 4** (the runtime), which is
      where a failed tool call is turned into a repair.
- [x] **2.6** Dependency policy — **the audit's premise was wrong and the real problem is worse.**
      `TOOLCHAIN_FILES` in `site-source/paths.ts` is already an empty set, so frontend and
      backend already agree that a site owns its `package.json`. What is stale is the *agent's*
      view: `ide/agent/tools.ts` still refuses "platform-managed toolchain files" via a check
      that can no longer fire, and `systemPrompt.ts` still tells the model "you cannot add npm
      dependencies", which is false — `ReactAppBuilder` builds whatever the site commits.
      The genuine constraint is an **asymmetry nothing currently states**: the live preview
      bundles bare imports from esm.sh pinned to the fixed palette
      (`virtual:dcms-palette-types`), while the production build resolves the real lockfile. So
      a dependency outside the palette *builds in production but breaks in preview*. Delete the
      dead toolchain check, and state the asymmetry — not a false prohibition — in Phase 6.1's
      prompt rewrite and in the `deps` tool (5.3).
      ✔ Done — see the 2026-09-11 log entry. The box was simply never ticked.

### Phase 3 — Draft, autosave & conflict rework

- [x] **3.1** Kill the false positives. ✔ Per-tab `clientId` (`site-source/clientId.ts`,
      `sessionStorage` so two tabs differ but a reload does not) sent on every save, echoed back
      on `DraftUpdate`, and used for echo suppression. The version test remains only as the
      fallback for an admin-api old enough not to echo one.
      Version-based suppression was sound only while saves were strictly ordered, and they are
      not: an agent batch flushing while the author types produces overlapping saves, and the
      test then failed **both** ways — suppressing a real remote change (silently) or announcing
      the tab's own work back to it.
- [x] **3.2** Autosave no longer dies on conflict. ✔ Per-file quarantine: `takeDelta` skips
      conflicted paths and the rest keeps flowing, so the blast radius of a conflict is the file
      it happened in rather than the whole project. `setConflict` **unions** rather than
      replaces — replacing would release the first file from quarantine and let the next flush
      clobber it. `resolveConflict(path, content, serverHash)` rebases the file onto the server's
      baseline, without which the very next save 409s again on the same file.
- [x] **3.3** Server-newer sync: `vfs.mergeRemote` + `session.syncFromServer()`. ✔ The IDE now
      **pulls and merges** on `DraftChanged` instead of offering a reload.
      *Accept:* a second tab editing a different file causes zero prompts in the first — **test:
      "leaves a locally-edited file alone when the server changed a different one"**.
      **The tests caught a real bug in my first implementation.** I compared local text to remote
      text, which flags every file the author is currently typing in, every time any other file
      is saved anywhere — the exact false positive this step exists to remove. A conflict
      requires *both* sides to have moved: the server's hash differs from the baseline this tab
      last synced at **and** there is an unsaved local edit. `mergeRemote` also does not bump
      `generation`, because that resyncs every Monaco model and discards cursors and undo
      history.
- [-] **3.4** Per-branch client state. **Dropped: the audit was wrong.** `openDocs.ts` is
      already keyed by `(siteId, branch)` and both call sites in `IdePage` pass the branch. No
      change needed. (Cursor/scroll/composer persistence is a Phase 10 nicety, not a correctness
      fix, and is tracked there.)
- [~] **3.5** Agent writes carry `origin` through the same save path. ✔ on the wire
      (`ideApi.saveFiles(..., origin)` + `SaveFilesRequest.Origin`); the editor-side presentation
      of a run as a change set is Phase 10.5, which is where the review pane lives.
- [x] **3.6** Tests: `site-source/merge.test.ts`, 15 cases covering adopt-untouched,
      edited-elsewhere, both-sides-same, genuine divergence, unsaved-edit-with-static-server,
      server-side deletion, quarantine of puts and deletes, conflict union, resolve, and the
      agent-run hold counter. ✔

### Phase 4 — Agent runtime & event stream

- [x] **4.1** `AgentRuntime` (browser): `runAgent(opts) → AsyncIterable<AgentEvent>`. ✔
      `agent/runtime.ts` + `agent/eventQueue.ts` (a push-to-pull adapter, because the model
      client reports through callbacks and consumers want `for await`).
      `ide/agent/useAgentSession.ts` is **rewritten onto it** and is now just this surface's
      wiring — tools, prompt, events-to-React-state. Converging `useAssistantSession.ts` is next.
- [x] **4.2** Typed `AgentEvent` stream. ✔ `turn.completed` carries the turn's `usage` and
      `model`, so the Phase 0.5 instrument is fed by the runtime rather than by a second path
      that could disagree with it.
- [x] **4.3** Parallel execution of independent tool calls; strict ordering where one depends on
      another. ✔ **The rule is the tool's declared risk**: reads run concurrently, writes run in
      the order the model asked for them. Two searches are independent and serialising them is
      pure latency; two writes may touch the same file, and the second's hash guard is computed
      against a workspace the first has already changed — order is part of their meaning.
      Both behaviours are asserted by tests, not assumed.
- [x] **4.4** Complexity classifier. ✔ `agent/classify.ts`, a **heuristic, not a model call** —
      a classifier that costs a round trip costs what it saves, which on a trivial task is most
      of the time the task should have taken. It is allowed to be wrong: under-classifying means
      planning unnecessarily, over-classifying means a few extra turns. Neither is a correctness
      problem, which is what makes a heuristic acceptable at all.
      A test caught that "add" alone is too broad a signal — "add a class to the button" is one
      edit, "add an About page" is a file, a route and a nav entry. The noun separates them.
- [x] **4.5** Failure loop with a hard ceiling of 3 autonomous repair attempts. ✔ Unblocked by
      5.3 and built in the runtime as `opts.validate`, **not** as prompt instruction: asking the
      model to remember to validate works most of the time, and the times it forgets are exactly
      the runs that end with a broken site.
      It only fires after a turn that actually wrote something, it reports `validation.failed`
      rather than success when no check was possible, and on exhaustion it ends the run with a
      written account instead of looping. Also satisfies **11.1**.
- [x] **4.6** Run recovery: the autosave hold is released in a `finally`, including on a throw
      or an abort — a run that dies halfway has still written files, and leaving the hold on
      would strand them unsaved. The change set survives as `session.changes` with
      `revertAll()`. ✔ "Continue from here" needs the stored transcript (Phase 8).

### Phase 5 — The tool suite

- [~] **5.1** *Workspace*: `project_overview`, `list_files`, `read_file`, `search`
      (text/symbol/references) — done in `ide/agent/workspaceTools.ts`. `diff` waits on 5.4.
- [x] **5.2** *Edit*: `edit_file`, `create_file`, `delete_file`, `rename_file`, `insert_lines`,
      `replace_lines`, `delete_lines`. ✔ `delete_file` is risk **dangerous**, not safe: unlike an
      edit there is no earlier version in the working draft to go back to until the whole run is
      reverted.
- [x] **5.3** *Project*: `check_build`, `last_build`, `dependencies`. ✔ `preview/buildBroker.ts`
      is the seam: the esbuild worker belongs to `usePreview`, and standing up a second one would
      mean two WASM instances bundling the same project on every keystroke. The pane publishes,
      the agent reads.
      **It degrades honestly.** With the preview pane closed, `check_build` returns an *error*
      saying it cannot check — never an empty success. A validation tool that reports "no
      problems" when it did not run is worse than none at all, because the model trusts it.
- [x] **5.4** *Git*: `git_status`, `git_diff`, `git_history`, `git_branches` — thin wrappers over
      `features/site-source/git.ts`. ✔ `compare` is left out: it answers a branch-vs-branch
      question the agent has no use for while it works on one draft.
- [x] **5.5** *Preview*: `preview_console`, `preview_dom`, `preview_text`. ✔
      `preview/previewBridge.ts` + `BRIDGE_SCRIPT` injected ahead of the app bundle, so a throw
      during module evaluation is still caught. Collects errors, warnings, exceptions, rejections
      and failed fetches — deliberately **not `console.log`**: a React app in development is
      chatty and a leftover debug line is not a signal worth paying for.
      A test caught a real leak: `attachPreview` installed a listener without removing the
      previous one, so every console message was recorded twice and the agent would read a page
      as having double the errors. Attach is now idempotent.
      **Screenshot dropped for now** — `preview_text` answers "what is on the page" for a
      fraction of the tokens, and the brief is explicit about not shipping an image every
      iteration. Revisit only when a task genuinely needs visual judgement.
- [x] **5.6** *Tenant* (mode- and scope-gated): the 16 tools from `features/assistant/tools.ts`,
      adapted rather than rewritten. ✔ `fromAssistantTool` bridges the one divergence (a bare
      string vs `ToolOutcome`), and that is the right answer rather than a shortcut: the extra
      fields describe *workspace file* effects, and a tool that publishes an article touches no
      workspace file — there is nothing real for them to carry.
- [~] **5.7** *Sandbox* (D9): `sandbox_request` and `sandbox_reset` done — the agent can exercise
      the tenant API through the same same-origin proxy the preview iframe uses, so the sandbox
      header is applied server-side exactly as it is for the site. **`sandbox.build` deferred**:
      triggering a real SiteBuilder build is operationally a deploy, it overlaps publish, and it
      wants its own approval story rather than being smuggled in as a tool.
- [x] **5.8** *Skills*: `read_skill` over `ide/agent/skills.ts` — `react-site`,
      `dcms-source-control`, `dcms-content-api`, `frontend`. ✔ The prompt advertises the names
      (a few dozen tokens); the text loads only when the task needs it.
- [x] **5.9** Tool-result size discipline — **the runtime already guaranteed it; now it is proved.**
      Every result goes through `capResult`, which defaults to 8 000 characters and appends a line
      naming the continuation ("Narrow the request — a line range, a path filter, or a more
      specific query"). A tool that names no ceiling still cannot spend a run's budget on one
      directory listing; five name their own where the default would clip mid-diagnostic
      (`git_diff` 12k, `check_types` 10k, `preview_dom`/`sandbox_request` 6k, `preview_text` 4k).
      Guarded in `agent/registry.test.ts`: every tool in the real registry is fed 100 000
      characters and must truncate, truncation must name its continuation, and an explicit
      ceiling must sit inside a band that means something — a cap of 200k is a cap in name only,
      and one of 200 truncates mid-sentence forever.

### Phase 6 — Context economy

- [x] **6.1** Tiny system prompt. ✔ The file list and the OpenAPI dump are **out** of
      `systemPrompt.ts` — both were pasted in whole on every turn of every run, were the largest
      line item on a real site, and were mostly never read. `project_overview` and `search` are
      the replacements. The prompt now states only what is true every turn, including the
      preview/production dependency asymmetry.
- [x] **6.2** `ContextBudget` enforced by `agent/context.ts`, wired into every turn. ✔ The full
      transcript stays in `opts.messages`; what is pruned is **the copy on the wire**, so what
      gets stored later is still complete.
- [x] **6.3** Three-layer history. ✔ Compression works outside-in, least destructive first:
      **elide old tool results**, then drop whole exchanges, then carry a working-memory block.
      A long run's history is dominated by old tool results — file reads from eight turns ago
      describing code that no longer exists, resent every turn — and they are the safest thing
      to drop because the model can simply read the file again.
      **The constraint that shapes it:** the Messages API requires every `tool_use` to be
      answered by a `tool_result` with the matching id in the next message. Orphaning one is a
      request the provider refuses outright, and the failure surfaces far from its cause — so
      messages are only ever removed in whole exchanges, and `findOrphanedToolUses` is exported
      purely so the tests can assert it never happens.
      Working memory (goal, files changed, notes) is maintained **by the runtime**, not asked of
      the model: a model asked to summarise its own history spends a turn doing it.
- [x] **6.4** Prompt-cache-friendly request shape: `withCacheHints` puts `system` and `tools`
      first and `messages` last, and marks the last tool with `cache_control`. ✔ A prefix cache
      keys on exact order, so anything that changes between turns must come after everything
      that does not.
- [x] **6.5** **Router**: `agent/modelRouter.ts`, per-turn effort by classified complexity —
      trivial gets thinking *disabled* and a 4k ceiling; complex gets adaptive thinking at high
      effort. A repair turn always gets high effort regardless, because a run that already failed
      its build has demonstrated the cheap setting was not enough. ✔
      **Deviation: it routes effort, not model names.** The browser does not choose the model —
      ai-gateway resolves it from the tenant's and the user's settings, which may be a local
      Ollama with exactly one model installed. Sending a model name the workspace has not
      configured would fail, and overriding a user who deliberately pinned one is worse than not
      trying. Effort and token ceilings are where most of the cost difference on a trivial task
      actually lives. When the platform grows a per-tenant tier map, this is the one file that
      has to learn about it.
- [!] **6.6** Re-measure against the Phase 0 baseline:
      `node scripts/ai-bench/ai-bench.mjs compare --before baseline --after phase6`.
      *Accept:* simple edit ≤10k input tokens and 1 model call; normal feature ≤40k and ≤2 calls.
      **BLOCKED, and deliberately left unclaimed.** The baseline (0.5) was never taken, because
      it needs real runs against a real billed key on a real site — which is not something the
      test suite can do. Every Phase 6 mechanism is built and unit-tested, but **no measured
      claim about token savings can be made until somebody runs the three fixed tasks.** That
      is the next manual step, and the reason the instrument was built first.
- [x] **6.7** `AnthropicOpenAiBridge` now emits `input_tokens` alongside `output_tokens`. ✔
      Anthropic sends the prompt side in `message_start`; the bridge emits no such frame, so it
      rides on `message_delta` instead — harmless, because every reader takes the maximum per
      field. Test added; 22/22 bridge tests pass. `client.ts` and the bench README corrected,
      since they described the gap as permanent.

### Phase 7 — Modes, permissions & sandbox

- [x] **7.1** Unify on `read` / `careful` / `agent` / `auto` (D7); the IDE's `auto`/`manual`
      toggle is retired and `AgentPanel` now offers the four shared modes. ✔ `read` and `careful`
      are deliberately reachable in the IDE: reviewing what the agent *would* do is a legitimate
      way to use it on a site that is already live.
- [x] **7.2** **Scope** axis, orthogonal to mode. ✔ `agent/scope.ts` + `toolsFor`.
      Deliberately separate: mode answers "how much does it do without asking", scope answers
      "what may it touch at all". Collapsing them gives the familiar mess where turning up
      autonomy silently widens reach — an operator who wanted the agent to stop asking about
      edits did not thereby want it publishing articles.
      Scope is **never remembered**, unlike mode: it always starts at `site`.
- [x] **7.3** Risk table — audited across all 41 tools and then made unbreakable.
      `dangerous` is exactly `delete_file`, `delete_media`, `publish_content`, `unpublish_content`
      and `schedule_content`; there is no merge tool, because every git tool the agent holds is a
      read. `agent/registry.test.ts` now fails the build if a tool whose *name* says it publishes
      or destroys is not marked `dangerous`, unless it is listed in `SAFE_DESPITE_VERB` with the
      reason — one entry, `delete_lines`, which edits text inside a file the run transaction can
      revert. A second test keeps that exception list from naming tools that no longer exist,
      since a stale entry silently exempts the next tool to take the name.
      Also asserted against the registry rather than in isolation: a `dangerous` tool is
      `unavailable` in read, `approve` in careful *and* agent, and only ever runs unattended in
      full auto — and every gated tool carries both `summarize()` and `describe()`, because a
      tool that reaches the approval card without a sentence is a JSON blob asking for a yes.
      **The guard immediately earned itself**: `delete_file` and `delete_lines` — the two most
      consequential workspace tools — had 28- and 39-character descriptions while their harmless
      siblings had paragraphs. Both rewritten to say when *not* to call them.
- [x] **7.4** Permission enforcement server-side — **already true, now proved and kept true.**
      Audited every endpoint the agent can reach, one at a time: all sixteen tenant tools, the
      IDE draft save, git, publish and the AI proxy. **Every one carries `RequirePermission`,
      and every mutating one also carries `WithAudit`.** The one ungated route in that set is
      `GET /api/admin/plugins/catalog`, which returns static plugin manifests for config forms
      and is authenticated-only on purpose.
      So there is nothing to build *per tool*, and a second server-side registry keyed by tool
      name would be worse than nothing: a list to keep in sync with the endpoints, guarding a
      door the browser can walk past by calling the endpoint directly. The endpoint is the
      boundary; the tool's `permission` field is UX, keeping the model from being offered a call
      that could only end in a refusal.
      What was missing was anything that keeps it that way, so:
      **`tests/Dcms.IntegrationTests/Security/PermissionCoverageTests.cs`** — every mutating
      endpoint across admin-api, content-api, identity and platform-api must declare how it is
      gated: a permission, a service-principal scope, a self-scoped route, `AllowAnonymous`, or
      the new `.PermissionExempt("reason")` for a gate the endpoint table cannot see. Modelled on
      the existing `AuditCoverageTests`, container-free, so it runs on every machine.
      Adopted with a **shrinking baseline** rather than fifty exemption strings written by
      someone who had not read the endpoints: `KnownUndeclared` lists what predates the guard, so
      it fails on new omissions from today. Two further tests stop that list rotting — a stale
      entry would silently re-open the route it names, and an entry for an endpoint that now
      declares itself would make the list stop measuring anything. Proved non-vacuous by removing
      an entry and by faking a stale one, and watching each fail. 16 tests.
- [x] **7.5** Approval UX — `ide/agent/ApprovalCard.tsx`, replacing a card that listed
      `edit_file src/App.tsx` over its arguments and offered Apply or Reject.
      That card asked somebody to approve a change to their site by reading **the name of the
      function that makes it**, and since the name is always reasonable the answer is always yes.
      An approval nobody can meaningfully refuse is not a safety feature; it is a click people
      learn to make quickly, and they carry the habit to the card that says *publish*.
      - **Diff-first.** `approval.ts` turns each gated call into lines out and lines in, at the
        place in the file they happen, against the workspace as it stands *before* the call —
        the only state in which a preview means anything. Non-file tools fall back to their own
        `summarize()`, which 7.3 now requires of every gated tool. Header carries `+n −n`, the
        number somebody weighs before reading a single line. 14 tests.
      - **Three answers.** `ApprovalDecision` is `once | run | deny`; the runtime keeps a per-run,
        per-tool-name allowance so "for the rest of this run" actually stops the asking. It is
        deliberately not a setting — a standing permission nobody remembers granting is the thing
        this whole model exists to avoid — and it dies with the run.
      - **Keyboard-driven.** Enter allows, A allows for the run, Escape refuses, listened for on
        the document so the shortcut works wherever focus went; skipped while the operator is
        typing, so Enter in the composer is still a message. Each shortcut is printed on the
        button that performs it.
      **A real hazard caught on the way.** Widening `approve` from a boolean left the gate reading
      `decision !== 'deny'` — so an approver still returning the old `false` was not a refusal, it
      was an approval of every dangerous call in the batch. It allow-lists now, failing closed the
      same way a missing approver already did, and a test pins it.
- [x] **7.6** Audit — **already true, and already guarded.** Every mutating endpoint the agent
      reaches records through `Dcms.Shared.Audit`: `ContentCreated`, `ContentUpdated`,
      `ContentPublished`, `ContentUnpublished`, `ContentScheduled`, `MediaUploaded`, `MediaMoved`,
      `MediaDeleted`, `MediaFolderCreated`, `SiteFilesChanged`, `GitCommitted`,
      `SitePublishRequested`, `AiRequestProxied`. `AuditCoverageTests` has enforced the rule since
      before this work: declare `.WithAudit(...)` or `.AuditExempt("reason")`, or fail the build.
      Recorded at the endpoint rather than in the browser loop, which is the only place it can be
      trusted — an audit trail written by the client is one the client can decline to write.

### Phase 8 — History & persistence — **DONE**

- [x] **8.1** **Q1 settled: extend, one table.** `Surface` (`console` | `ide`), `SiteId` and
      `Branch` on `AiConversation`, plus the `(TenantId, OwnerUserId, Surface, SiteId, UpdatedAt)`
      index the IDE rail reads through. The two surfaces run one runtime over one wire format, so
      their rows are the same kind of row; a second table would have duplicated the entire review
      story hanging off this one — `ai:chats:read-all`, workspace visibility, RLS registration,
      retention. The one real argument for splitting (the IDE's many short runs burying the
      console's few long conversations) is answered by the column plus an index, and every list
      call now *must* name a surface.
      `Branch` is the **last** branch run against, not the first: a branch is a property of when
      a turn happened, and an operator can switch mid-conversation.
      **Caught in review:** EF's generated `AddColumn` backfilled `Surface` as `""`, which no
      rail filters on — every pre-existing conversation would have silently vanished from the
      assistant's history. Fixed with `HasDefaultValue("console")` at the model level, so the
      migration's `ADD COLUMN … DEFAULT 'console'` backfills correctly.
- [x] **8.2** The IDE agent stored nothing at all before this — reload and the whole run was gone.
      It now appends at every turn boundary (`turn.completed`, `tool.completed`,
      `validation.started`) and once more in `finally`, tracking `persistedUpTo` against the live
      message array the runtime mutates. Serialised through a promise chain, because `Seq` is
      assigned server-side and overlapping appends would interleave the transcript.
      `persistedUpTo` advances **only on success**, so a failed append re-sends rather than
      leaving a hole. The conversation is opened *before* the first model call, so a run whose
      first call fails still records what was asked.
      *Accept:* ✔ covered by `persistence.test.tsx` — a provider that never answers still leaves
      the question, the conversation and an unfinished run record stored.
- [x] **8.3** `ConversationRail` now takes `surface` and `siteId` and is mounted in the IDE's
      agent panel behind a History toggle, alongside a New-conversation action. `resume(id)` puts
      the stored blocks straight back into the model history and projects them onto the panel —
      tool calls shown as the call, never as their result, because a resumed transcript that
      pasted whole file reads back would be unreadable.
- [x] **8.4** New `ai.runs` table (**registered in `RlsConfigurator.TenantTables`**): task,
      seq range, outcome, complexity, change set, validation verdict, metrics.
      **Upserted on a browser-generated id**, written once at the start and once at the end,
      because under D1 nobody server-side can close out a run whose tab was closed — and writing
      only at the end would store successes and nothing else, when the runs worth reviewing are
      exactly the ones that stopped. A row with `finishedAt: null` is not a gap; it is the record.
      **The diff is deliberately not stored:** every edit is already in the transcript, in the
      `tool_use` block that made it. What a run adds is the summary across the whole run, which
      no single message has. `validation: null` means never checked (preview closed) and is kept
      distinct from failing.
- [x] **8.5** `AiConversationRetentionWorker`: **two windows**, because the two surfaces are not
      the same kind of record — an IDE run is a working note about a change now in git (45 d), a
      console conversation is often the only place the reasoning behind a content change exists
      (180 d). Both count from last activity, and **nothing shared or archived is ever swept**:
      sharing hands a transcript to colleagues and archiving is what the product already offers
      for "keep this". One `ExecuteDelete`; messages and runs go on the cascade. Advisory-locked
      to one replica, one audit record per pass.
      The 1 MB per-append cap was re-checked and **needed a client-side answer**: individual tool
      results are capped by `capResult`, but each failed flush leaves its turns pending, so an
      outage during a tool-heavy run accumulates a backlog that would eventually exceed the cap
      and stick *permanently*. Appends are now drained in ≤512 KB batches. A single oversized turn
      is still sent alone rather than skipped — the Messages API refuses a transcript with an
      orphaned `tool_use`, so skipping one message would break every later resume.

### Phase 9 — Realtime sweep: remove the GET loops (D10) — **DONE**

Independent of the agent work; can absorb spare cycles at any point.

- [x] **9.1** Shared `useHubRevalidation` (`packages/ui/src/live/`): refetches on hub reconnect,
      on the tab becoming visible, and on the browser coming back online, throttled to 30 s —
      except while the hub is known to be down, where the throttle is skipped because nothing
      is backing the data at all. Both apps set `refetchOnWindowFocus: false` globally and keep
      it; this is the targeted version. Mounted once per app shell. ✔ 10 tests.
      Its companion is **`hubPresence`** (4 tests): a tiny registry each hub hook reports into,
      so a panel three levels below the shell can ask whether the socket is up without the
      boolean being threaded through every component in between. `undefined` (no such hub on
      this page) is deliberately distinct from `false` (the socket is down).
- [x] **9.2** `DeploymentsView` — poll gone. `BuildChanged` already carried every transition,
      including the post-publish one the 2.5 s poll was really written for. **The whole
      expect-build mechanism died with it** (`EXPECT_BUILD_WINDOW_MS`, `expectBuild`,
      `expectingBuildSince` and five call sites): it existed only to decide whether to keep
      polling. The panel now says *"not receiving live updates"* when the site hub is down —
      a panel that has stopped asking and does not admit it is worse than one that polls.
- [x] **9.3** `media/api.ts` — both 15 s polls gone; the `media` tag was already the mechanism.
- [x] **9.4** `chat/ChatPage.tsx` — the 15 s fallback gone.
- [x] **9.5** `apps/platform` ×5 — and the distinction the plan asked for turned out to be real.
      *Certificates* and *notifications* were pure fallbacks behind a push, so they just went.
      *Monitoring*, *overview* and *storage* ×2 are genuine periodic samples — read from
      Prometheus, Loki and the tenant plane, none of which announce anything — so their period
      moved server-side into **`PlatformSampleBroadcaster`**, which pushes `health` / `stores` /
      `overview` ticks on the console hub. Same freshness; the load on Prometheus stops scaling
      with how many tabs somebody left open. It does nothing at all while no console is
      connected to the replica, which a browser poll could never manage.
      Two guards came with it: `PlatformHub` counts connections per replica (keyed on
      `Context.Items`, so a refused connection cannot drift the count negative), and
      `createResourceInvalidator` grew an optional **per-tag throttle** so a scaled platform-api
      broadcasting the same tick from every replica costs each console one refetch, not N.
      Announcement tags take no throttle: dropping a real change is the staleness push exists
      to prevent. ✔ 3 tests.
- [x] **9.6** Guard: `no-restricted-syntax` on `Property[key.name='refetchInterval']` across
      `apps/*/src/**`, with the reason in the message. Verified it fires.
      *Accept:* `grep -rn refetchInterval apps/ --exclude-dir=dist` returns nothing ✔ — the
      prose mentions went too, each one having described something no longer there.

### Phase 10 — VS Code-like IDE — **DONE** (10.3 partial: no split view, see below)

Invoked the `frontend-design` skill before any markup was written.

**Design note.** The brief pins the visual direction hard ("more VS Code-like") and the IDE
lives inside an already-themed SPA, so palette and typography were *not* open choices — a
differently-coloured IDE bolted into the admin would read as a third-party embed. Every token is
the admin's own. What the pass actually decided:

- **The organising idea: three actors write to this workspace** — you, the agent, and the server
  (another tab, a colleague's commit). VS Code's whole visual language assumes one author and a
  local filesystem. So every new surface makes authorship and freshness legible.
- **One rule carried everywhere: *clean* and *unchecked* are different states.** An empty
  Problems list with the preview off does not mean the code is fine; an empty build history does
  not mean nothing was built; a run whose gate never ran is not a run that passed. Each of those
  says so, with the action that would actually check. `PanelEmpty` vs `PanelUnchecked` is that
  rule made structural.
- **Monospace means "verbatim machine text"** — paths, log lines, tool names, line numbers,
  timestamps — never "this looks technical". That keeps mono meaningful instead of decorative.
- **No motion that nobody asked for.** Panel height, tab switches and card expansion animate
  because they answer a click; nothing enters on its own.

- [x] **10.1** Bottom panel added, resizable and persisted per browser (`usePanelState`,
      `useStoredWidth` extended to a height, `Resizer` grew a `horizontal` orientation). The
      activity bar, side bar, editor group and status bar already existed.
- [x] **10.2** Tabs: **Problems (moved out of the sidebar)**, Output, Console, Build.
      Problems was in a 260px column where every `path:line  message` row wrapped onto three
      lines, and it competed with the file tree for the same space — you could look at the error
      or at the file it was in, never both.
      *New:* `output.ts` (the workspace log — saves, publishes, sandbox resets, which were toasts
      and therefore gone the moment they faded; collapses immediate repeats into `(×n)`),
      `ConsoleView` (the preview bridge's messages, built for the agent and never shown to the
      person who can actually fix them), `BuildLogView` (the broker only ever kept the *latest*
      result, which cannot answer "did this just break or has it been broken since I touched
      that file?").
      **Deviation, stated:** the plan lists *Agent* as a bottom-panel tab. It is not one. A
      conversation is tall and narrow; a bottom panel is short and wide. The agent keeps a
      full-height side panel, and the four log-shaped surfaces share the bottom.
- [~] **10.3** Breadcrumbs ✔ (`src › components › Nav.tsx` — the full path previously existed
      only in a `title` nobody hovers). Tab pinning ✔ (pins sort to the front, survive
      *Close other tabs*, and drop when their tab closes). ✔ 9 tests.
      **Split view: not built, and not an oversight.** With the preview open the editor column is
      ~500px; splitting it gives two 250px editors, which is unusable. Split view and a live
      preview pane are competing answers to the same question, and this product already chose
      the preview.
- [x] **10.4** `Transcript.tsx`, rebuilt on the Phase 4 event stream. Entries became a typed
      union instead of `{type, text, isError}` — a tool call used to render as the grey word
      `edit_file` with no arguments, no result and no duration. Now: collapsible tool cards
      carrying the model's own arguments and the result it got back, per-call file chips that
      open the file, collapsed streamed thinking, a retry line, and a build-check line where
      **skipped is its own state** rather than a quiet tick.
      Run meter: turns, tools, in/out/cached tokens, wall time. **An unknown input count renders
      as `—`, never as 0** — that is the exact failure the measurement work exists to avoid. No
      currency: price depends on model, tier and contract, none of which the browser knows.
- [x] **10.5** `ChangeReview.tsx`: the run's edits, A/M/D in git's own letters, click to diff,
      revert one file or all.
      **There is no Accept button, deliberately.** The agent writes through the same VFS the
      editor does, so the changes are already in the workspace — "accept" would be a no-op with
      a reassuring label, which is the worst kind of control. The real actions are the
      destructive ones.
- [x] **10.6** Palette gained one command per panel tab (people search for "Problems", not for
      "panel"), plus *Close other tabs*. Keyboard: `⌘⇧M` shows Problems, backtick toggles the
      panel (both Meta and Control, because macOS will not give up `⌘\``).
- [x] **10.7** Verified in a real browser at 1600×950, light **and** dark — every token flips,
      including the panel, badges and timestamps. Empty/unchecked states written for all four new
      surfaces. Both locales complete (en + cs), checked programmatically rather than by eye.
      **Two real problems the visual review caught**, neither of which any test would have:
      the IDE agent and the global assistant dock were *both* called "Assistant" — two different
      products with one name on one screen — so the IDE's is now **Agent** throughout; and the
      agent panel's header had a title, a mode `<select>` and three icon buttons fighting over a
      240px column, so the mode picker moved to the composer row where it belongs (it is a
      property of the next thing you send).

### Phase 11 — Validation, telemetry & hardening

- [x] **11.1** Deterministic gate before any repair turn — esbuild today; TS and lint are 11.2.
      The model is called only when a check fails. ✔ See 4.5.
- [x] **11.2** Real TS diagnostics, not just esbuild's syntax errors.
      **The premise of this item was wrong, and cheaply so.** It assumed hosting `tsc` in a
      worker — roughly 7 MB — which is why it sat last. It needed neither: Monaco is already
      loaded, its TypeScript worker already runs for the editor's own IntelliSense, a model
      already exists for every non-binary file in the VFS, and `setEagerModelSync(true)` already
      pushes all of them into that worker. The whole project has been type-checked continuously
      the entire time. Nothing was reading the answers. No new dependency, no bigger bundle.
      New `ide/diagnostics/`: `typeCheck.ts` (pure — message flattening, offset→line, mapping,
      baseline diffing, the gate's verdict), `checkTypes.ts` (the worker, imported lazily so the
      editor is not a static dependency of the agent runtime), `useTypeProblems.ts` (the panel).
      Three places it lands:
      - **The gate** now runs both checks. esbuild proves the project bundles; it strips types
        without reading them, so wrong arguments and misspelled props sail through it, and those
        are most of what an agent gets wrong. Type problems are diffed against a baseline taken
        at run start — otherwise a site carrying errors its authors chose to live with fails
        every run and burns all three repair turns on somebody else's code. Matched on file,
        code and message rather than position, so an error that merely moved is still recognised
        as pre-existing. Warnings never fail the gate.
      - **`check_types`**, so the agent can ask rather than only be told at the end.
      - **The Problems panel**, which previously went *empty* with the preview closed for a
        reason that had nothing to do with the code. Type checking needs no preview, so it now
        lists what types say and notes that the build is the half still missing.
      **A deadline on every check, learned from the e2e run.** Without one the gate simply never
      returned when the worker did not answer — no error, just a run parked on "Checking the
      build" until somebody pressed Stop. A check that cannot finish is "unknown", never "clean".
      29 tests.
- [x] **11.3** Per-run telemetry: turns, tool calls, outcome and wall time as metrics
      (`DcmsMetrics.AiRun`), plus the panel's run meter. **Tokens are deliberately not
      re-emitted** — ai-gateway already meters them at the only place that sees every provider,
      and a second counter over the same events is how two dashboards start disagreeing.
      The meter shows an unknown as `—`, never as zero: not every provider reports the prompt
      side of a turn, and a meter that renders a gap as zero reports a saving that never
      happened, which is the exact failure 6.6 exists to avoid. No cost in currency anywhere —
      price depends on model, tier and contract, none of which the browser knows.
- [x] **11.4** `AiQuota` in ai-gateway: 60 calls/minute and 5M tokens/day per tenant, in Redis
      (`IncrementAsync` with a TTL set only on the first increment of a window), with a local
      in-process fallback so a Redis outage degrades to per-pod limiting rather than to none.
      A breach is a 429 carrying `Retry-After`, forwarded through admin-api and surfaced as
      `QuotaError` — the panel names the limit and when to come back, because a limit reported
      as a generic failure is one the user retries straight into. 15 tests.
- [x] **11.5** Injection posture. Tools whose output comes from outside the workspace declare
      `untrustedSource`; the runtime fences their results with a line naming the source, so the
      boundary between instructions and material is in the transcript rather than only in the
      system prompt. The three `preview_*` tools carry it — a rendered page contains whatever
      the site chose to display, and a form submission is written by any visitor on the internet.
      **Recorded in the code and the tests as mitigation, not protection**: what actually stops
      an injected instruction is that the agent holds no authority the user does not — approval
      gates, the scope axis, and the server re-checking every permission regardless of what the
      model believed. Colleagues' code is *not* fenced; it is not a threat the agent can defend
      against and pretending otherwise is theatre. 12 tests.
- [x] **11.6** Tests. The unit side was already carried by phases 0–10; what was missing was
      anything proving the pieces are wired together, so `e2e/admin/ideAgent.spec.ts` runs the
      whole loop in a real browser against a scripted Anthropic SSE provider
      (`e2e/fixtures/aiStream.ts`, which splits tool-call JSON across two `input_json_delta`
      frames so the incremental parser is actually exercised). Four paths: an edit that lands
      and is reviewable, a wrong anchor reported rather than swallowed, a 429, and a 409.
      **It immediately earned itself twice.** `toolEntries` was a `const` declared *after* the
      run loop in `useAgentSession`, while `applyEvent` is a hoisted function declaration — so
      every single tool call in the IDE threw `Cannot access 'toolEntries' before
      initialization`, at runtime, in the browser, invisible to `tsc` and to every unit test.
      And the 409 branch mapped `conflicts` without filtering, so a malformed entry raised the
      conflict banner and then named no files in it (`[undefined].join(', ')` is `''`) while
      quarantining a path that does not exist.
      Those tests run with the preview hidden: esbuild-wasm cannot initialise under the mocked
      harness, so every build fails and the gate correctly retries three times and gives up.
      That is the gate working, and it is not what those tests are about.
- [x] **11.7** Docs: [`docs/ai-agent.md`](docs/ai-agent.md) — why the loop is in the browser, the
      run loop and the build gate, the two axes, the tool registry, how to write a tool and the
      four things that are easy to get wrong, the injection posture, the transcript/run storage
      model, and where cost actually goes. Linked from the README index.

---

## 6. Sequencing

```
P0 ──► P1 ──► P2 ──┬─► P3 ─────────────────────┐
                   └─► P4 ──► P5 ──► P6 ──► P7 ├─► P8 ──► P11
                                               │
P9  (independent, any time) ───────────────────┤
P10 (shell early; agent surfaces after P4+P5) ─┘
```

---

## 7. Progress log

### 2026-09-12
- **Phase 11 built.** Telemetry (11.3), quota (11.4), injection posture (11.5), e2e (11.6),
  docs (11.7) and real TS diagnostics (11.2). `docs/ai-agent.md` is new and linked from the README.
- **7.4 and 7.6 were stale, not open.** Both turned out to be already true, so the work was to
  prove it and then keep it true. Checked every endpoint the agent can reach one at a time:
  all sixteen tenant tools, the IDE draft save, git, publish and the AI proxy — every one carries
  `RequirePermission`, and every mutating one also carries `WithAudit`.
  The gap was that nothing stopped that decaying, so there is now a
  `PermissionCoverageTests` guard alongside the existing `AuditCoverageTests`: every mutating
  endpoint in all four services must declare how it is gated or fail the build. Adopted as a
  shrinking baseline rather than fifty exemption strings invented by someone who had not read
  the endpoints — it fails on new omissions from today, and two more tests keep the baseline
  itself from rotting. Proved non-vacuous by breaking it three ways and watching it fail.
- **7.5, 7.3 and 5.9 done, closing every phase item except the blocked one.** A diff-first
  approval card with three answers and keyboard shortcuts; a registry guard that makes the risk
  table and the result-size discipline unbreakable rather than merely true today.
  Two more things the new tests caught: `delete_file` and `delete_lines` — the two most
  consequential workspace tools — carried 28- and 39-character descriptions while their harmless
  siblings had paragraphs; and widening `approve` from a boolean briefly left the gate treating a
  stale `false` as **approval** of every dangerous call in the batch. It fails closed now, pinned
  by a test.
- **One finding, reported rather than quietly patched.**
  `POST/PUT/PATCH/DELETE /api/admin/sites/{siteId}/preview/api/{**path}` resolves the site with
  `IgnoreQueryFilters()`, so the tenant it proxies to comes from the `siteId` rather than from
  the caller's own tenant. Tenant membership still gates the request, and the content-api routes
  it reaches are anonymous for everyone anyway, so the reach is bounded to another tenant's
  *sandbox* delivery API — but the filter bypass is explicit and somebody chose it, so it wants a
  decision rather than a silent change. Listed in `KnownUndeclared` with that reasoning attached.
- **The e2e spec found two bugs that nothing else could have.** Both were invisible to `tsc`,
  to eslint and to 478 unit tests, and both sat on paths a user hits immediately:
  - `toolEntries` was declared after the loop that uses it. `applyEvent` is a hoisted function
    declaration; `const` hoists into a temporal dead zone. **Every tool call in the IDE agent
    threw.** The panel showed the TDZ message as the run's error and carried on, which is why
    it looked like a rendering problem rather than a dead feature.
  - The 409 handler mapped `conflicts` to `.path` without filtering. A malformed entry yields
    `[undefined]` — truthy, so the conflict banner renders — and `[undefined].join(', ')` is the
    empty string, so it names no files while still blocking publish. Now filtered.
- **11.2 done, and its premise was wrong.** It was scheduled last because it looked like ~7 MB of
  `tsc` in a worker. Monaco's TypeScript worker was already loaded, already held a model for
  every file, and had been type-checking the whole project continuously the entire time —
  nothing was reading the answers. The agent's gate now runs types *and* the bundler, diffed
  against a pre-run baseline so it is judged on what it broke rather than what it inherited; the
  Problems panel no longer goes blank when the preview is closed.
  **The e2e run earned itself a third time here:** the new gate had no deadline, so a worker
  that never answered parked the run on "Checking the build" indefinitely. Every check is now
  bounded, and a check that cannot finish reports "unknown" rather than "clean".
- **6.6 remains blocked** and no token-saving claim is made anywhere in this document that is not
  labelled as a mechanism rather than a measurement.
- **Local build note, pre-existing:** `pnpm --filter @dcms/admin build` OOMs at Node's default
  heap on this box. Verified against a clean `HEAD` worktree, so it is not from this work; it
  builds with `NODE_OPTIONS=--max-old-space-size=5120`. Monaco remains a single chunk — the lazy
  import added no duplicate.
- **Nothing is committed.** The working tree holds phases 0–6 and 8–11.

### 2026-09-11 (later)
- **Phase 6 built.** `agent/context.ts` (budget + three-layer compression + working memory),
  `agent/modelRouter.ts` (per-turn effort + cache-friendly request shape), and the
  `AnthropicOpenAiBridge` fix so both sides of the usage reach the browser.
  **6.6 is blocked, not done:** the mechanisms exist and are unit-tested, but the Phase 0
  baseline was never taken, so there is still no measured claim that any of this saved tokens.
  Taking it needs a real run against a real key.
- **Phase 5 done**, and 4.5/11.1/7.2 with it. New: `preview/buildBroker.ts`,
  `preview/previewBridge.ts`, `ide/agent/checkTools.ts`, `ide/agent/tenantTools.ts`,
  `ide/agent/skills.ts`, `agent/scope.ts`. The agent now has 25 tools across files, build, git,
  preview, tenant, sandbox and skills.
  Two bugs the tests caught: the preview bridge double-counted every console message because
  `attachPreview` leaked its listener, and the broker had to learn to answer "I cannot check"
  rather than hang when the preview pane is closed.
- **Phase 4 done.** `agent/runtime.ts` is the one loop behind both surfaces; `useAgentSession`
  is rewritten onto it. New: `eventQueue.ts`, `classify.ts`, `runtime.ts`,
  `ide/agent/workspaceTools.ts` (11 tools over the workspace, replacing the old five).
  7.1 and 6.1 landed alongside because the integration required them: the panel now offers the
  four shared modes, and the system prompt no longer pastes the file list or the OpenAPI spec.
- **Phase 3 done.** The autosave/conflict rework the brief asked for. Backend: `ClientId` and
  `Origin` on the save, `ClientId` echoed on `DraftUpdate`. Frontend: `clientId.ts`,
  `vfs.mergeRemote`, per-file quarantine in `takeDelta`, `resolveConflict`, and the IDE pulling
  and merging on `DraftChanged` instead of offering a reload.
  The merge tests **caught a real bug in my first implementation** — comparing local text to
  remote text made every file being typed in a conflict. The rule is that both sides must have
  moved. See 3.3.
  3.4 dropped: `openDocs.ts` was already keyed by site+branch — the audit item was stale, the
  second such correction this session.
- **Phases 0, 1 and 2 done.** New module `features/agent/`: `contracts.ts`, `modes.ts`,
  `hash.ts`, `workspace.ts`, `projectIndex.ts`, `edits.ts`, `transaction.ts`, `vfsPort.ts`.
  **106 new tests**; admin suite 204 → 310, `tsc -b` clean, eslint clean.
- 2.6 applied: deleted the dead toolchain guards from `ide/agent/tools.ts` and **corrected the
  system prompt**, which told the model it could not add dependencies. It can — the build
  installs whatever package.json declares. The prompt now states the real constraint instead:
  the preview bundles from a fixed esm.sh palette, so an off-palette dependency ships fine but
  shows a broken preview until publish.
- **Phase 0 closed. Phase 1 mostly done.**
- 0.4: created `features/agent/` as the neutral shared module (`contracts.ts`, `modes.ts` moved
  out of `assistant/`, `hash.ts`, `workspace.ts`). Both tool registries now bind to `ToolSpec`.
- 1.1, 1.4, 1.5, 1.6 done and 1.3 partly done — see the steps. 29 new tests in
  `agent/workspace.test.ts`.
- **Verified:** `tsc -b` clean, **233/233** admin tests pass (was 204), eslint clean.
- **Correction to the audit:** step 2.6's premise was wrong. `TOOLCHAIN_FILES` is already empty,
  so frontend and backend already agree. The real problem is that the agent's *system prompt*
  still forbids adding dependencies when the build allows them, and that nothing states the
  actual constraint — the preview bundles from a fixed esm.sh palette while the production build
  resolves the real lockfile, so an off-palette dependency builds but breaks the preview.
- **Next:** 1.2 (project index worker), then 1.7 (caches), then Phase 2.

### 2026-09-11
- Audited every surface the rework touches: IDE agent (`features/ide/agent/*`), VFS and draft
  session (`features/site-source/*`), preview bundler, the workspace assistant and its mode/risk
  model, the AI backend (`Dcms.AdminApi/Ai/*`, `Dcms.AiGateway`), the draft + git endpoints in
  `SiteEndpoints.cs`, the SignalR site hub, and the sandbox story. Findings in §2.
- Wrote this plan: 12 phases, 78 steps.
- Locked D1–D11 with the user. The one that reshaped the plan is **D1 — the loop stays in the
  browser**: the workspace, the index and the context manager are all client-side modules, the
  model stream keeps using the existing SSE proxy, and run durability is bought with streamed
  transcript persistence (8.2) rather than a server-side runtime.
- Built the per-run measurement instrument for 0.5 (see that step). Verified: `tsc -b` clean,
  204/204 admin tests pass, eslint clean. `eslint.config.js` gained a `scripts/**/*.mjs` Node
  block — the main block matches `.{js,ts,tsx}` only, so an `.mjs` file had no globals at all.
- **Next:** take the actual baseline (needs a real run against a real key), then 0.4 (shared
  contracts), then Phase 1.
