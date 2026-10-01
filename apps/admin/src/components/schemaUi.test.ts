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

  it('routes a format to the widget registered under it, and only then', () => {
    const schema = { type: 'object', properties: { logo: { type: 'string', format: 'media' } } } as RJSFSchema;
    expect(buildUiSchema(schema, ['media'])).toEqual({ logo: { 'ui:widget': 'media' } });
    // An installed plugin's format with its widget not (yet) loaded is a plain input, not a crash.
    expect(buildUiSchema(schema, [])).toEqual({});
  });
});
