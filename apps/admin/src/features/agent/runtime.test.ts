import { describe, expect, it, vi } from 'vitest';
import type { AssistantTurn, Message } from '../ide/agent/client';
import type { AgentEvent, ToolSpec } from './contracts';
import type { AiMode } from './modes';
import { capResult, runAgent, type ModelTransport } from './runtime';

/** A transport that replays scripted turns, so a run can be driven without a model. */
function scripted(...turns: Partial<AssistantTurn>[]): ModelTransport {
  let i = 0;
  return async (_body, handlers) => {
    const turn = turns[Math.min(i++, turns.length - 1)];
    if (turn.content?.some((b) => b.type === 'text')) {
      handlers.onText?.((turn.content.find((b) => b.type === 'text') as { text: string }).text);
    }
    return {
      content: turn.content ?? [],
      stopReason: turn.stopReason ?? 'end_turn',
      usage: turn.usage ?? null,
      model: turn.model ?? 'test-model',
    };
  };
}

const say = (text: string): Partial<AssistantTurn> => ({
  content: [{ type: 'text', text }],
  stopReason: 'end_turn',
});

const useTools = (
  ...calls: { id: string; name: string; input?: object }[]
): Partial<AssistantTurn> => ({
  content: calls.map((c) => ({ type: 'tool_use', id: c.id, name: c.name, input: c.input ?? {} })),
  stopReason: 'tool_use',
});

interface Ctx {
  log: string[];
}

function tool(
  name: string,
  risk?: 'safe' | 'dangerous',
  run?: ToolSpec<Ctx>['run'],
): ToolSpec<Ctx> {
  return {
    name,
    description: name,
    input_schema: { type: 'object' },
    risk,
    run:
      run ??
      (async (_input, ctx) => {
        ctx.log.push(name);
        return { content: `${name} ok` };
      }),
  };
}

async function collect(iter: AsyncIterable<AgentEvent>): Promise<AgentEvent[]> {
  const out: AgentEvent[] = [];
  for await (const e of iter) out.push(e);
  return out;
}

function run(opts: Partial<Parameters<typeof runAgent<Ctx>>[0]> & { transport: ModelTransport }): {
  events: Promise<AgentEvent[]>;
  messages: Message[];
  ctx: Ctx;
} {
  const messages: Message[] = [];
  const ctx: Ctx = { log: [] };
  const events = collect(
    runAgent<Ctx>({
      task: 'do the thing',
      messages,
      system: 'sys',
      tools: [],
      context: ctx,
      mode: 'agent' as AiMode,
      signal: new AbortController().signal,
      ...opts,
    }),
  );
  return { events, messages, ctx };
}

const types = (events: AgentEvent[]) => events.map((e) => e.type);

