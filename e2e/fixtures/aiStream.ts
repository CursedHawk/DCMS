/**
 * A scripted Anthropic SSE stream, for driving the agent without a provider.
 *
 * <p>The agent loop is the one part of this product that cannot be exercised by any other
 * suite: it runs in the browser, it is driven entirely by a streamed wire format, and what it
 * does with that stream is edit the user's files. jsdom cannot host it (esbuild-wasm, an
 * iframe, Monaco) and the integration tests never reach the browser at all.</p>
 *
 * <p>So the provider is scripted here. These are the exact events `streamAssistantTurn` parses;
 * a turn that omits `content_block_stop` produces a tool call the loop assembles and never
 * dispatches, which is precisely the class of bug this fixture exists to catch.</p>
 */

export interface ScriptedTool {
  id: string;
  name: string;
  input: Record<string, unknown>;
}

/** One model turn: some text, and optionally a tool call that ends it. */
export interface ScriptedTurn {
  text?: string;
  thinking?: string;
  tool?: ScriptedTool;
}

function event(type: string, payload: Record<string, unknown>): string {
  return `event: ${type}\ndata: ${JSON.stringify({ type, ...payload })}\n\n`;
}

export function anthropicStream(turn: ScriptedTurn): string {
  const frames: string[] = [
    event('message_start', {
      message: { model: 'claude-test-1', usage: { input_tokens: 1200, output_tokens: 0 } },
    }),
  ];

  let index = 0;

  if (turn.thinking) {
    frames.push(
      event('content_block_start', { index, content_block: { type: 'thinking', thinking: '' } }),
      event('content_block_delta', {
        index,
        delta: { type: 'thinking_delta', thinking: turn.thinking },
      }),
      event('content_block_stop', { index }),
    );
    index++;
  }

  if (turn.text) {
    frames.push(
      event('content_block_start', { index, content_block: { type: 'text', text: '' } }),
      event('content_block_delta', { index, delta: { type: 'text_delta', text: turn.text } }),
      event('content_block_stop', { index }),
    );
    index++;
  }

  if (turn.tool) {
    frames.push(
      event('content_block_start', {
        index,
        content_block: { type: 'tool_use', id: turn.tool.id, name: turn.tool.name, input: {} },
      }),
      // Split across two deltas on purpose: the loop accumulates partial JSON and parses it on
      // stop, and a fixture that sent it whole would never exercise that.
      ...splitJson(JSON.stringify(turn.tool.input)).map((partial_json) =>
        event('content_block_delta', { index, delta: { type: 'input_json_delta', partial_json } }),
      ),
      event('content_block_stop', { index }),
    );
  }

  frames.push(
    event('message_delta', {
      delta: { stop_reason: turn.tool ? 'tool_use' : 'end_turn' },
      usage: { output_tokens: 240 },
    }),
    event('message_stop', {}),
  );

  return frames.join('');
}

function splitJson(json: string): string[] {
  const at = Math.max(1, Math.floor(json.length / 2));
  return [json.slice(0, at), json.slice(at)];
}
