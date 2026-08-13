import { useQuery } from '@tanstack/react-query';
import { api } from '../../lib/api';

/** A field as declared on the form's plugin-instance config. */
export interface FormFieldDef {
  name: string;
  label: string;
  type: 'text' | 'email' | 'date' | 'number' | 'textarea' | 'checkbox' | string;
  required: boolean;
}

export interface FormDef {
  name: string;
  title: string;
  fields: FormFieldDef[];
  /** Addresses notified on each submission; empty when notifications are off. */
  notifyRecipients: string[];
  totalCount: number;
  unhandledCount: number;
}

/** One Forms plugin instance and the forms it declares. */
export interface FormsInstance {
  instanceId: string;
  slug: string;
  name: string;
  enabled: boolean;
  forms: FormDef[];
}

export interface Submission {
  id: string;
  formName: string;
  /** Exactly what the visitor sent, keyed by field name. */
  data: Record<string, unknown>;
  submittedAt: string;
  handledAt: string | null;
  userAgent: string | null;
}

export interface SubmissionPage {
  items: Submission[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export type HandledFilter = 'all' | 'unhandled' | 'handled';

export const PAGE_SIZE = 25;

export function useFormsInstances() {
  return useQuery({
    queryKey: ['forms-instances'],
    queryFn: () => api.get<FormsInstance[]>('/admin/forms'),
  });
}

export function useSubmissions(
  instanceId: string | undefined,
  formName: string | undefined,
  handled: HandledFilter,
  page: number,
) {
  return useQuery({
    queryKey: ['form-submissions', instanceId, formName, handled, page],
    enabled: !!instanceId && !!formName,
    queryFn: () => {
      const params = new URLSearchParams({
        formName: formName ?? '',
        page: String(page),
        pageSize: String(PAGE_SIZE),
      });
      if (handled !== 'all') params.set('handled', handled);
      return api.get<SubmissionPage>(`/admin/forms/${instanceId}/submissions?${params}`);
    },
  });
}

/**
 * A submitted value as a single line of text. Booleans read as yes/no rather
 * than "true", and anything unexpected (a form whose fields changed after the
 * submission was stored) falls back to its JSON form instead of "[object Object]".
 */
export function formatValue(value: unknown, yes: string, no: string): string {
  if (value === null || value === undefined || value === '') return '—';
  if (typeof value === 'boolean') return value ? yes : no;
  if (typeof value === 'string' || typeof value === 'number') return String(value);
  return JSON.stringify(value);
}

/** Field names present on a submission but no longer declared on the form. */
export function extraKeys(submission: Submission, fields: FormFieldDef[]): string[] {
  const declared = new Set(fields.map((f) => f.name));
  return Object.keys(submission.data).filter((k) => !declared.has(k));
}