describe('run lifecycle', () => {
  it('brackets a run with started and completed', async () => {
    const { events } = run({ transport: scripted(say('done')) });
    expect(types(await events)).toEqual([
      'agent.started',
      'text.delta',
      'turn.completed',
      'agent.completed',
    ]);
  });

  it('streams text deltas as they arrive', async () => {
    const { events } = run({ transport: scripted(say('hello')) });
    const delta = (await events).find((e) => e.type === 'text.delta');
    expect(delta).toEqual({ type: 'text.delta', delta: 'hello' });
  });

  it('appends the task and the reply to the conversation', async () => {
    const { events, messages } = run({ transport: scripted(say('ok')) });
    await events;
    expect(messages[0]).toEqual({ role: 'user', content: 'do the thing' });
    expect(messages[1].role).toBe('assistant');
  });

  it('reports a transport failure as agent.failed, not a throw', async () => {
    const transport: ModelTransport = async () => {
      throw new Error('gateway exploded');
    };
    const events = await run({ transport }).events;
    expect(events.at(-1)).toEqual({
      type: 'agent.failed',
      runId: expect.any(String),
      message: 'gateway exploded',
    });
  });

  it('stops cleanly when aborted', async () => {
    const controller = new AbortController();
    const transport: ModelTransport = async () => {
      controller.abort();
      const err = new Error('aborted');
      err.name = 'AbortError';
      throw err;
    };
    const events = await run({ transport, signal: controller.signal }).events;
    expect(events.at(-1)?.type).toBe('agent.stopped');
  });

  it('stops looping at the iteration ceiling', async () => {
    // A model that always asks for another tool must not run forever.
    const { events, ctx } = run({
      transport: scripted(useTools({ id: '1', name: 'read' })),
      tools: [tool('read')],
      maxIterations: 3,
    });
    await events;
    expect(ctx.log).toHaveLength(3);
  });

  it('says it paused, not completed, when the ceiling cuts a task short', async () => {
    // The HrSite runs ended mid-edit at 25 turns and were recorded as "completed".
    const { events } = run({
      transport: scripted(useTools({ id: '1', name: 'read' })),
      tools: [tool('read')],
      maxIterations: 2,
    });
    const all = await events;
    expect(all.at(-1)).toMatchObject({ type: 'agent.paused', turns: 2 });
    expect(types(all)).not.toContain('agent.completed');
  });

  it('checks what it wrote before pausing, and leaves a failing build in the transcript', async () => {
    const validate = vi.fn(async () => ({ ok: false, report: 'src/App.tsx:3 — TS2304' }));
    const { events, messages } = run({
      transport: scripted(useTools({ id: '1', name: 'edit', input: {} })),
      tools: [tool('edit', 'safe')],
      maxIterations: 2,
      validate,
    });
    const all = await events;
    expect(validate).toHaveBeenCalledOnce();
    expect(types(all)).toEqual(expect.arrayContaining(['validation.started', 'validation.failed', 'agent.paused']));
    // "continue" picks up from what is actually broken.
    expect(JSON.stringify(messages.at(-1))).toContain('TS2304');
  });
});

describe('tool execution', () => {
  it('emits started and completed around a call', async () => {
    const { events } = run({
      transport: scripted(useTools({ id: '1', name: 'read' }), say('done')),
      tools: [tool('read')],
    });
    expect(types(await events)).toContain('tool.started');
    expect(types(await events)).toContain('tool.completed');
  });

  it('uses the tool label for the started event', async () => {
    const labelled: ToolSpec<Ctx> = {
      ...tool('write', 'safe'),
      describe: (input) => `wrote ${String(input.path)}`,
    };
    const { events } = run({
      transport: scripted(useTools({ id: '1', name: 'write', input: { path: 'a.ts' } }), say('ok')),
      tools: [labelled],
    });
    const started = (await events).find((e) => e.type === 'tool.started');
    expect(started).toMatchObject({ label: 'wrote a.ts' });
  });

  it('reports an unknown tool without ending the run', async () => {
    const { events } = run({
      transport: scripted(useTools({ id: '1', name: 'nope' }), say('recovered')),
      tools: [],
    });
    const all = await events;
    expect(all.at(-1)?.type).toBe('agent.completed');
    expect(all.some((e) => e.type === 'tool.completed' && e.result.isError)).toBe(true);
  });

  it('survives a tool that throws', async () => {
    // One call hitting an edge must not kill the task; the model can route around it.
    const angry = tool('boom', undefined, async () => {
      throw new Error('tool blew up');
    });
    const { events } = run({
      transport: scripted(useTools({ id: '1', name: 'boom' }), say('recovered')),
      tools: [angry],
    });
    const all = await events;
    expect(all.at(-1)?.type).toBe('agent.completed');
    expect(
      all.some((e) => e.type === 'tool.completed' && e.result.content === 'tool blew up'),
    ).toBe(true);
  });

  it('emits file.changed for paths a tool reports touching', async () => {
    const writer = tool('write', 'safe', async () => ({ content: 'ok', paths: ['a.ts', 'b.ts'] }));
    const { events } = run({
      transport: scripted(useTools({ id: '1', name: 'write' }), say('ok')),
      tools: [writer],
    });
    const changed = (await events).filter((e) => e.type === 'file.changed');
    expect(changed).toHaveLength(2);
  });
});

