/** Shapes returned by /admin/audit. Shared by the page, the record dialog and the history tab. */

export interface AuditActor {
  kind: string;
  id: string | null;
  ref: string | null;
  display: string | null;
  /** direct | propagated | inferred. A propagated actor is a peer service's assertion, not
   *  something the recording request authenticated — the UI says so rather than implying
   *  certainty it does not have. */
  attribution: string;
}

export interface FieldChange {
  field: string;
  before: unknown;
  after: unknown;
  /** True when the values were withheld rather than absent. Shown as changed, never hidden. */
  redacted: boolean;
}

export interface AuditRecord {
  id: string;
  occurredAt: string;
  seq: number;
  action: string;
  category: string;
  outcome: string;
  severity?: string;
  actor: AuditActor;
  subjectUserId?: string | null;
  resource: { type: string | null; id: string | null; label: string | null } | null;
  service: string;
  correlationId: string | null;
  http: { method: string | null; route: string | null; status: number | null } | null;
  ipAddress: string | null;
  ipTrusted: boolean;
  isSandbox: boolean;
  /** True for records that belong to no tenant (a sign-in); these come back with less detail. */
  platformScope: boolean;
  metadata: string | null;
  changes: string | null;
  /** Which redaction policy produced `changes`, so a blank field stays interpretable. */
  redactionVersion?: number;
}

export interface AuditPage {
  items: AuditRecord[];
  hasMore: boolean;
  nextCursor: { occurredAt: string; seq: number } | null;
}

export interface VerifySegment {
  period: string;
  checked: number;
  valid: boolean;
  firstBadSeq: number | null;
  reason: string | null;
}
