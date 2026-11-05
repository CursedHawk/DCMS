/** What the build read from a function's types (JSON Schema), said briefly in the IDE. */
export interface Schema {
  type?: string | string[];
  properties?: Record<string, Schema>;
  required?: string[];
  items?: Schema;
  additionalProperties?: Schema;
  enum?: unknown[];
  anyOf?: Schema[];
}

/** A schema as a short TypeScript-like type: `{ lines: number[]; coupon?: string }`. */
export function typeText(schema: Schema | null | undefined, depth = 0): string {
  if (!schema || Object.keys(schema).length === 0) return 'any';
  if (schema.anyOf) return schema.anyOf.map((s) => typeText(s, depth)).join(' | ');
  if (schema.enum) return schema.enum.map((v) => JSON.stringify(v)).join(' | ');
  const types = Array.isArray(schema.type) ? schema.type : schema.type ? [schema.type] : [];
  const one = (type: string): string => {
    switch (type) {
      case 'integer':
        return 'number';
      case 'array': {
        const item = typeText(schema.items, depth + 1);
        return item.includes(' ') ? `(${item})[]` : `${item}[]`;
      }
      case 'object':
        if (schema.properties) {
          if (depth >= 2) return '{…}';
          const required = new Set(schema.required ?? []);
          const members = Object.entries(schema.properties).map(([k, v]) => `${k}${required.has(k) ? '' : '?'}: ${typeText(v, depth + 1)}`);
          return `{ ${members.join('; ')} }`;
        }
        return schema.additionalProperties ? `Record<string, ${typeText(schema.additionalProperties, depth + 1)}>` : 'object';
      default:
        return type;
    }
  };
  return types.length ? types.map(one).join(' | ') : 'any';
}