describe('parallelism', () => {
  it('runs reads concurrently', async () => {
    let running = 0;
    let peak = 0;
    const slowRead = tool('read', undefined, async () => {
      running++;
      peak = Math.max(peak, running);
      await new Promise((r) => setTimeout(r, 5));
      running--;
      return { content: 'ok' };
    });
    const { events } = run({
      transport: scripted(
        useTools({ id: '1', name: 'read' }, { id: '2', name: 'read' }, { id: '3', name: 'read' }),
        say('ok'),
      ),
      tools: [slowRead],
    });
    await events;
    expect(peak).toBeGreaterThan(1);
  });

  it('runs writes strictly in order', async () => {
    // Two writes may touch the same file, and the second's hash guard is computed against a
    // workspace the first has already changed — order is part of their meaning.
    const order: string[] = [];
    const write = tool('write', 'safe', async (input) => {
      const id = String(input.id);
      await new Promise((r) => setTimeout(r, id === 'a' ? 10 : 1));
      order.push(id);
      return { content: 'ok' };
    });
    const { events } = run({
      transport: scripted(
        useTools(
          { id: '1', name: 'write', input: { id: 'a' } },
          { id: '2', name: 'write', input: { id: 'b' } },
        ),
        say('ok'),
      ),
      tools: [write],
    });
    await events;
    expect(order).toEqual(['a', 'b']);
  });
});

