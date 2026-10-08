import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast, toastApiError } from '@dcms/ui';
import { instancePath, usePluginApi, usePluginT } from '@dcms/plugin-ui';

// ---------------------------------------------------------------------------------------------
// The configuration document (Dcms.Plugins.DynamicApps.Api.Model), as the server sends it.
// ---------------------------------------------------------------------------------------------

export type FieldType =
  | 'text' | 'longText' | 'integer' | 'decimal' | 'boolean' | 'date' | 'dateTime'
  | 'email' | 'url' | 'choice' | 'multiChoice' | 'media' | 'json';

export const FIELD_TYPES: FieldType[] = [
  'text', 'longText', 'integer', 'decimal', 'boolean', 'date', 'dateTime', 'email', 'url', 'choice', 'multiChoice', 'media', 'json',
];

export interface FieldDef {
  id: string;
  apiName: string;
  displayName: string;
  description?: string;
  type: FieldType;
  required: boolean;
  unique: boolean;
  searchable: boolean;
  sortable: boolean;
  filterable: boolean;
  deprecated: boolean;
  readOnly: boolean;
  hiddenFromPublic: boolean;
  default?: unknown;
  maxLength?: number;
  minimum?: number;
  maximum?: number;
  choiceSetId?: string;
  /** When new: the live field of this table whose values it takes over at publish. */
  copyFrom?: string;
}

export interface PublicAccess {
  read: 'none' | 'all' | 'own';
  create: boolean;
  updateOwn: boolean;
  deleteOwn: boolean;
  /** Row access: a signed-in user also reaches each record one of these matches for them. */
  rules?: RowRule[];
}

/** Follow `path` (navigation api names) from a record; it is the user's when `field` there holds what `matches` names of them. */
export interface RowRule {
  path: string[];
  field: string;
  matches: string;
  read: boolean;
  update: boolean;
  delete: boolean;
}

export interface IndexDef {
  id: string;
  apiName: string;
  fieldIds: string[];
  unique: boolean;
}

export interface TableDef {
  id: string;
  apiName: string;
  displayName: string;
  pluralName?: string;
  description?: string;
  primaryFieldId?: string;
  enabled: boolean;
  public: PublicAccess;
  fields: FieldDef[];
  indexes: IndexDef[];
}

export type RelationshipKind = 'manyToOne' | 'oneToOne' | 'manyToMany';

export interface RelationshipDef {
  id: string;
  apiName: string;
  displayName?: string;
  kind: RelationshipKind;
  sourceTableId: string;
  targetTableId: string;
  inverseApiName?: string;
  required: boolean;
  onDelete: 'restrict' | 'setNull' | 'cascade';
  /** When new: the live text field of the source table holding the record ids it takes over at publish. */
  copyFrom?: string;
}

export interface ChoiceSetDef {
  id: string;
  apiName: string;
  displayName: string;
  options: { value: string; label: string; color?: string }[];
}

export interface ViewDef {
  id: string;
  apiName: string;
  displayName: string;
  tableId: string;
  columns: string[];
  sort: { fieldId: string; descending: boolean }[];
  isDefault: boolean;
}

export interface FlowStep {
  id: string;
  action: string;
  input: Record<string, unknown>;
  condition?: string;
}

export interface FlowDef {
  id: string;
  apiName: string;
  displayName: string;
  description?: string;
  enabled: boolean;
  trigger: { event: string; tableId?: string; relationshipId?: string; changedFields: string[]; everyMinutes?: number };
  condition?: string;
  steps: FlowStep[];
}

export interface AppConfig {
  schemaVersion: number;
  settings: { description?: string };
  tables: TableDef[];
  relationships: RelationshipDef[];
  choiceSets: ChoiceSetDef[];
  views: ViewDef[];
  flows: FlowDef[];
}

export interface RevisionInfo {
  id: string;
  number: number;
  status: 'draft' | 'published' | 'superseded' | 'rolledBack' | 'discarded';
  source: 'human' | 'ai' | 'system' | 'import';
  hash: string;
  parentId?: string;
  basePublishedId?: string;
  description?: string;
  createdBy?: string;
  createdAt: string;
  updatedAt: string;
  validatedAt?: string;
  publishedAt?: string;
  publishedBy?: string;
  sourceConversationId?: string;
}

export interface AppState {
  draft: RevisionInfo | null;
  published: RevisionInfo | null;
  hash: string;
}

export interface RevisionDocument {
  revision: RevisionInfo;
  config: AppConfig;
}

export interface ConfigChange {
  op: 'create' | 'update' | 'delete';
  resourceType: string;
  resourceId?: string;
  path: string;
  before?: unknown;
  after?: unknown;
  destructive: boolean;
}

