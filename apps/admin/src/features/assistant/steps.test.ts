import { describe, expect, it } from 'vitest';
import type { Message } from '../ide/agent/client';
import { fieldChanges, prettyPayload, stepsFromMessages, type ToolStep } from './steps';
import { ASSISTANT_TOOLS } from './tools';

/**
 * Replaying a stored conversation. The stored blocks are what the model produced, so the
 * transcript is a projection of them rather than a second record that could disagree.
 */
describe('stepsFromMessages', () => {
  const conversation: Message[] = [
    { role: 'user', content: 'draft something' },
    {
      role: 'assistant',
      content: [
        { type: 'text', text: 'On it.' },
        {
          type: 'tool_use',
          id: 'c1',
          name: 'create_content',
          input: { instanceId: 'i', contentType: 'article', slug: 'autumn-26', data: {} },
        },
      ],
    },
    {
      role: 'user',
      content: [{ type: 'tool_result', tool_use_id: 'c1', content: '{"id":"item-1"}' }],
    },
  ];

  it('rebuilds the question, the answer and the work', () => {
    const steps = stepsFromMessages(conversation, ASSISTANT_TOOLS);
    expect(steps.map((s) => s.kind)).toEqual(['user', 'assistant', 'tool']);
  });

  it('gives a resumed card the same label it had live', () => {
    const [, , card] = stepsFromMessages(conversation, ASSISTANT_TOOLS) as [
      unknown,
      unknown,
      ToolStep,
    ];
    expect(card.label).toBe('Created draft autumn-26');
    expect(card.status).toBe('ok');
    expect(card.result).toContain('item-1');
  });

  it('marks a call whose result never arrived as still running, rather than dropping it', () => {
    // The tab was closed mid-run. "We stopped recording here" is the honest reading of it;
    // showing nothing would say the agent never made the call.
    const steps = stepsFromMessages(conversation.slice(0, 2), ASSISTANT_TOOLS);
    expect((steps[2] as ToolStep).status).toBe('running');
  });

  it('reports an error result as an error', () => {
    const failed: Message[] = [
      ...conversation.slice(0, 2),
      {
        role: 'user',
        content: [
          { type: 'tool_result', tool_use_id: 'c1', content: 'the slug is taken', is_error: true },
        ],
      },
    ];
    const card = stepsFromMessages(failed, ASSISTANT_TOOLS)[2] as ToolStep;
    expect(card.status).toBe('error');
    expect(card.error).toBe('the slug is taken');
  });

  it('skips empty text blocks rather than rendering blank rows', () => {
    const steps = stepsFromMessages(
      [{ role: 'assistant', content: [{ type: 'text', text: '  ' }] }],
      ASSISTANT_TOOLS,
    );
    expect(steps).toEqual([]);
  });

  it('names a tool that no longer exists instead of failing the replay', () => {
    // A conversation can outlive a tool. Losing the label is acceptable; losing the transcript
    // that says what was done to the workspace is not.
    const steps = stepsFromMessages(
      [
        {
          role: 'assistant',
          content: [{ type: 'tool_use', id: 'c9', name: 'retired_tool', input: {} }],
        },
      ],
      ASSISTANT_TOOLS,
    );
    expect((steps[0] as ToolStep).label).toBe('retired_tool');
  });
});

describe('fieldChanges', () => {
  const card = (result: string): ToolStep => ({
    kind: 'tool',
    id: 's1',
    callId: 'c1',
    name: 'update_content',
    label: 'Edited title',
    risk: 'safe',
    input: {},
    status: 'ok',
    result,
  });

  it('reads the before and after out of the stored result', () => {
    expect(
      fieldChanges(card('{"changed":{"before":{"title":"Old"},"after":{"title":"New"}}}')),
    ).toEqual([{ field: 'title', before: 'Old', after: 'New' }]);
  });

  it('shows a field that had no previous value as empty rather than absent', () => {
    expect(fieldChanges(card('{"changed":{"before":{},"after":{"perex":"New"}}}'))).toEqual([
      { field: 'perex', before: null, after: 'New' },
    ]);
  });

  it('has nothing to show for a tool that does not report changes', () => {
    expect(fieldChanges(card('{"id":"item-1"}'))).toEqual([]);
  });

  it('does not throw on a result that is not JSON', () => {
    // Several tools return prose when they fail.
    expect(fieldChanges(card('the collection is gone'))).toEqual([]);
  });
});

describe('prettyPayload', () => {
  it('formats JSON', () => {
    expect(prettyPayload('{"a":1}')).toBe('{\n  "a": 1\n}');
  });

  it('leaves anything else alone', () => {
    expect(prettyPayload('not json')).toBe('not json');
    expect(prettyPayload(undefined)).toBe('');
  });
});
