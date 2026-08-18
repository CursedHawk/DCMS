import {
  identityClassOf,
  themeVariables,
  type DcmsComponentSpec,
  type ThemeTokens,
} from '@dcms/gjs-schema';
import { api } from '../../../lib/api';

/**
 * AI generation for the builder.
 *
 * The catalogue and the theme travel with the request rather than being known
 * server-side: both are defined in TypeScript, and the catalogue is per-tenant
 * once generated plugin components are in it. Sending them is what lets the model
 * emit `class="dcms-hero"` and `var(--dcms-color-primary)` — markup that comes
 * back as real, editable components styled like the rest of the site, rather than
 * a generic template the author then has to rewrite.
 */

export interface GeneratedSource {
  html: string;
  css: string;
}

export interface GeneratedSite {
  files: Record<string, string>;
}

interface GenerationResponse {
  valid: boolean;
  result?: unknown;
  raw?: string;
}

interface ComponentHint {
  type: string;
  label: string;
  tag: string;
  identityClass: string;
  category: string;
  docs?: string;
  acceptsChildren: boolean;
}

function hints(specs: readonly DcmsComponentSpec[]): ComponentHint[] {
  return specs.map((spec) => ({
    type: spec.type,
    label: spec.label,
    tag: spec.tag,
    identityClass: identityClassOf(spec),
    category: spec.category,
    docs: spec.docs,
    acceptsChildren: spec.acceptsChildren,
  }));
}

export interface GenerationContext {
  specs: readonly DcmsComponentSpec[];
  theme: ThemeTokens;
}

function envelope(context: GenerationContext) {
  return {
    components: hints(context.specs),
    // Names *and* values: knowing `--dcms-color-primary` is a deep blue is what
    // stops the model pairing it with a clashing hard-coded colour.
    themeVariables: themeVariables(context.theme).map((v) => `${v.name}: ${v.value}`),
  };
}

/** Thrown when the model answered but not in the shape we asked for. */
export class UnusableGenerationError extends Error {
  constructor(readonly raw: string | undefined) {
    super('The model did not return usable output.');
    this.name = 'UnusableGenerationError';
  }
}

function unwrap<T>(response: GenerationResponse, check: (value: unknown) => value is T): T {
  if (!response.valid || !check(response.result)) {
    throw new UnusableGenerationError(response.raw ?? stringify(response.result));
  }
  return response.result;
}

function stringify(value: unknown): string | undefined {
  try {
    return value === undefined ? undefined : JSON.stringify(value, null, 2);
  } catch {
    return undefined;
  }
}

function isSource(value: unknown): value is GeneratedSource {
  if (!value || typeof value !== 'object') return false;
  const record = value as Record<string, unknown>;
  // CSS is optional: a section built entirely from catalogue classes needs none.
  return typeof record.html === 'string' && (record.css === undefined || typeof record.css === 'string');
}

function isSite(value: unknown): value is GeneratedSite {
  if (!value || typeof value !== 'object') return false;
  const files = (value as Record<string, unknown>).files;
  if (!files || typeof files !== 'object' || Array.isArray(files)) return false;
  return Object.values(files as Record<string, unknown>).every((v) => typeof v === 'string');
}

const normalize = (source: GeneratedSource): GeneratedSource => ({
  html: source.html ?? '',
  css: source.css ?? '',
});

export const aiApi = {
  /** One section to insert into the page the author is looking at. */
  block: async (
    instruction: string,
    context: GenerationContext & { pageHtml?: string },
  ): Promise<GeneratedSource> => {
    const response = await api.post<GenerationResponse>('/admin/ai/generate/block', {
      instruction,
      context: context.pageHtml,
      ...envelope(context),
    });
    return normalize(unwrap(response, isSource));
  },

  /** A whole page body, replacing what is there. */
  page: async (
    instruction: string,
    context: GenerationContext & { pageHtml?: string },
  ): Promise<GeneratedSource> => {
    const response = await api.post<GenerationResponse>('/admin/ai/generate/page', {
      instruction,
      context: context.pageHtml,
      ...envelope(context),
    });
    return normalize(unwrap(response, isSource));
  },

  /** A whole site, as the file map the builder already stores. */
  site: async (instruction: string, context: GenerationContext): Promise<GeneratedSite> => {
    const response = await api.post<GenerationResponse>('/admin/ai/generate/site', {
      instruction,
      ...envelope(context),
    });
    return unwrap(response, isSite);
  },
};
