import { useCallback, useRef, useState } from 'react';
import { usePermissions } from '@dcms/ui';
import {
  type Message,
  NoApiKeyError,
  streamAssistantTurn,
  type ToolResultBlock,
  type ToolUseBlock,
} from '../ide/agent/client';
import { type AiPageContext } from './context';
import { toolDefinitions, toolsFor } from './tools';

/*
 * Shorter than the IDE agent's 25. That one is writing code across many files and genuinely
 * needs the rounds; this one answers a question about a workspace, and a loop that keeps going
 * past ten turns is not converging, it is stuck — and every turn costs the operator money.
 */
const MAX_ITERATIONS = 10;
const MAX_TOKENS = 4000;

export interface AssistantEntry {
  id: string;
  type: 'user' | 'assistant' | 'tool' | 'error';
  text: string;
}

function systemPrompt(page: AiPageContext | null): string {
  return [
    'You are the assistant inside DCMS, a content and site management platform.',
    'You answer questions about the workspace the operator is signed in to, using the tools provided.',
    '',
    'Rules:',
    '- Use a tool rather than guessing. You have no knowledge of this workspace except what tools return.',
    '- If no tool can answer, say so plainly and say which permission would be needed.',
    '- Be brief. These are working answers, not essays.',
    '- Never invent ids, file names, counts or dates.',
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
 * <p>The tools are resolved from the caller's permissions on every send rather than once, so a
 * role change mid-conversation takes effect on the next question instead of at the next reload.</p>
 */
export function useAssistantSession(page: AiPageContext | null) {
  const me = usePermissions();
  const [entries, setEntries] = useState<AssistantEntry[]>([]);
  const [running, setRunning] = useState(false);
  const [needsKey, setNeedsKey] = useState(false);
  // Which provider the server resolved when it refused, so the panel can name it.
  const [keyProvider, setKeyProvider] = useState<string | null>(null);
  const abort = useRef<AbortController | null>(null);
  const history = useRef<Message[]>([]);

  const push = (entry: Omit<AssistantEntry, 'id'>) =>
    setEntries((current) => [...current, { ...entry, id: crypto.randomUUID() }]);

  const stop = useCallback(() => {
    abort.current?.abort();
    abort.current = null;
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

      const available = toolsFor(me);
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
                system: systemPrompt(page),
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
              push({ type: 'tool', text: tool.name });
              try {
                results.push({
                  type: 'tool_result',
                  tool_use_id: call.id,
                  content: await tool.run(call.input),
                });
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
    [me, page, running],
  );

  return { entries, running, needsKey, keyProvider, send, stop, reset, toolCount: toolsFor(me).length };
}
