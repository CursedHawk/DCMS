import { describe, expect, it } from 'vitest';
import type { AssistantTool } from '../../assistant/tools';
import { siteAgentAppTools } from './appModelTools';

const op = (name: string, risk?: 'safe' | 'dangerous'): AssistantTool => ({ name, description: name, input_schema: {}, risk, run: async () => '' });

describe('the site agent’s Dynamic Apps tools', () => {
  it('keeps reads, preview, validation and change sets, and asks before a change set', () => {
    const tools = siteAgentAppTools([
      op('crm_config_get_summary'),
      op('crm_config_get_table'),
      op('crm_config_apply_change_set', 'safe'),
      op('crm_config_validate_draft', 'safe'),
      op('crm_config_publish_draft', 'dangerous'),
      op('crm_config_discard_draft', 'dangerous'),
      op('crm_config_start_flow', 'dangerous'),
      op('crm_records_create_record', 'safe'),
    ]);
    expect(tools.map((t) => t.name)).toEqual(['crm_config_get_summary', 'crm_config_get_table', 'crm_config_apply_change_set', 'crm_config_validate_draft']);
    expect(tools.find((t) => t.name === 'crm_config_apply_change_set')!.risk).toBe('dangerous');
    expect(tools.find((t) => t.name === 'crm_config_validate_draft')!.risk).toBe('safe');
  });
});
