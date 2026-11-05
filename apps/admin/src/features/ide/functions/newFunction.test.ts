import { describe, expect, it } from 'vitest';
import { fileOf, problemOf, starterOf, type NewFunction } from './newFunction';
import { typeText } from './typeText';

const base: NewFunction = {
  language: 'ts',
  path: 'orders/total',
  description: '',
  start: 'http',
  access: ['user', 'apiKey'],
  methods: ['get', 'post'],
  table: 'crm.deals',
  event: 'updated',
  topic: 'orders.paid',
  cron: '0 * * * *',
  interval: '15m',
};

describe('new function', () => {
  it('puts the file where its path says, as the build names it', () => {
    expect(fileOf(base)).toBe('functions/orders/total.ts');
    expect(fileOf({ language: 'cs', path: 'orders/order-total' })).toBe('functions/orders/OrderTotal.cs');
    expect(fileOf({ language: 'cs', path: 'ping' })).toBe('functions/Ping.cs');
  });

  it('refuses what the build would', () => {
    expect(problemOf(base)).toBeNull();
    expect(problemOf({ ...base, path: 'Orders/total' })).toBe('ide.newFn.badPath');
    expect(problemOf({ ...base, path: 'a/b/c/d/e' })).toBe('ide.newFn.badPath');
    expect(problemOf({ ...base, access: [] })).toBe('ide.newFn.noAccess');
    expect(problemOf({ ...base, start: 'row', table: 'deals' })).toBe('ide.newFn.badTable');
    expect(problemOf({ ...base, start: 'schedule', cron: '* *' })).toBe('ide.newFn.badCron');
  });

  it('writes a TypeScript function that declares its trigger and access', () => {
    expect(starterOf(base)).toContain('access: { http: ["user", "apiKey"] },');
    expect(starterOf({ ...base, methods: ['post', 'get', 'delete'] })).toContain('access: { http: ["user", "apiKey"], methods: ["get", "post", "delete"] },');
    expect(starterOf(base)).toContain('async handler(input: { name?: string }, ctx): Promise<{ message: string }> {');
    expect(problemOf({ ...base, methods: [] })).toBe('ide.newFn.noMethods');
    const row = starterOf({ ...base, start: 'row', description: 'Line two "quoted"' });
    expect(row).toContain('import { defineFunction, on } from "@dcms/functions";');
    expect(row).toContain('triggers: [on.row("crm.deals").updated()],');
    expect(row).toContain('uses: { tables: { "crm.deals": ["read"] } },');
    expect(row).toContain('description: "Line two \\"quoted\\"",');
  });

  it('writes a C# function named by its last segment, in a namespace of its folders', () => {
    const cs = starterOf({ ...base, language: 'cs', path: 'orders/order-total', start: 'every' });
    expect(cs).toContain('namespace Functions.Orders;');
    expect(cs).toContain('[Function("order-total")]');
    expect(cs).toContain('[OnEvery("15m")]');
    expect(cs).toContain('public sealed class OrderTotal : Function<JsonNode, object>');
    const http = starterOf({ ...base, language: 'cs', methods: ['put'] });
    expect(http).toContain('[Http(Access.User, Access.ApiKey, Methods = [HttpMethod.Put])]');
    expect(http).toContain('public sealed record TotalInput(string? Name);');
    expect(http).toContain('public sealed class Total : Function<TotalInput, TotalOutput>');
  });

  it('says a schema as a short type', () => {
    expect(typeText({})).toBe('any');
    expect(
      typeText({
        type: 'object',
        properties: { lines: { type: 'array', items: { type: 'number' } }, status: { type: ['string', 'null'], enum: ['open', null] }, tags: { type: 'object', additionalProperties: { type: 'string' } } },
        required: ['lines'],
      }),
    ).toBe('{ lines: number[]; status?: "open" | null; tags?: Record<string, string> }');
  });
});
