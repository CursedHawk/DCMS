# ai-bench — does the IDE agent actually get cheaper?

The IDE agent rework is judged on a standard: *fewer tokens, fewer API calls, faster*. A standard
nothing can check gets re-argued every time somebody doubts it, so this is the instrument that
settles it.

## What it measures, and what it cannot

`ai-gateway` already counts tokens per **API call** and exports them to Prometheus, tagged by
tenant, provider and model (`DcmsMetrics.AiCall`). That answers *what is the workspace spending*.
It cannot answer *did this task get cheaper*, because a counter aggregated across calls has no
notion of a **run** — one task, however many turns it took. A run is the unit the improvement
happens in.

So the agent loop records its own run totals (`features/ide/agent/runMetrics.ts`) from numbers the
provider already puts on the wire, and this script folds them into a committed baseline.

**It is not automated, and cannot honestly be.** Driving a run needs a browser, a real site and a
real, billed API key; `pnpm e2e` runs against a mocked server and would measure nothing. So the
app produces the records and this script compares them. Running a benchmark is a deliberate act.

### Blind spots, stated up front

- **Input tokens may be unknown.** Both providers now report them — the OpenAI bridge learned to
  emit `input_tokens` alongside `output_tokens` — but an older gateway, a provider that sends no
  usage, or a stream that dies early still leaves the prompt side genuinely unmeasured. Those runs
  record `null`, and the bench refuses to compare them rather than treating the unknown as zero
  and reporting a saving that never happened.
- **Only calls the browser actually made are counted.** A run that dies mid-turn undercounts; its
  `outcome` is recorded so it can be excluded.
- **Cache reads are reported separately** and never folded into the input total, because a cached
  prefix is precisely what Phase 6 is trying to buy.
- **A run is only comparable against the same model.** The model is captured from `message_start`
  and the script refuses cross-model comparisons.
- **n=1 per task.** These are single runs of a non-deterministic system, not a statistical claim.
  Treat a <20% difference as noise.

## The fixed task set

Three tasks, from plan step 0.5. Run each against a scratch Mode B site seeded from the standard
React starter, on a fresh conversation:

| id | prompt |
| --- | --- |
| `button-copy` | Change the primary call-to-action button on the homepage to read "Get started". |
| `add-page` | Add an About page with a heading, two paragraphs of placeholder copy, and a link to it from the nav. |
| `fix-build` | (first break the build: delete an import in `src/App.tsx`) Fix the build error. |

## Using it

1. Run a task in the IDE agent, then export the record (agent panel → copy run metrics) to a file.
2. Fold it into the baseline:

   ```sh
   node scripts/ai-bench/ai-bench.mjs record --task button-copy --label before run.json
   ```

3. After a phase lands, record the same task again with a new label, then compare:

   ```sh
   node scripts/ai-bench/ai-bench.mjs compare --before before --after phase6
   ```

Results live in `benchmarks/ai-agent.json`, committed. A number in a chat log is gone by the time
the comparison matters.
