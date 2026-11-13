import { aiPrompt, type AiPromptKey, type AiPromptOverrides, fillPrompt } from '@dcms/core';

/**
 * The site agent's system prompt.
 *
 * <h3>What is deliberately NOT in here</h3>
 * <p>The file list and the tenant's OpenAPI spec used to be pasted in whole, on every turn of
 * every run. On a real site that was the single largest line item in the bill, and most of it was
 * never read: the model needs to know what exists when it is looking for something, not while it
 * is writing a sentence.</p>
 *
 * <p>Both are now tools. `project_overview` answers "what is this site" in one cheap call, and
 * `search` answers "where is X" without reading anything. The prompt's job is to say what the
 * environment is and how to work in it — the things that are true on every turn and cheap to
 * state.</p>
 *
 * <p>The same reasoning is why the Mode A prompt is short. A Mode A site carries its own
 * generated `AGENTS.md` — the file layout, every theme variable, the whole block catalogue with
 * its identity classes, the binding attributes — rewritten from this site's own manifest on
 * every save. Restating any of that here would be a second copy that can only be staler than
 * the first, so the prompt points at it instead.</p>
 */

/**
 * Which kind of site this run is editing: a React app (Mode B), static HTML/CSS (Mode A), or a
 * visually built React app whose pages are component trees (Mode D, ADR 0020).
 */
export type SiteAgentKind = 'react' | 'static' | 'visual' | 'functions';

export interface SystemPromptContext {
  kind?: SiteAgentKind;
  siteName?: string;
  /** The tenant's overrides, set from the platform console. Absent means the shipped prompts. */
  overrides?: AiPromptOverrides | null;
}

/** The text of each kind's prompt lives in `@dcms/core`'s catalogue, where the console shows it. */
export function buildSystemPrompt(ctx: SystemPromptContext): string {
  const key: AiPromptKey = `site.${ctx.kind ?? 'react'}`;
  const prompt = fillPrompt(aiPrompt(key, ctx.overrides), {
    site: ctx.siteName ? ` ("${ctx.siteName}")` : '',
    rules: aiPrompt('site.rules', ctx.overrides),
  });
  return `${prompt}\n\n${aiPrompt('knowledge', ctx.overrides)}`;
}
