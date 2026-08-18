import { useCallback, useRef, useState } from 'react';
import { useVfs } from '../../site-source';
import {
  type Message,
  NoApiKeyError,
  streamAssistantTurn,
  type ToolResultBlock,
  type ToolUseBlock,
} from './client';
import { buildSystemPrompt } from './systemPrompt';
import { AGENT_TOOLS, runTool } from './tools';

const MAX_ITERATIONS = 25;
const MAX_TOKENS = 16000;

export type AgentMode = 'auto' | 'manual';

export interface AgentEntry {
  id: string;
  type: 'user' | 'assistant' | 'tool' | 'error';
  text: string;
  isError?: boolean;
}

export interface PendingApproval {
  tools: ToolUseBlock[];
}

export interface AgentSession {
  entries: AgentEntry[];
  running: boolean;
  mode: AgentMode;
  setMode: (m: AgentMode) => void;
  /** Set when the last run failed because no Anthropic key is linked. */
  needsKey: boolean;
  /** Non-null in manual mode while awaiting approval of proposed edits. */
  pending: PendingApproval | null;
  approve: (ok: boolean) => void;
  send: (text: string) => void;
  stop: () => void;
  reset: () => void;
}

export function useAgentSession(opts: {
  siteName?: string;
  getOpenApi?: () => string | undefined;
}): AgentSession {
  const [entries, setEntries] = useState<AgentEntry[]>([]);
  const [running, setRunning] = useState(false);
  const [mode, setMode] = useState<AgentMode>('auto');
  const [needsKey, setNeedsKey] = useState(false);
  const [pending, setPending] = useState<PendingApproval | null>(null);

  const messagesRef = useRef<Message[]>([]);
  const abortRef = useRef<AbortController | null>(null);
  const approveRef = useRef<((ok: boolean) => void) | null>(null);
  const modeRef = useRef<AgentMode>('auto');
  modeRef.current = mode;

  const push = useCallback((e: AgentEntry) => setEntries((prev) => [...prev, e]), []);
  const patch = useCallback(
    (id: string, text: string) =>
      setEntries((prev) => prev.map((e) => (e.id === id ? { ...e, text } : e))),
    [],
  );

  const approve = useCallback((ok: boolean) => {
    setPending(null);
    approveRef.current?.(ok);
    approveRef.current = null;
  }, []);

  const requestApproval = useCallback((tools: ToolUseBlock[]) => {
    return new Promise<boolean>((resolve) => {
      approveRef.current = resolve;
      setPending({ tools });
    });
  }, []);

  const stop = useCallback(() => {
    abortRef.current?.abort();
    approveRef.current?.(false);
    approveRef.current = null;
    setPending(null);
  }, []);

  const reset = useCallback(() => {
    stop();
    messagesRef.current = [];
    setEntries([]);
    setNeedsKey(false);
  }, [stop]);

  const send = useCallback(
    (text: string) => {
      const trimmed = text.trim();
      if (!trimmed || running) return;

      setNeedsKey(false);
      push({ id: uid(), type: 'user', text: trimmed });
      messagesRef.current.push({ role: 'user', content: trimmed });

      const ctrl = new AbortController();
      abortRef.current = ctrl;
      setRunning(true);

      void (async () => {
        try {
          const system = buildSystemPrompt({
            files: Object.keys(useVfs.getState().files),
            siteName: opts.siteName,
            openApi: opts.getOpenApi?.(),
          });

          for (let i = 0; i < MAX_ITERATIONS; i++) {
            const assistantId = uid();
            let acc = '';
            let started = false;

            const turn = await streamAssistantTurn(
              {
                system,
                messages: messagesRef.current,
                tools: AGENT_TOOLS,
                max_tokens: MAX_TOKENS,
                thinking: { type: 'adaptive' },
                output_config: { effort: 'high' },
              },
              {
                onText: (delta) => {
                  if (!started) {
                    started = true;
                    push({ id: assistantId, type: 'assistant', text: delta });
                    acc = delta;
                  } else {
                    acc += delta;
                    patch(assistantId, acc);
                  }
                },
              },
              ctrl.signal,
            );

            messagesRef.current.push({ role: 'assistant', content: turn.content });

            const toolUses = turn.content.filter(
              (b): b is ToolUseBlock => b.type === 'tool_use',
            );
            if (turn.stopReason !== 'tool_use' || toolUses.length === 0) break;

            // Manual mode: pause for approval before applying any change.
            if (modeRef.current === 'manual') {
              const okToApply = await requestApproval(toolUses);
              if (!okToApply) {
                push({ id: uid(), type: 'tool', text: 'Declined proposed changes.', isError: true });
                messagesRef.current.push({
                  role: 'user',
                  content: toolUses.map<ToolResultBlock>((tu) => ({
                    type: 'tool_result',
                    tool_use_id: tu.id,
                    content: 'User declined this change.',
                    is_error: true,
                  })),
                });
                continue;
              }
            }

            const results = toolUses.map<ToolResultBlock>((tu) => {
              const r = runTool(tu);
              push({ id: uid(), type: 'tool', text: describeTool(tu), isError: r.isError });
              return { type: 'tool_result', tool_use_id: tu.id, content: r.content, is_error: r.isError };
            });
            messagesRef.current.push({ role: 'user', content: results });
          }
        } catch (e) {
          if ((e as Error)?.name === 'AbortError') {
            push({ id: uid(), type: 'error', text: 'Stopped.', isError: true });
          } else if (e instanceof NoApiKeyError) {
            setNeedsKey(true);
            push({ id: uid(), type: 'error', text: e.message, isError: true });
          } else {
            push({ id: uid(), type: 'error', text: (e as Error)?.message ?? 'Agent failed.', isError: true });
          }
        } finally {
          setRunning(false);
          abortRef.current = null;
        }
      })();
    },
    [opts, push, patch, requestApproval, running],
  );

  return { entries, running, mode, setMode, needsKey, pending, approve, send, stop, reset };
}

function describeTool(tu: ToolUseBlock): string {
  const path = (tu.input?.path as string) ?? '';
  switch (tu.name) {
    case 'write_file':
      return `write ${path}`;
    case 'edit_file':
      return `edit ${path}`;
    case 'delete_file':
      return `delete ${path}`;
    case 'read_file':
      return `read ${path}`;
    case 'list_files':
      return 'list files';
    default:
      return tu.name;
  }
}

function uid(): string {
  return crypto.randomUUID();
}