describe('mode gating', () => {
  it('refuses a dangerous tool in read mode without calling it', async () => {
    const { events, ctx } = run({
      transport: scripted(useTools({ id: '1', name: 'publish' }), say('ok')),
      tools: [tool('publish', 'dangerous')],
      mode: 'read',
    });
    await events;
    expect(ctx.log).toEqual([]);
  });

  it('runs a safe tool unattended in agent mode', async () => {
    const approve = vi.fn();
    const { events, ctx } = run({
      transport: scripted(useTools({ id: '1', name: 'write' }), say('ok')),
      tools: [tool('write', 'safe')],
      mode: 'agent',
      approve,
    });
    await events;
    expect(ctx.log).toEqual(['write']);
    expect(approve).not.toHaveBeenCalled();
  });

  it('asks before a dangerous tool in agent mode', async () => {
    const approve = vi.fn().mockResolvedValue('once');
    const { events, ctx } = run({
      transport: scripted(useTools({ id: '1', name: 'publish' }), say('ok')),
      tools: [tool('publish', 'dangerous')],
      mode: 'agent',
      approve,
    });
    await events;
    expect(approve).toHaveBeenCalledOnce();
    expect(ctx.log).toEqual(['publish']);
  });

  it('stops asking once the operator says "for the rest of this run"', async () => {
    // The point of the third answer. Asked twice, a person reads the first card and clicks
    // through the second — so a decision made once has to actually hold.
    const approve = vi.fn().mockResolvedValue('run');
    const { events, ctx } = run({
      transport: scripted(
        useTools({ id: '1', name: 'publish' }),
        useTools({ id: '2', name: 'publish' }),
        say('ok'),
      ),
      tools: [tool('publish', 'dangerous')],
      mode: 'agent',
      approve,
    });
    await events;
    expect(approve).toHaveBeenCalledOnce();
    expect(ctx.log).toEqual(['publish', 'publish']);
  });

  it('asks again for a tool the operator did not allow for the run', async () => {
    // The grant is per tool name, not a blanket "stop asking me about everything".
    const approve = vi.fn().mockResolvedValue('run');
    const { events, ctx } = run({
      transport: scripted(
        useTools({ id: '1', name: 'publish' }),
        useTools({ id: '2', name: 'destroy' }),
        say('ok'),
      ),
      tools: [tool('publish', 'dangerous'), tool('destroy', 'dangerous')],
      mode: 'agent',
      approve,
    });
    await events;
    expect(approve).toHaveBeenCalledTimes(2);
    expect(ctx.log).toEqual(['publish', 'destroy']);
  });

  it('asks every time when the operator only allowed it once', async () => {
    const approve = vi.fn().mockResolvedValue('once');
    const { events, ctx } = run({
      transport: scripted(
        useTools({ id: '1', name: 'publish' }),
        useTools({ id: '2', name: 'publish' }),
        say('ok'),
      ),
      tools: [tool('publish', 'dangerous')],
      mode: 'agent',
      approve,
    });
    await events;
    expect(approve).toHaveBeenCalledTimes(2);
    expect(ctx.log).toEqual(['publish', 'publish']);
  });

  it('does not carry an allowance past the run it was granted in', async () => {
    // A grant that outlived its run would be a standing permission nobody remembers giving.
    const approve = vi.fn().mockResolvedValue('run');
    const options = {
      tools: [tool('publish', 'dangerous')],
      mode: 'agent' as const,
      approve,
    };
    await run({ transport: scripted(useTools({ id: '1', name: 'publish' }), say('ok')), ...options })
      .events;
    await run({ transport: scripted(useTools({ id: '1', name: 'publish' }), say('ok')), ...options })
      .events;
    expect(approve).toHaveBeenCalledTimes(2);
  });

  it('refuses anything that is not an explicit allow', async () => {
    /*
     * Fail closed. This caught a real hazard: while `approve` was being widened from a boolean
     * to a three-way decision, the gate read `decision !== 'deny'` — so an approver still
     * returning the old `false` was not a refusal, it was an approval of every dangerous call
     * in the batch. The check allow-lists instead, and this pins it.
     */
    const { events, ctx } = run({
      transport: scripted(useTools({ id: '1', name: 'publish' }), say('ok')),
      tools: [tool('publish', 'dangerous')],
      mode: 'careful',
      // Deliberately not an ApprovalDecision: a caller that was never updated.
      approve: (async () => false) as unknown as () => Promise<'once'>,
    });
    expect(types(await events)).toContain('tool.declined');
    expect(ctx.log).toEqual([]);
  });

  it('does not run a declined tool and says so', async () => {
    const { events, ctx } = run({
      transport: scripted(useTools({ id: '1', name: 'publish' }), say('ok')),
      tools: [tool('publish', 'dangerous')],
      mode: 'careful',
      approve: async () => 'deny' as const,
    });
    expect(types(await events)).toContain('tool.declined');
    expect(ctx.log).toEqual([]);
  });

  it('refuses rather than running unattended when no approver is wired', async () => {
    // A surface that forgets to pass `approve` must fail closed.
    const { events, ctx } = run({
      transport: scripted(useTools({ id: '1', name: 'publish' }), say('ok')),
      tools: [tool('publish', 'dangerous')],
      mode: 'agent',
    });
    await events;
    expect(ctx.log).toEqual([]);
  });

  it('asks once for a batch rather than once per call', async () => {
    // Three cards in a row trains an operator to click through without reading.
    const approve = vi.fn().mockResolvedValue('once');
    const { events } = run({
      transport: scripted(
        useTools({ id: '1', name: 'publish' }, { id: '2', name: 'publish' }),
        say('ok'),
      ),
      tools: [tool('publish', 'dangerous')],
      mode: 'agent',
      approve,
    });
    await events;
    expect(approve).toHaveBeenCalledOnce();
    expect(approve.mock.calls[0][0]).toHaveLength(2);
  });

  it('runs everything unattended in auto mode', async () => {
    const approve = vi.fn();
    const { events, ctx } = run({
      transport: scripted(useTools({ id: '1', name: 'publish' }), say('ok')),
      tools: [tool('publish', 'dangerous')],
      mode: 'auto',
      approve,
    });
    await events;
    expect(ctx.log).toEqual(['publish']);
    expect(approve).not.toHaveBeenCalled();
  });
});