export interface ConfigIssue {
  severity: 'error' | 'warning';
  code: string;
  path: string;
  message: string;
}

export interface ChangeOperation {
  op: 'create' | 'update' | 'delete';
  type: 'settings' | 'table' | 'field' | 'index' | 'relationship' | 'choiceSet' | 'view' | 'flow';
  target?: string;
  value?: Record<string, unknown>;
}

export interface RevisionPreview {
  draft: RevisionInfo;
  published: RevisionInfo | null;
  changes: ConfigChange[];
  issues: ConfigIssue[];
  canPublish: boolean;
  hasDestructiveChanges: boolean;
}

export interface PublishResult {
  published: boolean;
  revision: RevisionInfo | null;
  issues: ConfigIssue[];
}

export interface FlowActionInfo {
  key: string;
  id: string;
  major: number;
  description: string;
  risk: 'read' | 'safe' | 'dangerous';
  inputSchema: Record<string, unknown>;
}

export interface FlowRunSummary {
  id: string;
  flow: string;
  revision: number;
  status: 'pending' | 'running' | 'succeeded' | 'skipped' | 'failed' | 'terminated';
  trigger: string;
  attempts: number;
  depth: number;
  writes: number;
  correlationId: string;
  causationId?: string;
  error?: string;
  createdAt: string;
  startedAt?: string;
  finishedAt?: string;
}

export interface FlowRunDetail {
  run: FlowRunSummary;
  triggerEvent: Record<string, unknown> | null;
  steps: {
    stepId: string;
    attempt: number;
    action: string;
    status: FlowRunSummary['status'];
    input: unknown;
    output: unknown;
    error?: string;
    startedAt: string;
    finishedAt?: string;
  }[];
  chain: FlowRunSummary[];
}

export type RecordRow = Record<string, unknown> & { id: string; version: number; created_at: string; updated_at: string };

export interface RecordPage {
  items: RecordRow[];
  total: number;
  page: number;
  pageSize: number;
}

export interface RecordQuery {
  filter?: unknown;
  sort?: { field: string; direction: 'asc' | 'desc' }[];
  search?: string;
  expand?: string[];
  page?: number;
  pageSize?: number;
}

// ---------------------------------------------------------------------------------------------
// Queries. Every key is rooted at `plugin:{slug}`: what the console's assistant invalidates
// after it changes this instance, so a screen shows an agent's work as it happens.
// ---------------------------------------------------------------------------------------------

export const rootKey = (slug: string) => `plugin:${slug}`;

export function useAppState(slug: string) {
  const api = usePluginApi();
  return useQuery({
    queryKey: [rootKey(slug), 'state'],
    queryFn: () => api.get<AppState>(instancePath(slug, '/_model')),
  });
}

/** What the editor shows and changes: the draft when one is open, else the live configuration. */
export function useWorkingConfig(slug: string, state: AppState | undefined) {
  const api = usePluginApi();
  const which = state?.draft ? 'draft' : state?.published ? 'published' : null;
  return useQuery({
    queryKey: [rootKey(slug), 'document', which, state?.hash],
    queryFn: async () =>
      which ? api.get<RevisionDocument>(instancePath(slug, `/_model/${which}`)) : null,
    enabled: state !== undefined,
  });
}

/**
 * Applies a change set against the hash the screen last read. A conflict means someone — a
 * colleague, the assistant — changed the draft meanwhile: the screen says so and reloads rather
 * than overwriting their work.
 */
export function useApplyChanges(slug: string) {
  const api = usePluginApi();
  const qc = useQueryClient();
  const { t } = usePluginT();
  return useMutation({
    mutationFn: async (input: { operations: ChangeOperation[]; description?: string }) => {
      const state = qc.getQueryData<AppState>([rootKey(slug), 'state'])
        ?? (await api.get<AppState>(instancePath(slug, '/_model')));
      return api.post<{ draft: RevisionInfo; changes: ConfigChange[]; issues: ConfigIssue[] }>(
        instancePath(slug, '/_model/draft/changes'),
        { expectedHash: state.hash, operations: input.operations, description: input.description },
      );
    },
    onSuccess: (result) => {
      const errors = result.issues.filter((i) => i.severity === 'error').length;
      if (errors > 0) toast.warning(t('config.savedWithIssues', { count: errors }));
    },
    onError: (error) => {
      if ((error as { status?: number }).status === 409) {
        toast.error(t('config.conflict'));
      } else {
        toastApiError(error, t);
      }
    },
    onSettled: () => qc.invalidateQueries({ queryKey: [rootKey(slug)] }),
  });
}

