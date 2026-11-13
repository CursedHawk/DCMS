import { queryClient } from '../../lib/queryClient';
import { api } from '../../lib/api';
import type { ToolSpec } from './contracts';
import type { TenantToolContext } from '../ide/agent/tenantTools';
import type { SiteAgentKind } from '../ide/agent/systemPrompt';

/**
 * What a site agent of each kind is given: its tools and its deterministic gate (ADR 0033).
 *
 * <p>This used to be assembled by each editor page and passed into the panel, which meant a run
 * could only be started by the page that knew how to build its kit. An agent the console
 * assistant asks for has no page at all, so the kit is decided here, from the kind alone, and
 * every way of starting a run gets the same agent.</p>
 */
export interface SiteKit {
  tools: ToolSpec<TenantToolContext>[];
  /** A kind's own gate. Absent for Mode B, whose gate is types + build over the run's files. */
  validate?: (files: Readonly<Record<string, string>>) => Promise<{ ok: boolean; report: string } | null>;
  /** The TypeScript `lib` to check against, when it is not the browser's. */
  typeLib?: string[];
}

/**
 * Tools that only mean something for a Mode B site.
 *
 * <p>Removed from other kinds rather than left to fail: a model told a tool exists will reach
 * for it, then spend a turn recovering from an answer that was never coming.</p>
 */
const REACT_ONLY_TOOLS: ReadonlySet<string> = new Set([
  'check_types',
  'check_build',
  'last_build',
  'dependencies',
  'preview_console',
  'preview_dom',
  'preview_text',
]);

/** The agent kind for a site's render mode, or null for one no agent works on. */
export function kindOfRenderMode(renderMode: string | undefined): SiteAgentKind | null {
  switch (renderMode) {
    case 'ReactApp':
      return 'react';
    case 'StaticPrerender':
      return 'static';
    case 'ReactBuilder':
      return 'visual';
    default:
      // StaticFiles is a file drop with nothing to edit; anything newer is unknown here.
      return null;
  }
}

export async function kitFor(
  kind: SiteAgentKind,
  opts: { functionsApi?: string; siteId?: string } = {},
): Promise<SiteKit> {
  const [{ ALL_TOOLS }, { PREVIEW_TOOLS, SKILL_TOOLS, TENANT_TOOLS, fromAssistantTool }, { knowledgeTools }] = await Promise.all([
    import('../ide/agent/checkTools'),
    import('../ide/agent/tenantTools'),
    import('./knowledgeTools'),
  ]);
  // Every kind, at every scope: the workspace's knowledge is not tenant reach (ADR 0034).
  const knowledge = opts.siteId ? knowledgeTools(opts.siteId).map(fromAssistantTool) : [];
  const base = (ALL_TOOLS as ToolSpec<TenantToolContext>[]).filter(
    (tool) => kind === 'react' || !REACT_ONLY_TOOLS.has(tool.name),
  );
  const reach = [...SKILL_TOOLS, ...PREVIEW_TOOLS, ...TENANT_TOOLS, ...knowledge];

  switch (kind) {
    case 'react':
      return { tools: [...base, ...reach] };

    case 'functions': {
      if (!opts.functionsApi) throw new Error('A functions agent needs its repository.');
      const { checkDraft, functionsTools } = await import('../ide/agent/functionsTools');
      const api = opts.functionsApi;
      return {
        // A repository of functions: its files, its types and git; no site, preview or skills.
        tools: [
          ...(ALL_TOOLS as ToolSpec<TenantToolContext>[]).filter(
            (tool) => tool.name === 'check_types' || !REACT_ONLY_TOOLS.has(tool.name),
          ),
          ...functionsTools(api),
          ...knowledge,
        ],
        validate: (files) => checkDraft(api, files),
        // A function runs with the language and the web's worker APIs only (ADR 0029).
        typeLib: ['esnext', 'webworker'],
      };
    }

    case 'static': {
      // Lazy, as the builder page loads them: the block catalogue they reach is not small.
      const { STATIC_TOOLS, validateStaticSite } = await import('../builder/agent/staticTools');
      return {
        tools: [...base, ...(STATIC_TOOLS as ToolSpec<TenantToolContext>[]), ...reach],
        validate: validateStaticSite,
      };
    }

    case 'visual': {
      const [{ VISUAL_TOOLS, validateVisualSite }, { siteAgentAppTools }, { contractTools }] = await Promise.all([
        import('../visual/agent/visualTools'),
        import('../visual/agent/appModelTools'),
        import('../assistant/contractTools'),
      ]);
      // The same cache entry `useContractTools` reads, so a page that has it loaded costs nothing.
      const catalog = await queryClient
        .fetchQuery({
          queryKey: ['contracts', 'ai'],
          queryFn: () => api.get<Parameters<typeof contractTools>[0]>('/admin/contracts?plane=ai'),
          staleTime: 60_000,
        })
        .catch(() => []);
      const appTools = siteAgentAppTools(Array.isArray(catalog) ? contractTools(catalog) : []);
      return {
        tools: [...base, ...VISUAL_TOOLS, ...appTools, ...reach],
        validate: validateVisualSite,
      };
    }
  }
}