describe('result capping', () => {
  it('leaves a small result alone', () => {
    expect(capResult('short', 100)).toEqual({ content: 'short', truncated: false });
  });

  it('clips an oversized result and says how to narrow it', () => {
    // Silent truncation is the trap: the model reads a partial answer as a complete one.
    const r = capResult('x'.repeat(200), 50);
    expect(r.truncated).toBe(true);
    expect(r.content).toContain('truncated at 50 characters');
    expect(r.content).toContain('Narrow the request');
  });

  it('keeps the end of the output, where a build puts its errors', () => {
    const log = `${'compiling…\n'.repeat(500)}error TS2304: Cannot find name 'Foo'.`;
    const r = capResult(log, 300);
    expect(r.content).toContain("Cannot find name 'Foo'");
    expect(r.content.startsWith('compiling')).toBe(true);
  });

  it('honours a tool-specific ceiling', async () => {
    const chatty = {
      ...tool('read', undefined, async () => ({ content: 'y'.repeat(100) })),
      maxResultChars: 10,
    };
    const { events } = run({
      transport: scripted(useTools({ id: '1', name: 'read' }), say('ok')),
      tools: [chatty],
    });
    const done = (await events).find((e) => e.type === 'tool.completed');
    expect(done).toMatchObject({ result: { truncated: true } });
  });
});

describe('classification', () => {
  it('reports the classification before the first model call', async () => {
    const onClassified = vi.fn();
    await run({ transport: scripted(say('ok')), task: 'rename the button', onClassified }).events;
    expect(onClassified).toHaveBeenCalledWith(
      expect.objectContaining({ complexity: 'trivial', plan: false }),
    );
  });
});

describe('deterministic repair loop', () => {
  const writeTool = tool('write', 'safe');

  it('does not validate a run that only read', async () => {
    // Rebuilding after a question was answered is a pointless rebuild.
    const validate = vi.fn();
    await run({ transport: scripted(say('here is the answer')), validate }).events;
    expect(validate).not.toHaveBeenCalled();
  });

  it('validates after a run that wrote, and passes', async () => {
    const validate = vi.fn().mockResolvedValue({ ok: true, report: '' });
    const { events } = run({
      transport: scripted(useTools({ id: '1', name: 'write' }), say('done')),
      tools: [writeTool],
      validate,
    });
    const all = await events;
    expect(validate).toHaveBeenCalledOnce();
    expect(types(all)).toContain('validation.passed');
    expect(all.at(-1)?.type).toBe('agent.completed');
  });

  it('feeds a failure back and retries', async () => {
    let calls = 0;
    const validate = vi.fn(async () => {
      calls++;
      return calls === 1 ? { ok: false, report: "a.ts:1 — missing ';'" } : { ok: true, report: '' };
    });
    const { events, messages } = run({
      transport: scripted(useTools({ id: '1', name: 'write' }), say('fixed')),
      tools: [writeTool],
      validate,
    });
    const all = await events;

    expect(types(all)).toContain('agent.retrying');
    expect(all.at(-1)?.type).toBe('agent.completed');
    // The model is told exactly what broke, not just that something did.
    expect(JSON.stringify(messages)).toContain("missing ';'");
  });

  it('gives up after the repair ceiling and says so', async () => {
    // A model that cannot fix a build in three attempts is usually making it worse, and every
    // attempt is billed.
    const validate = vi.fn().mockResolvedValue({ ok: false, report: 'still broken' });
    const { events } = run({
      transport: scripted(useTools({ id: '1', name: 'write' }), say('try again')),
      tools: [writeTool],
      validate,
      maxRepairs: 2,
    });
    const all = await events;

    expect(validate).toHaveBeenCalledTimes(2);
    expect(all.at(-1)).toMatchObject({
      type: 'agent.failed',
      message: expect.stringContaining('2 attempts'),
    });
  });

  it('does not claim success when no check was possible', async () => {
    // The preview pane is closed: silence must not be passed off as a passing build.
    const validate = vi.fn().mockResolvedValue(null);
    const { events } = run({
      transport: scripted(useTools({ id: '1', name: 'write' }), say('done')),
      tools: [writeTool],
      validate,
    });
    const all = await events;
    expect(types(all)).toContain('validation.failed');
    expect(types(all)).not.toContain('validation.passed');
  });

  it('treats a read-only tool call as not having written', async () => {
    const validate = vi.fn();
    await run({
      transport: scripted(useTools({ id: '1', name: 'read' }), say('done')),
      tools: [tool('read')],
      validate,
    }).events;
    expect(validate).not.toHaveBeenCalled();
  });
});

