/**
 * The payload-schema subset (ADR 0037) as the builder edits it. The server's config schema
 * (`$defs/payload`) is what enforces it; this only reads and writes it.
 */
export type Literal = string | number | boolean;

export interface PayloadSchema {
  type?: 'object' | 'array' | 'string' | 'number' | 'integer' | 'boolean';
  description?: string;
  properties?: Record<string, PayloadSchema>;
  required?: string[];
  additionalProperties?: boolean;
  items?: PayloadSchema;
  enum?: Literal[];
  const?: Literal;
  format?: 'date-time' | 'date' | 'email' | 'uri' | 'uuid';
  minLength?: number;
  maxLength?: number;
  minimum?: number;
  maximum?: number;
  minItems?: number;
  maxItems?: number;
}

export interface Channel {
  name: string;
  description?: string;
  keyed?: boolean;
  subscribe?: 'public' | 'signedIn' | 'permission' | 'self';
  publish?: 'none' | 'subscribers' | 'permission';
  raiseEvent?: boolean;
  schema?: PayloadSchema;
}

/** What a field is, in the builder's terms: a JSON type, or a choice of values. */
export const KINDS = ['string', 'number', 'integer', 'boolean', 'object', 'array', 'choice'] as const;
export type Kind = (typeof KINDS)[number];

export const FORMATS = ['date-time', 'date', 'email', 'uri', 'uuid'] as const;

/** The deepest a payload may nest and still be typed by every generator. */
export const MAX_DEPTH = 5;

export function kindOf(schema: PayloadSchema): Kind {
  return schema.enum ? 'choice' : (schema.type ?? 'string');
}

/** The same field as another kind, keeping what still applies (its description). */
export function withKind(schema: PayloadSchema, kind: Kind): PayloadSchema {
  const base: PayloadSchema = schema.description ? { description: schema.description } : {};
  switch (kind) {
    case 'choice':
      return { ...base, enum: schema.enum ?? ['option'] };
    case 'object':
      return { ...base, type: 'object', properties: schema.properties ?? {} };
    case 'array':
      return { ...base, type: 'array', items: schema.items ?? { type: 'string' } };
    default:
      return { ...base, type: kind };
  }
}

const identifier = /^[A-Za-z_$][A-Za-z0-9_$]*$/;
const key = (name: string) => (identifier.test(name) ? name : JSON.stringify(name));

/** The TypeScript the generated clients will show for a payload: the builder's preview. */
export function toTypeScript(schema: PayloadSchema | undefined, indent = ''): string {
  if (!schema) return 'unknown';
  if (schema.const !== undefined) return JSON.stringify(schema.const);
  if (schema.enum) return schema.enum.map((v) => JSON.stringify(v)).join(' | ');
  switch (schema.type) {
    case 'string':
      return 'string';
    case 'number':
    case 'integer':
      return 'number';
    case 'boolean':
      return 'boolean';
    case 'array': {
      const item = toTypeScript(schema.items, indent);
      return item.includes(' ') && !item.startsWith('{') ? `(${item})[]` : `${item}[]`;
    }
    case 'object': {
      const entries = Object.entries(schema.properties ?? {});
      if (entries.length === 0) return 'Record<string, unknown>';
      const inner = indent + '  ';
      const required = new Set(schema.required ?? []);
      const lines = entries.map(([name, field]) => {
        const doc = field.description ? `${inner}/** ${field.description} */\n` : '';
        return `${doc}${inner}${key(name)}${required.has(name) ? '' : '?'}: ${toTypeScript(field, inner)};`;
      });
      return `{\n${lines.join('\n')}\n${indent}}`;
    }
    default:
      return 'unknown';
  }
}

/** Problems the server would refuse, said before saving: names, duplicates, keyed self channels. */
export function channelProblems(channels: Channel[]): string[] {
  const problems: string[] = [];
  const seen = new Set<string>();
  for (const channel of channels) {
    if (!/^[a-z0-9][a-z0-9-]{0,62}$/.test(channel.name)) problems.push(`"${channel.name}": lowercase letters, digits and hyphens.`);
    if (seen.has(channel.name)) problems.push(`"${channel.name}" is declared twice.`);
    seen.add(channel.name);
    if (channel.subscribe === 'self' && !channel.keyed) problems.push(`"${channel.name}": a self channel is keyed by the user's id.`);
  }
  return problems;
}
