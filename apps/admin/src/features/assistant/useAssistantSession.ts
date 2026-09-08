import { useCallback, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { usePermissions } from '@dcms/ui';
import {
  type Message,
  NoApiKeyError,
  streamAssistantTurn,
  type ToolResultBlock,
  type ToolUseBlock,
} from '../ide/agent/client';
import { type AiPageContext } from './context';
import { type AiAccessMode, type AssistantTool, canUseWriteAccess, toolDefinitions, toolsFor } from './tools';

/*
 * Shorter than the IDE agent's 25. That one is writing code across many files and genuinely
 * needs the rounds; this one answers a question about a workspace, and a loop that keeps going
 * past ten turns is not converging, it is stuck — and every turn costs the operator money.
 */
const MAX_ITERATIONS = 10;
const MAX_TOKENS = 4000;

/**
 * Where the access mode is remembered.
 *
 * <p>Per browser, not per session: somebody drafting a week of posts should not re-arm the
 * switch every time they close the dock. It is safe to remember because it is not the gate —
 * every individual write still stops at the approval card with its payload on show.</p>
 */
const MODE_KEY = 'dcms.ai.access';

function storedMode(): AiAccessMode {
  try {
    return localStorage.getItem(MODE_KEY) === 'write' ? 'write' : 'read';
  } catch {
    return 'read';
  }
}

export interface AssistantEntry {
  id: string;
  type: 'user' | 'assistant' | 'tool' | 'error';
  text: string;
}

/** A write the model has proposed, waiting for the operator to allow or refuse it. */
export interface PendingWrite {
  call: ToolUseBlock;
  /** One line saying what will happen, from the tool itself. */
  summary: string;
  /** The exact arguments, pretty-printed — the operator approves this, not a paraphrase. */
  payload: string;
}

function systemPrompt(page: AiPageContext | null, mode: AiAccessMode): string {
  return [
    'You are the assistant inside DCMS, a content and site management platform.',
    'You answer questions about the workspace the operator is signed in to, using the tools provided.',
    '',
    'Rules:',
    '- Use a tool rather than guessing. You have no knowledge of this workspace except what tools return.',
    '- If no tool can answer, say so plainly and say which permission would be needed.',
    '- Be brief. These are working answers, not essays.',
    '- Never invent ids, file names, counts or dates.',
    ...(mode === 'write'
      ? [
          '',
          'You have write access to content in this workspace:',
          '- Call describe_content_types before creating or updating, so the field names are the real ones. Never invent a field.',
          '- Write real content, not placeholders. If the operator has not said enough to fill a required field, ask rather than inventing facts about them.',
          '- Slugs are lower case with hyphens and no accents.',
          '- Creating and updating produce drafts. Only publish when the operator has actually asked you to publish.',
          '- Every write is shown to the operator for approval before it happens, so propose the whole change rather than asking for permission in prose.',
        ]
      : []),
    page
      ? `\nThe operator is currently looking at: ${page.summary}${
          page.selection?.length ? ` (${page.selection.length} selected)` : ''
        }`
      : '',
  ].join('\n');
}

/**
 * The assistant's turn loop, in the browser.
 *
 * <p>Same shape as the IDE agent's — each model turn is POSTed to the admin-api proxy, which
 * injects the operator's Vault-stored key and streams the response back, so the key never
 * reaches the browser — but a different tool set and a different job.</p>
 *
 * <p>The tools are resolved from the caller's permissions <em>and the access mode</em> on every
 * send rather than once, so a role change or a flipped switch mid-conversation takes effect on
 * the next question instead of at the next reload.</p>
 */
export function useAssistantSession(page: AiPageContext | null) {
  const me = usePermissions();
  const queryClient = useQueryClient();
  const [entries, setEntries] = useState<AssistantEntry[]>([]);
  const [running, setRunning] = useState(false);
  const [needsKey, setNeedsKey] = useState(false);
  // Which provider the server resolved when it refused, so the panel can name it.
  const [keyProvider, setKeyProvider] = useState<string | null>(null);
  const [mode, setModeState] = useState<AiAccessMode>(storedMode);
  const [pending, setPending] = useState<PendingWrite | null>(null);
  const abort = useRef<AbortController | null>(null);
  const history = useRef<Message[]>([]);
  const decide = useRef<((ok: boolean) => void) | null>(null);

  const writable = canUseWriteAccess(me);
  // A role that lost content:write between page loads must not keep a remembered write mode.
  const effectiveMode: AiAccessMode = writable ? mode : 'read';

  const push = (entry: Omit<AssistantEntry, 'id'>) =>
    setEntries((current) => [...current, { ...entry, id: crypto.randomUUID() }]);

  const setMode = useCallback((next: AiAccessMode) => {
    setModeState(next);
    try {
      localStorage.setItem(MODE_KEY, next);
    } catch {
      // A browser refusing storage is not a reason to refuse the switch.
    }
  }, []);

  /** Answer the approval card. Resolving with false is a refusal the model is told about. */
  const approve = useCallback((ok: boolean) => {
    setPending(null);
    decide.current?.(ok);
    decide.current = null;
  }, []);

  const requestApproval = useCallback((tool: AssistantTool, call: ToolUseBlock) => {
    const input = call.input as Record<string, unknown>;
    return new Promise<boolean>((resolve) => {
      decide.current = resolve;
      setPending({
        call,
        summary: tool.summarize?.(input) ?? tool.name,
        payload: JSON.stringify(input, null, 2),
      });
    });
  }, []);

  const stop = useCallback(() => {
    abort.current?.abort();
    abort.current = null;
    // An abandoned promise would hold the loop open forever behind a card nobody can see.
    decide.current?.(false);
    decide.current = null;
    setPending(null);
    setRunning(false);
  }, []);

  const reset = useCallback(() => {
    stop();
    history.current = [];
    setEntries([]);
    setNeedsKey(false);
  }, [stop]);

  const send = useCallback(
    (text: string) => {
      const question = text.trim();
      if (!question || running) return;

      const available = toolsFor(me, effectiveMode);
      push({ type: 'user', text: question });
      history.current.push({ role: 'user', content: question });

      const controller = new AbortController();
      abort.current = controller;
      setRunning(true);
      setNeedsKey(false);

      void (async () => {
        try {
          for (let round = 0; round < MAX_ITERATIONS; round++) {
            let streamed = '';
            const turn = await streamAssistantTurn(
              {
                max_tokens: MAX_TOKENS,
                system: systemPrompt(page, effectiveMode),
                messages: history.current,
                tools: toolDefinitions(available),
              },
              { onText: (delta) => (streamed += delta) },
              controller.signal,
            );

            history.current.push({ role: 'assistant', content: turn.content });
            if (streamed.trim()) push({ type: 'assistant', text: streamed.trim() });

            const calls = turn.content.filter((b): b is ToolUseBlock => b.type === 'tool_use');
            if (calls.length === 0) break;

            const results: ToolResultBlock[] = [];
            for (const call of calls) {
              const tool = available.find((candidate) => candidate.name === call.name);
              if (!tool) {
                // The model asked for something it was not offered. Tell it so rather than
                // failing the turn — it recovers by answering without the tool.
                results.push({
                  type: 'tool_result',
                  tool_use_id: call.id,
                  content: `No tool named ${call.name} is available to this user.`,
                  is_error: true,
                });
                continue;
              }

              // The approval gate. One card per call, showing the arguments it will send:
              // batching them would ask for a single yes to a set the operator has to unpack.
              if (tool.mutates) {
                const allowed = await requestApproval(tool, call);
                if (controller.signal.aborted) return;
                if (!allowed) {
                  push({ type: 'tool', text: `${tool.name} — declined` });
                  results.push({
                    type: 'tool_result',
                    tool_use_id: call.id,
                    content:
                      'The operator declined this change. Do not retry it; ask what to do differently.',
                    is_error: true,
                  });
                  continue;
                }
              }

              push({ type: 'tool', text: tool.name });
              try {
                const content = await tool.run(call.input as Record<string, unknown>);
                for (const key of tool.invalidates ?? []) {
                  void queryClient.invalidateQueries({ queryKey: [key] });
                }
                results.push({ type: 'tool_result', tool_use_id: call.id, content });
              } catch (error) {
                results.push({
                  type: 'tool_result',
                  tool_use_id: call.id,
                  content: error instanceof Error ? error.message : 'The call failed.',
                  is_error: true,
                });
              }
            }
            history.current.push({ role: 'user', content: results });
          }
        } catch (error) {
          if (controller.signal.aborted) return;
          if (error instanceof NoApiKeyError) {
            setNeedsKey(true);
            setKeyProvider(error.provider);
          }
          else
            push({
              type: 'error',
              text: error instanceof Error ? error.message : 'Something went wrong.',
            });
        } finally {
          if (abort.current === controller) abort.current = null;
          setRunning(false);
        }
      })();
    },
    [effectiveMode, me, page, queryClient, requestApproval, running],
  );

  return {
    entries,
    running,
    needsKey,
    keyProvider,
    mode: effectiveMode,
    setMode,
    /** False when no writing tool is reachable for this role — the switch is not shown at all. */
    writable,
    pending,
    approve,
    send,
    stop,
    reset,
    toolCount: toolsFor(me, effectiveMode).length,
  };
}