describe('compaction (ADR 0034)', () => {
  /** A transport that records every request and answers by what it was asked. */
  function recording(turns: Partial<AssistantTurn>[]) {
    const bodies: Record<string, unknown>[] = [];
    let i = 0;
    const transport: ModelTransport = async (body) => {
      bodies.push(JSON.parse(JSON.stringify(body)));
      const last = (body.messages as Message[]).at(-1);
      if (JSON.stringify(last).includes('Do not call any tools while writing this summary')) {
        return {
          content: [{ type: 'text', text: '<summary>Read the file; half done.</summary><knowledge>[]</knowledge>' }],
          stopReason: 'end_turn',
          usage: null,
          model: 'test-model',
        };
      }
      const turn = turns[Math.min(i++, turns.length - 1)];
      return {
        content: turn.content ?? [],
        stopReason: turn.stopReason ?? 'end_turn',
        usage: turn.usage ?? null,
        model: 'test-model',
      };
    };
    return { transport, bodies };
  }

  it('summarizes the whole conversation once the prompt reaches the threshold, and carries on from the summary', async () => {
    const { transport, bodies } = recording([
      {
        ...useTools({ id: 't1', name: 'read' }),
        usage: { inputTokens: 500, cacheReadTokens: 60_000, cacheCreationTokens: 0, outputTokens: 100 },
      },
      say('Done.'),
    ]);
    const archived: string[] = [];
    const { events, messages } = run({
      transport,
      tools: [tool('read')],
      compaction: {
        atTokens: 50_000,
        archive: async (result) => {
          archived.push(result.summary);
          return { archivedAt: 'conversations/x/part-1', saved: [] };
        },
      },
    });
    const seen = await events;

    // Three calls: the work, the summary (the previous request plus one message), the work again.
    expect(bodies).toHaveLength(3);
    const [first, summary, after] = bodies as { messages: Message[] }[];
    expect(JSON.stringify(summary.messages.slice(0, -1))).toBe(JSON.stringify([...first.messages, messages[1], messages[2]]));

    // Afterwards only the continuation is sent: no earlier turn, no thinking, no orphan.
    expect(after.messages).toHaveLength(1);
    expect(JSON.stringify(after.messages[0])).toContain('Read the file; half done.');
    expect(JSON.stringify(after.messages[0])).toContain('conversations/x/part-1');
    expect(after.messages[0]).not.toHaveProperty('kind');

    // The transcript keeps everything, the summary marked as such.
    expect(messages.filter((m) => m.kind === 'compaction')).toHaveLength(1);
    expect(archived).toEqual(['Read the file; half done.']);
    expect(seen.map((e) => e.type)).toEqual(expect.arrayContaining(['context.compacting', 'context.compacted', 'agent.completed']));
  });

  it('does not compact below the threshold, and never without the setting', async () => {
    const { transport, bodies } = recording([
      { ...useTools({ id: 't1', name: 'read' }), usage: { inputTokens: 100, cacheReadTokens: 0, cacheCreationTokens: 0, outputTokens: 10 } },
      say('Done.'),
    ]);
    await run({ transport, tools: [tool('read')], compaction: { atTokens: 50_000 } }).events;
    expect(bodies).toHaveLength(2);
    // Append-only: the second request starts with the first one's messages.
    const [first, second] = bodies as { messages: Message[] }[];
    expect(JSON.stringify(second.messages.slice(0, first.messages.length))).toBe(JSON.stringify(first.messages));
  });

  it('a summary that fails ends the run as failed, rather than trimming silently', async () => {
    const failing: ModelTransport = async (body) => {
      const last = (body.messages as Message[]).at(-1);
      if (JSON.stringify(last).includes('Do not call any tools')) throw new Error('provider down');
      return { content: [{ type: 'tool_use', id: 't1', name: 'read', input: {} }], stopReason: 'tool_use', usage: { inputTokens: 90_000, cacheReadTokens: 0, cacheCreationTokens: 0, outputTokens: 0 }, model: 'm' };
    };
    const seen = await run({ transport: failing, tools: [tool('read')], compaction: { atTokens: 50_000 } }).events;
    expect(seen.at(-1)).toMatchObject({ type: 'agent.failed', message: 'provider down' });
  });
});
