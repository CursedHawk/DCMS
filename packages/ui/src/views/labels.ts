import type { UiOp, UserAttribute } from './model';

/** Every word the view editors show; English by default, the consoles pass their own. */
export interface ViewLabels {
  matchAll: string;
  matchAny: string;
  matchPrefix: string;
  addCondition: string;
  addGroup: string;
  removeCondition: string;
  removeGroup: string;
  field: string;
  operator: string;
  value: string;
  yes: string;
  no: string;
  noConditions: string;
  op: Record<UiOp, string>;
  /** The value of a condition: one typed in, or the current user's (#302). */
  valueSource: string;
  typedValue: string;
  currentUser: Record<UserAttribute, string>;
  columns: string;
  showColumn: (label: string) => string;
  moveUp: (label: string) => string;
  moveDown: (label: string) => string;
  filters: string;
  clear: string;
  view: string;
  defaultView: string;
  myViews: string;
  sharedViews: string;
  builtInViews: string;
  modified: string;
  save: string;
  saveAs: string;
  saveAsTitle: string;
  name: string;
  cancel: string;
  share: string;
  shareTitle: string;
  shareWorkspace: string;
  shareWorkspaceHint: string;
  sharePeople: string;
  shareRoles: string;
  search: string;
  setDefault: string;
  unsetDefault: string;
  isDefault: string;
  deleteView: string;
  deleteConfirm: (name: string) => string;
  ownedBy: (owner: string) => string;
  viewActions: string;
}

export const DEFAULT_VIEW_LABELS: ViewLabels = {
  matchAll: 'all',
  matchAny: 'any',
  matchPrefix: 'Match',
  addCondition: 'Add condition',
  addGroup: 'Add group',
  removeCondition: 'Remove condition',
  removeGroup: 'Remove group',
  field: 'Field',
  operator: 'Operator',
  value: 'Value',
  yes: 'Yes',
  no: 'No',
  noConditions: 'No conditions yet.',
  op: {
    eq: 'is',
    ne: 'is not',
    gt: 'greater than / after',
    gte: 'at least / on or after',
    lt: 'less than / before',
    lte: 'at most / on or before',
    in: 'is any of',
    nin: 'is none of',
    contains: 'contains',
    startsWith: 'starts with',
    isEmpty: 'is empty',
    isNotEmpty: 'is not empty',
  },
  valueSource: 'Compare with',
  typedValue: 'A value',
  currentUser: { email: 'Current user’s email', id: 'Current user’s ID', groups: 'Current user’s groups' },
  columns: 'Columns',
  showColumn: (label) => `Show ${label}`,
  moveUp: (label) => `Move ${label} up`,
  moveDown: (label) => `Move ${label} down`,
  filters: 'Filters',
  clear: 'Clear',
  view: 'View',
  defaultView: 'Default view',
  myViews: 'My views',
  sharedViews: 'Shared with me',
  builtInViews: 'Built-in',
  modified: 'modified',
  save: 'Save',
  saveAs: 'Save as…',
  saveAsTitle: 'Save view',
  name: 'Name',
  cancel: 'Cancel',
  share: 'Share…',
  shareTitle: 'Share view',
  shareWorkspace: 'Everyone in the workspace',
  shareWorkspaceHint: 'Shared views are read-only to others. Only people who can open this table see it.',
  sharePeople: 'People',
  shareRoles: 'Roles',
  search: 'Search',
  setDefault: 'Open this table with this view',
  unsetDefault: 'Stop opening with this view',
  isDefault: 'opens first',
  deleteView: 'Delete view',
  deleteConfirm: (name) => `Delete the view “${name}”? Everyone it is shared with loses it.`,
  ownedBy: (owner) => `by ${owner}`,
  viewActions: 'View actions',
};
