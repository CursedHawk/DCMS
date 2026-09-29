import { describe, expect, it } from 'vitest';
import type { RJSFSchema } from '@rjsf/utils';
import { buildUiSchema } from './schemaUi';

describe('buildUiSchema', () => {
  it('routes a contract binding to the binding widget, told which contract', () => {
    const schema = {
      type: 'object',
      properties: {
        title: { type: 'string' },
        rosterSlug: { type: 'string', 'x-dcms-contract-binding': 'roster.members@1' },
      },
    } as RJSFSchema;

    expect(buildUiSchema(schema)).toEqual({
      rosterSlug: { 'ui:widget': 'contract-binding', 'ui:options': { contract: 'roster.members@1' } },
    });
  });

  it('still routes widget formats', () => {
    const schema = { type: 'object', properties: { logo: { type: 'string', format: 'media' } } } as RJSFSchema;
    expect(buildUiSchema(schema)).toEqual({ logo: { 'ui:widget': 'media' } });
  });
});
