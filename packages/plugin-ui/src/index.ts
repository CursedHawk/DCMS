/**
 * @dcms/plugin-ui — what a DCMS plugin builds its admin screens with (docs/adr/0019).
 *
 * A plugin's admin UI is one module whose default export is `definePluginAdmin({...})`:
 * React components for the screens its manifest declares (`AdminScreens`), RJSF widgets for
 * custom `format`s in its config or data-set schemas, and its translations. The console loads
 * the module when one of those is needed and renders it inside its own shell, so a plugin
 * screen gets the console's theme, components (`@dcms/ui`), data cache, permissions and
 * language without doing anything.
 *
 * Everything a screen needs from the console comes through the hooks below, never through
 * imports of console internals: that is what lets the same module run compiled into the
 * console (built-in plugins) or loaded from an installed plugin's folder.
 */
import { createContext, createElement, useContext, useEffect, useRef, type ComponentType, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import type { ApiClient } from '@dcms/core';

export { SHARED_MODULES } from './shared';

/** One instance of the plugin in the current workspace. */
export interface PluginInstanceRef {
  id: string;
  slug: string;
  name: string;
  enabled: boolean;
  /** Whether the assistant may use this instance's contracts (the instance's "AI tools" switch). */
  aiToolsEnabled?: boolean;
}

/** What a screen component receives. */
export interface PluginScreenProps {
  pluginId: string;
  screenId: string;
  /** The instance an instance-scoped screen belongs to; null on a plugin-wide screen. */
  instance: PluginInstanceRef | null;
  /** Every instance of the plugin in this workspace, enabled or not. */
  instances: PluginInstanceRef[];
}

/**
 * The props of an RJSF widget, kept structural so a plugin does not need @rjsf as a dependency
 * just to type one. `formContext.instanceId` is the instance whose config is being edited.
 */
export interface PluginWidgetProps {
  id: string;
  value: unknown;
  onChange: (value: unknown) => void;
  disabled?: boolean;
  readonly?: boolean;
  schema: Record<string, unknown>;
  options?: Record<string, unknown>;
  registry?: { formContext?: { instanceId?: string } & Record<string, unknown> };
}

export interface PluginAdminModule {
  /** Components by the screen ids the manifest declares. */
  screens?: Record<string, ComponentType<PluginScreenProps>>;
  /** RJSF widgets by the `format` they render (`"format": "meta-connection"`). */
  configWidgets?: Record<string, ComponentType<PluginWidgetProps>>;
  /** Translations by language, read with `usePluginT()`. Missing keys fall back to the console's own. */
  locales?: Record<string, Record<string, unknown>>;
}

/** The default export of a plugin's admin module. Identity at runtime; there for the types. */
export function definePluginAdmin(module: PluginAdminModule): PluginAdminModule {
  return module;
}

/**
 * What a screen is showing, for the console's assistant: an area, one sentence, the ids of what
 * is selected. Never the data itself — the assistant fetches that through its tools.
 */
export interface PluginAiContext {
  area: string;
  summary: string;
  selection?: readonly string[];
}

/** The console's services, as a screen sees them. Provided by the console; never constructed by a plugin. */
export interface PluginHost {
  /** The admin API client: `/admin/...` paths, signed in, with the current workspace. */
  api: ApiClient;
  /** The current workspace's slug. */
  tenant: string | null;
  /** Headers for a request the client cannot make (a SignalR hub, a raw fetch): auth and workspace. */
  authHeaders(): Promise<Record<string, string>>;
  /** Base URL of the public site API (content-api), for hubs and site calls. */
  contentApiBase: string;
  /** Whether the signed-in member holds a permission key. */
  can(permission: string): boolean;
  navigate(to: string): void;
  /** The console's assistant: what the screen shows, and a way to open it. Absent outside the console shell. */
  assistant?: {
    setContext(context: PluginAiContext | null): void;
    /** Opens the assistant; with a prompt, puts it in the message box for the member to send (never sends it). */
    open(prompt?: string): void;
    /**
     * Turns on the instance's "AI tools" switch, so the assistant can use its contracts, and
     * refreshes the assistant's tools. Needs `plugins:manage`, as the switch does in settings.
     */
    enableTools(instanceId: string): Promise<void>;
  };
  /** Console components a plugin may embed. */
  components: {
    /** A data set of an instance, rendered as the plugin page renders it (search, filters, editing). */
    DataSet: ComponentType<{ slug: string; set: string }>;
    /** The console's code editor (Monaco), with its language workers and themes already set up. */
    CodeEditor: ComponentType<CodeEditorProps>;
  };
}

/** A problem to underline in a {@link CodeEditorProps.value}, by 0-based UTF-16 offsets. */
export interface CodeEditorMarker {
  message: string;
  start: number;
  end: number;
  severity?: 'error' | 'warning';
}

export interface CodeEditorProps {
  /**
   * Names the document, e.g. `file:///dcms/dynamic-apps/flows/{id}/condition.dexpr`. Stable for
   * as long as the same text is edited: a new uri is a new document (and a fresh undo stack).
   */
  uri: string;
  /** A Monaco language id: `typescript`, `javascript`, `json`, or one a plugin registers through `onMonaco`. */
  language: string;
  value: string;
  onChange?: (value: string) => void;
  readOnly?: boolean;
  /** In pixels. Default 240. */
  height?: number;
  /** No line numbers, folding or gutter: a field holding an expression rather than a file. */
  compact?: boolean;
  /** For the label that names the field. */
  ariaLabel?: string;
  markers?: readonly CodeEditorMarker[];
  /** Declaration files (`.d.ts` text) TypeScript and JavaScript see while this editor is mounted. */
  extraLibs?: readonly { path: string; content: string }[];
  /** A JSON Schema the `json` language validates and completes this document against. */
  jsonSchema?: Record<string, unknown>;
  /**
   * Runs once before the editor is created, with the `monaco-editor` namespace (type it with
   * `import type * as Monaco from 'monaco-editor'`): for registering a plugin's own language.
   * Whatever it returns is disposed on unmount.
   */
  onMonaco?: (monaco: unknown) => { dispose(): void } | void;
}

const HostContext = createContext<PluginHost | null>(null);
const ScreenContext = createContext<PluginScreenProps | null>(null);

export function PluginHostProvider({ host, children }: { host: PluginHost; children: ReactNode }) {
  return createElement(HostContext.Provider, { value: host }, children);
}

/** Set by the console around each screen and widget it renders for a plugin. */
export function PluginScreenProvider({ screen, children }: { screen: PluginScreenProps; children: ReactNode }) {
  return createElement(ScreenContext.Provider, { value: screen }, children);
}

export function usePluginHost(): PluginHost {
  const host = useContext(HostContext);
  if (!host) throw new Error('usePluginHost: not inside the DCMS console (no PluginHostProvider).');
  return host;
}

/** The screen being rendered: plugin, instance and the plugin's instances. */
export function usePluginScreen(): PluginScreenProps {
  const screen = useContext(ScreenContext);
  if (!screen) throw new Error('usePluginScreen: not inside a plugin screen or widget.');
  return screen;
}

/** The admin API client. Paths are relative to `/api`: `api.get('/admin/forms')`. */
export function usePluginApi(): ApiClient {
  return usePluginHost().api;
}

/** `/admin/plugins/{slug}{path}`: where an instance's own admin routes (MapAdminEndpoints) live. */
export function instancePath(slug: string, path = ''): string {
  return `/admin/plugins/${encodeURIComponent(slug)}${path}`;
}

/** The i18n namespace a plugin's `locales` are registered under. */
export function pluginNamespace(pluginId: string): string {
  return `plugin.${pluginId}`;
}

/**
 * Translations: the plugin's own `locales` first, then the console's (`actions.save`,
 * `common.saved`), so a screen uses the console's words for the console's actions.
 */
export function usePluginT() {
  const { pluginId } = usePluginScreen();
  return useTranslation([pluginNamespace(pluginId), 'common']);
}

/** The full key a permission reference means for a plugin: a bare action is its own. */
export function resolvePermission(pluginId: string, permission: string): string {
  return permission.includes(':') ? permission : `plugin:${pluginId}:${permission}`;
}

/** Whether the signed-in member holds a permission: `useCan('moderate')` or `useCan('content:write')`. */
export function useCan(permission: string): boolean {
  const host = usePluginHost();
  const { pluginId } = usePluginScreen();
  return host.can(resolvePermission(pluginId, permission));
}

/**
 * Tells the console's assistant what this screen shows, for as long as it is mounted — so
 * "add a status field here" means this table. Compared by value: a new object with the same
 * content each render costs nothing.
 */
export function usePluginAiContext(context: PluginAiContext | null): void {
  const assistant = usePluginHost().assistant;
  const latest = useRef(context);
  latest.current = context;
  const identity = context === null ? '' : [context.area, context.summary, ...(context.selection ?? [])].join('\u0000');
  useEffect(() => {
    if (!assistant) return undefined;
    assistant.setContext(latest.current);
    return () => assistant.setContext(null);
  }, [identity, assistant]);
}

/** Opens the console's assistant beside the screen, optionally with a question ready to send; null outside the console shell. */
export function useOpenAssistant(): ((prompt?: string) => void) | null {
  const assistant = usePluginHost().assistant;
  return assistant ? (prompt?: string) => assistant.open(prompt) : null;
}

/** A background job (ADR 0024), as `GET /admin/jobs/{id}` returns it. */
export interface PluginJob {
  id: string;
  parentId: string | null;
  kind: string;
  titleKey: string;
  params: Record<string, unknown>;
  status: 'Queued' | 'Running' | 'Succeeded' | 'Failed' | 'Canceled';
  done: number;
  /** Null while the amount of work is unknown. */
  total: number | null;
  stageKey: string | null;
  errorKey: string | null;
  errorDetail: string | null;
  cancelable: boolean;
  retryable: boolean;
  createdAt: string;
  finishedAt: string | null;
  seq: number;
}

/**
 * One job and its items, kept live: the console patches this cache entry as the job's progress
 * arrives over its socket, so a screen that enqueued an import (`dcms.jobs@2` returns the id)
 * can show the bar without polling. The key is the console's own job key; keep them in step.
 */
export function useJob(jobId: string | null | undefined) {
  const host = usePluginHost();
  return useQuery({
    queryKey: ['jobs', host.tenant, 'detail', jobId ?? ''],
    queryFn: () => host.api.get<{ job: PluginJob; children: PluginJob[] }>(`/admin/jobs/${jobId}`),
    enabled: !!jobId,
    staleTime: 30_000,
  });
}