/** A one-call action on the configuration (validate, publish, discard, rollback) that refreshes the screen. */
export function useModelAction<TInput, TResult>(slug: string, run: (input: TInput) => Promise<TResult>) {
  const qc = useQueryClient();
  const { t } = usePluginT();
  return useMutation({
    mutationFn: run,
    onError: (error) => {
      const status = (error as { status?: number }).status;
      if (status === 409) toast.error(t('config.conflict'));
      else if (status !== 422) toastApiError(error, t);
    },
    onSettled: () => qc.invalidateQueries({ queryKey: [rootKey(slug)] }),
  });
}

/** A publish or rollback the server refused (422): its result, with the issues that say why. */
export function refusedOf(error: unknown): PublishResult | null {
  const { status, detail } = error as { status?: number; detail?: unknown };
  return status === 422 && detail && Array.isArray((detail as PublishResult).issues) ? (detail as PublishResult) : null;
}

export function usePreview(slug: string, enabled: boolean) {
  const api = usePluginApi();
  return useQuery({
    queryKey: [rootKey(slug), 'preview'],
    queryFn: () => api.get<RevisionPreview>(instancePath(slug, '/_model/draft/preview')),
    enabled,
  });
}

export function useRevisions(slug: string) {
  const api = usePluginApi();
  return useQuery({
    queryKey: [rootKey(slug), 'revisions'],
    queryFn: () => api.get<{ items: RevisionInfo[]; total: number }>(instancePath(slug, '/_model/revisions?pageSize=100')),
  });
}

export function useRevisionChanges(slug: string, number: number | null) {
  const api = usePluginApi();
  return useQuery({
    queryKey: [rootKey(slug), 'revision', number, 'changes'],
    queryFn: () => api.get<ConfigChange[]>(instancePath(slug, `/_model/revisions/${number}/changes`)),
    enabled: number !== null,
  });
}

export function usePublicModel(slug: string, enabled: boolean) {
  const api = usePluginApi();
  return useQuery({
    queryKey: [rootKey(slug), 'public'],
    queryFn: () => api.get<{ revision: number; tables: { apiName: string; access: PublicAccess & { rowRules?: boolean }; fields: { apiName: string; type: string }[] }[] }>(
      instancePath(slug, '/_model/public')),
    enabled,
    retry: false,
  });
}

export function useActions(slug: string) {
  const api = usePluginApi();
  return useQuery({
    queryKey: [rootKey(slug), 'actions'],
    queryFn: () => api.get<FlowActionInfo[]>(instancePath(slug, '/_automation/actions')),
    staleTime: 10 * 60_000,
  });
}

export function useRuns(slug: string, flow: string | null, status: string | null) {
  const api = usePluginApi();
  const query = new URLSearchParams({ pageSize: '50' });
  if (flow) query.set('flow', flow);
  if (status) query.set('status', status);
  return useQuery({
    queryKey: [rootKey(slug), 'runs', flow, status],
    queryFn: () => api.get<{ items: FlowRunSummary[]; total: number }>(instancePath(slug, `/_automation/runs?${query}`)),
  });
}

export function useRun(slug: string, id: string | null) {
  const api = usePluginApi();
  return useQuery({
    queryKey: [rootKey(slug), 'run', id],
    queryFn: () => api.get<FlowRunDetail>(instancePath(slug, `/_automation/runs/${id}`)),
    enabled: id !== null,
  });
}

export function useRecords(slug: string, table: string | null, query: RecordQuery) {
  const api = usePluginApi();
  return useQuery({
    queryKey: [rootKey(slug), 'records', table, query],
    queryFn: () => api.post<RecordPage>(instancePath(slug, `/_records/${table}/query`), query),
    enabled: table !== null,
    placeholderData: (previous) => previous,
  });
}

/** Published model for the records screen: what the data plane serves now. */
export function usePublished(slug: string) {
  const api = usePluginApi();
  return useQuery({
    queryKey: [rootKey(slug), 'document', 'published-only'],
    queryFn: async () => {
      try {
        return await api.get<RevisionDocument>(instancePath(slug, '/_model/published'));
      } catch (error) {
        if ((error as { status?: number }).status === 404) return null;
        throw error;
      }
    },
  });
}

// ---------------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------------

/** `Deal amount` → `deal_amount`: a valid api name proposed from a display name. */
export function apiNameOf(label: string): string {
  const name = label
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '')
    .slice(0, 63);
  return /^[a-z]/.test(name) ? name : name ? `f_${name}`.slice(0, 63) : '';
}

export const API_NAME = /^[a-z][a-z0-9_]{0,62}$/;

export interface ValidationResultLike {
  revision: RevisionInfo;
  issues: ConfigIssue[];
  valid: boolean;
}
