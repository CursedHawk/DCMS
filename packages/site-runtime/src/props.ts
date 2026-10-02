import { z } from 'zod';
import { NAME, isInternalPath, isSafeExternalHref } from './ids';

/**
 * What a component exposes for an author to set, and which editor control sets it.
 *
 * A prop is always plain data — never a function, a ref or a React node — because it has to
 * survive a round trip through a JSON file, an inspector field and a model's tool call. A
 * definition is itself data too, so a component a tenant builds in the composer declares its
 * props exactly the way a built-in does.
 */

const base = {
  name: z.string().regex(NAME),
  label: z.string().min(1).max(60),
  description: z.string().max(300).optional(),
  required: z.boolean().optional(),
  /** Whether tablet and mobile may override the desktop value. */
  responsive: z.boolean().optional(),
};

/** The groups a `token` prop can draw from — the theme's own groups (see `theme.ts`). */
export const TOKEN_GROUPS = ['color', 'font', 'space', 'text', 'shadow', 'metric'] as const;

export const propDefinitionSchema = z.discriminatedUnion('kind', [
  z.strictObject({
    kind: z.literal('text'),
    ...base,
    default: z.string().optional(),
    multiline: z.boolean().optional(),
    maxLength: z.number().int().positive().optional(),
  }),
  z.strictObject({ kind: z.literal('richText'), ...base, default: z.string().optional() }),
  z.strictObject({
    kind: z.literal('number'),
    ...base,
    default: z.number().optional(),
    min: z.number().optional(),
    max: z.number().optional(),
    step: z.number().positive().optional(),
  }),
  z.strictObject({ kind: z.literal('boolean'), ...base, default: z.boolean().optional() }),
  z.strictObject({
    kind: z.literal('select'),
    ...base,
    options: z.array(z.strictObject({ value: z.string(), label: z.string() })).min(1),
    default: z.string().optional(),
  }),
  z.strictObject({ kind: z.literal('token'), ...base, group: z.enum(TOKEN_GROUPS), default: z.string().optional() }),
  z.strictObject({ kind: z.literal('color'), ...base, default: z.string().optional() }),
  z.strictObject({ kind: z.literal('media'), ...base }),
  z.strictObject({ kind: z.literal('url'), ...base, default: z.string().optional() }),
  z.strictObject({ kind: z.literal('contentRef'), ...base, contentType: z.string().optional() }),
  z.strictObject({ kind: z.literal('date'), ...base, default: z.string().optional() }),
  z.strictObject({ kind: z.literal('icon'), ...base, default: z.string().optional() }),
]);

export type PropDefinition = z.infer<typeof propDefinitionSchema>;
export type PropKind = PropDefinition['kind'];

/** `#rgb`, `#rgba`, `#rrggbb`, `#rrggbbaa`. */
const HEX_COLOR = /^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$/;
const TOKEN = /^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$/;

/**
 * The schema a value of this prop must satisfy.
 *
 * This is where the security-relevant kinds are narrowed. A `url` is an allow-listed link,
 * never `javascript:`; a `color` is a theme token or a hex literal, never an arbitrary CSS
 * expression — both end up in markup inside the admin's own origin on the canvas.
 */
export function propValueSchema(def: PropDefinition): z.ZodType {
  switch (def.kind) {
    case 'text':
      return z.string().max(def.maxLength ?? 10_000);
    case 'richText':
      return z.string().max(100_000);
    case 'number': {
      let schema = z.number().finite();
      if (def.min !== undefined) schema = schema.min(def.min);
      if (def.max !== undefined) schema = schema.max(def.max);
      return schema;
    }
    case 'boolean':
      return z.boolean();
    case 'select':
      return z.enum(def.options.map((o) => o.value) as [string, ...string[]]);
    case 'token':
      return z.string().regex(TOKEN, 'must be a theme token name');
    case 'color':
      return z.string().refine((v) => HEX_COLOR.test(v) || TOKEN.test(v), 'must be a theme colour or a #hex value');
    case 'media':
      return z.string().min(1).max(2048);
    case 'url':
      return z
        .string()
        .refine((v) => isInternalPath(v) || isSafeExternalHref(v), 'must be a path on this site or an http(s), mailto or tel link');
    case 'contentRef':
      return z.string().min(1).max(200);
    case 'date':
      return z.union([z.iso.date(), z.iso.datetime({ offset: true })]);
    case 'icon':
      return z.string().regex(/^[a-z0-9-]{1,64}$/, 'must be an icon name');
  }
}
