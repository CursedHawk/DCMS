// The Dynamic Apps stress fixture's application: one change set (<= 200 operations) that uses
// every part of the configuration model, so one tenant exercises all of it under load.
//
//   field types   every one (text … json, media), with required/unique/readOnly/hidden/default/min/max
//   relationships manyToOne (restrict, setNull, cascade, required), oneToOne, manyToMany
//   public access read all / own / none, create-only, update/delete own, hidden fields
//   row rules     a permission table (company_access) reached directly and through 2 hops
//   flows         row.created/updated/deleted, relation.created, revision.published, flow.event,
//                 form.submitted, user.invited, user.activated, schedule, manual, flow.invoke,
//                 a self-retriggering flow that the cascade limit must stop

const opts = (...values) => values.map((value) => ({ value, label: value.replaceAll('_', ' ') }));
const f = (apiName, type = 'text', extra = {}) => ({ apiName, displayName: apiName.replaceAll('_', ' '), type, ...extra });

// Row access: a company is the signed-in user's when company_access holds their email for it.
const viaCompany = [{ path: ['company', 'access'], field: 'user_email', matches: 'user.email', read: true, update: true }];

export const MODEL = [
  // ---------------------------------------------------------------- choice sets
  { op: 'create', type: 'choiceSet', value: { apiName: 'industry', displayName: 'Industry',
    options: opts('software', 'manufacturing', 'retail', 'finance', 'health', 'energy', 'logistics', 'media') } },
  { op: 'create', type: 'choiceSet', value: { apiName: 'stage', displayName: 'Stage',
    options: opts('lead', 'qualified', 'proposal', 'negotiation', 'won', 'lost') } },
  { op: 'create', type: 'choiceSet', value: { apiName: 'priority', displayName: 'Priority', options: opts('low', 'normal', 'high', 'urgent') } },
  { op: 'create', type: 'choiceSet', value: { apiName: 'ticket_status', displayName: 'Ticket status', options: opts('open', 'pending', 'resolved', 'closed') } },
  { op: 'create', type: 'choiceSet', value: { apiName: 'tags', displayName: 'Tags', options: opts('vip', 'partner', 'churn_risk', 'new', 'upsell', 'reference') } },
  { op: 'create', type: 'choiceSet', value: { apiName: 'activity_kind', displayName: 'Activity kind', options: opts('call', 'email', 'meeting', 'note') } },

  // ---------------------------------------------------------------- tables
  { op: 'create', type: 'table', value: { apiName: 'companies', displayName: 'Company', pluralName: 'Companies', primaryFieldId: 'name',
    public: { read: 'none', rules: [{ path: ['access'], field: 'user_email', matches: 'user.email', read: true, update: true }] },
    fields: [
      f('name', 'text', { required: true, unique: true, searchable: true, sortable: true, maxLength: 200 }),
      f('industry', 'choice', { choiceSetId: 'industry', filterable: true }),
      f('tags', 'multiChoice', { choiceSetId: 'tags', filterable: true }),
      f('website', 'url'),
      f('employees', 'integer', { minimum: 0, sortable: true, filterable: true }),
      f('annual_revenue', 'decimal', { minimum: 0, sortable: true, filterable: true }),
      f('founded', 'date', { sortable: true }),
      f('active', 'boolean', { default: true, filterable: true }),
      f('notes', 'longText', { searchable: true }),
      f('metadata', 'json', { hiddenFromPublic: true }),
      f('logo', 'media'),
      f('total_won', 'decimal', { readOnly: true, sortable: true }),
      f('last_activity_at', 'dateTime', { readOnly: true, sortable: true }),
    ] } },
  { op: 'create', type: 'table', value: { apiName: 'company_access', displayName: 'Company access', primaryFieldId: 'user_email',
    fields: [f('user_email', 'email', { required: true, filterable: true }), f('level', 'text', { default: 'editor' })] } },
  { op: 'create', type: 'table', value: { apiName: 'company_profiles', displayName: 'Company profile', primaryFieldId: 'headline',
    fields: [f('headline', 'text', { required: true }), f('summary', 'longText'), f('linkedin', 'url'), f('hq_country', 'text', { filterable: true })] } },
  { op: 'create', type: 'table', value: { apiName: 'contacts', displayName: 'Contact', primaryFieldId: 'email',
    public: { read: 'none', rules: viaCompany },
    fields: [
      f('email', 'email', { required: true, unique: true, searchable: true, sortable: true }),
      f('first_name', 'text', { searchable: true }),
      f('last_name', 'text', { required: true, searchable: true, sortable: true }),
      f('phone', 'text'),
      f('job_title', 'text', { filterable: true }),
      f('birthday', 'date'),
      f('opted_in', 'boolean', { default: false, filterable: true }),
    ] } },
  { op: 'create', type: 'table', value: { apiName: 'deals', displayName: 'Deal', primaryFieldId: 'title',
    public: { read: 'none', rules: viaCompany },
    fields: [
      f('title', 'text', { required: true, unique: true, searchable: true, sortable: true }),
      f('amount', 'decimal', { minimum: 0, sortable: true, filterable: true }),
      f('stage', 'choice', { choiceSetId: 'stage', default: 'lead', filterable: true }),
      f('priority', 'choice', { choiceSetId: 'priority', default: 'normal', filterable: true }),
      f('close_date', 'date', { sortable: true, filterable: true }),
      f('probability', 'integer', { minimum: 0, maximum: 100 }),
      f('won_at', 'dateTime', { readOnly: true, sortable: true }),
      f('internal_margin', 'decimal', { hiddenFromPublic: true }),
    ] } },
  { op: 'create', type: 'table', value: { apiName: 'products', displayName: 'Product', primaryFieldId: 'sku',
    public: { read: 'all' },
    fields: [
      f('sku', 'text', { required: true, unique: true, sortable: true, searchable: true }),
      f('name', 'text', { required: true, searchable: true, sortable: true }),
      f('price', 'decimal', { minimum: 0, sortable: true, filterable: true }),
      f('category', 'text', { filterable: true }),
      f('active', 'boolean', { default: true, filterable: true }),
      f('cost', 'decimal', { hiddenFromPublic: true }),
    ] } },
  { op: 'create', type: 'table', value: { apiName: 'activities', displayName: 'Activity', pluralName: 'Activities', primaryFieldId: 'subject',
    public: { read: 'none', rules: [{ ...viaCompany[0], delete: true }] },
    fields: [
      f('subject', 'text', { required: true, searchable: true }),
      f('kind', 'choice', { choiceSetId: 'activity_kind', default: 'note', filterable: true }),
      f('due_at', 'dateTime', { sortable: true, filterable: true }),
      f('done', 'boolean', { default: false, filterable: true }),
      f('body', 'longText'),
    ] } },
  { op: 'create', type: 'table', value: { apiName: 'tickets', displayName: 'Ticket', primaryFieldId: 'subject',
    public: { read: 'own', create: true, updateOwn: true, deleteOwn: true },
    fields: [
      f('subject', 'text', { required: true, searchable: true }),
      f('description', 'longText'),
      f('priority', 'choice', { choiceSetId: 'priority', default: 'normal', filterable: true }),
      f('status', 'choice', { choiceSetId: 'ticket_status', default: 'open', filterable: true }),
      f('escalated', 'boolean', { readOnly: true }),
      f('reporter_email', 'email'),
      f('internal_notes', 'longText', { hiddenFromPublic: true }),
    ] } },
  { op: 'create', type: 'table', value: { apiName: 'leads', displayName: 'Lead', primaryFieldId: 'email',
    public: { create: true },
    fields: [f('email', 'email', { required: true }), f('name', 'text'), f('company_name', 'text'), f('message', 'longText', { maxLength: 4000 }), f('source', 'text', { default: 'site' })] } },
  { op: 'create', type: 'table', value: { apiName: 'audit_log', displayName: 'Audit entry', pluralName: 'Audit log', primaryFieldId: 'message',
    fields: [f('message', 'text', { required: true, searchable: true }), f('level', 'text', { default: 'info', filterable: true }), f('ref', 'text', { filterable: true })] } },
  { op: 'create', type: 'table', value: { apiName: 'loop_counter', displayName: 'Loop counter', primaryFieldId: 'name',
    fields: [f('name', 'text', { required: true }), f('bumps', 'integer', { default: 0 })] } },
  { op: 'create', type: 'table', value: { apiName: 'kpi', displayName: 'KPI sample', primaryFieldId: 'metric',
    fields: [f('metric', 'text', { required: true, filterable: true }), f('value', 'decimal'), f('at', 'dateTime', { sortable: true })] } },

  // ---------------------------------------------------------------- relationships
  { op: 'create', type: 'relationship', value: { apiName: 'company', sourceTableId: 'company_access', targetTableId: 'companies',
    inverseApiName: 'access', required: true, onDelete: 'cascade' } },
  { op: 'create', type: 'relationship', value: { apiName: 'company', kind: 'oneToOne', sourceTableId: 'company_profiles', targetTableId: 'companies',
    inverseApiName: 'profile', onDelete: 'cascade' } },
  { op: 'create', type: 'relationship', value: { apiName: 'company', sourceTableId: 'contacts', targetTableId: 'companies',
    inverseApiName: 'contacts', onDelete: 'setNull' } },
  { op: 'create', type: 'relationship', value: { apiName: 'company', sourceTableId: 'deals', targetTableId: 'companies',
    inverseApiName: 'deals', required: true, onDelete: 'restrict' } },
  { op: 'create', type: 'relationship', value: { apiName: 'primary_contact', sourceTableId: 'deals', targetTableId: 'contacts',
    inverseApiName: 'primary_deals', onDelete: 'setNull' } },
  { op: 'create', type: 'relationship', value: { apiName: 'products', kind: 'manyToMany', sourceTableId: 'deals', targetTableId: 'products',
    inverseApiName: 'deals' } },
  { op: 'create', type: 'relationship', value: { apiName: 'company', sourceTableId: 'activities', targetTableId: 'companies',
    inverseApiName: 'activities', required: true, onDelete: 'cascade' } },
  { op: 'create', type: 'relationship', value: { apiName: 'deal', sourceTableId: 'activities', targetTableId: 'deals',
    inverseApiName: 'activities', onDelete: 'cascade' } },
  { op: 'create', type: 'relationship', value: { apiName: 'company', sourceTableId: 'tickets', targetTableId: 'companies',
    inverseApiName: 'tickets', onDelete: 'setNull' } },

  // ---------------------------------------------------------------- views
  { op: 'create', type: 'view', value: { apiName: 'all', displayName: 'All companies', tableId: 'companies', isDefault: true,
    columns: ['name', 'industry', 'employees', 'total_won'], sort: [{ fieldId: 'name' }] } },
  { op: 'create', type: 'view', value: { apiName: 'pipeline', displayName: 'Pipeline', tableId: 'deals', isDefault: true,
    columns: ['title', 'company', 'stage', 'amount', 'close_date'], sort: [{ fieldId: 'amount', descending: true }] } },
  { op: 'create', type: 'view', value: { apiName: 'queue', displayName: 'Ticket queue', tableId: 'tickets', isDefault: true,
    columns: ['subject', 'priority', 'status', 'company'] } },

  // ---------------------------------------------------------------- flows
  { op: 'create', type: 'flow', value: { apiName: 'deal_won', displayName: 'Deal won',
    trigger: { event: 'row.updated', tableId: 'deals', changedFields: ['stage'] },
    condition: "row.stage == 'won' && previous.stage != 'won'",
    steps: [
      { id: 'company', action: 'records.lookup@1', input: { table: 'companies', id: '{{ row.company }}' } },
      { id: 'stamp', action: 'records.update@1', input: { table: 'deals', id: '{{ row.id }}', values: { won_at: '{{ now() }}' } } },
      { id: 'total', action: 'records.update@1', input: { table: 'companies', id: '{{ row.company }}',
        values: { total_won: '{{ coalesce(steps.company.total_won, 0) + coalesce(row.amount, 0) }}' } } },
      { id: 'note', action: 'records.create@1', input: { table: 'activities',
        values: { subject: 'Won {{ row.title }}', kind: 'note', company: '{{ row.company }}', deal: '{{ row.id }}' } } },
      { id: 'tell', action: 'dcms.notifications.raise@1', condition: 'row.amount >= 90000',
        input: { title: 'Big deal won', body: '{{ row.title }} for {{ steps.company.name }}', severity: 'info' } },
      { id: 'announce', action: 'event.publish@1', input: { name: 'deal_closed', payload: { deal: '{{ row.id }}', amount: '{{ row.amount }}' } } },
    ] } },
  { op: 'create', type: 'flow', value: { apiName: 'on_deal_closed', displayName: 'On deal closed', trigger: { event: 'flow.event.deal_closed' },
    steps: [{ id: 'log', action: 'records.create@1', input: { table: 'audit_log',
      values: { message: 'deal closed for {{ event.payload.amount }}', ref: '{{ event.payload.deal }}', level: 'info' } } }] } },
  { op: 'create', type: 'flow', value: { apiName: 'deal_deleted', displayName: 'Deal deleted', trigger: { event: 'row.deleted', tableId: 'deals' },
    steps: [{ id: 'log', action: 'records.create@1', input: { table: 'audit_log', values: { message: 'deal deleted', ref: '{{ row.id }}', level: 'warn' } } }] } },
  { op: 'create', type: 'flow', value: { apiName: 'ticket_triage', displayName: 'Ticket triage', trigger: { event: 'row.created', tableId: 'tickets' },
    condition: "row.priority == 'urgent' || row.priority == 'high'",
    steps: [
      { id: 'flag', action: 'records.update@1', input: { table: 'tickets', id: '{{ row.id }}', values: { escalated: true } } },
      { id: 'log', action: 'records.create@1', input: { table: 'audit_log', values: { message: 'escalated {{ row.subject }}', ref: '{{ row.id }}', level: 'warn' } } },
    ] } },
  { op: 'create', type: 'flow', value: { apiName: 'activity_touch', displayName: 'Activity touches company', trigger: { event: 'row.created', tableId: 'activities' },
    condition: "row.kind == 'meeting'",
    steps: [{ id: 'touch', action: 'records.update@1', input: { table: 'companies', id: '{{ row.company }}', values: { last_activity_at: '{{ now() }}' } } }] } },
  { op: 'create', type: 'flow', value: { apiName: 'lead_intake', displayName: 'Lead intake', trigger: { event: 'row.created', tableId: 'leads' },
    steps: [
      // A filter value may not be null (`isNull` exists for that), so only look when there is a name.
      { id: 'match', action: 'records.query@1', condition: 'row.company_name != null',
        input: { table: 'companies', filter: { field: 'name', op: 'eq', value: '{{ row.company_name }}' }, pageSize: 1 } },
      { id: 'log', action: 'records.create@1', input: { table: 'audit_log', values: { message: 'lead {{ row.email }} matched {{ coalesce(steps.match.total, 0) }}', ref: '{{ row.id }}' } } },
    ] } },
  { op: 'create', type: 'flow', value: { apiName: 'form_to_lead', displayName: 'Form to lead', trigger: { event: 'form.submitted' },
    condition: "event.payload.formName == 'lead'",
    steps: [{ id: 'lead', action: 'records.create@1', input: { table: 'leads',
      values: { email: '{{ event.payload.data.email }}', name: '{{ event.payload.data.name }}', message: '{{ event.payload.data.message }}', source: 'form' } } }] } },
  { op: 'create', type: 'flow', value: { apiName: 'user_invited', displayName: 'User invited', trigger: { event: 'user.invited' },
    steps: [{ id: 'log', action: 'records.create@1', input: { table: 'audit_log', values: { message: 'invited {{ event.payload.email }}', level: 'info' } } }] } },
  { op: 'create', type: 'flow', value: { apiName: 'user_activated', displayName: 'User activated', trigger: { event: 'user.activated' },
    steps: [{ id: 'log', action: 'records.create@1', input: { table: 'audit_log', values: { message: 'activated {{ event.payload.email }}', level: 'info' } } }] } },
  { op: 'create', type: 'flow', value: { apiName: 'product_linked', displayName: 'Product linked', trigger: { event: 'relation.created', relationshipId: 'deals.products' },
    steps: [{ id: 'log', action: 'records.create@1', input: { table: 'audit_log', values: { message: 'product linked', level: 'debug', ref: '{{ event.payload.sourceId }}' } } }] } },
  { op: 'create', type: 'flow', value: { apiName: 'on_publish', displayName: 'On publish', trigger: { event: 'revision.published' },
    steps: [{ id: 'log', action: 'records.create@1', input: { table: 'audit_log', values: { message: 'published', level: 'info' } } }] } },
  { op: 'create', type: 'flow', value: { apiName: 'kpi_tick', displayName: 'KPI tick', trigger: { event: 'schedule', everyMinutes: 5 },
    steps: [
      { id: 'won', action: 'records.query@1', input: { table: 'deals', filter: { field: 'stage', op: 'eq', value: 'won' }, pageSize: 1 } },
      { id: 'open', action: 'records.query@1', input: { table: 'tickets', filter: { field: 'status', op: 'eq', value: 'open' }, pageSize: 1 } },
      { id: 'save_won', action: 'records.create@1', input: { table: 'kpi', values: { metric: 'deals_won', value: '{{ steps.won.total }}', at: '{{ now() }}' } } },
      { id: 'save_open', action: 'records.create@1', input: { table: 'kpi', values: { metric: 'tickets_open', value: '{{ steps.open.total }}', at: '{{ now() }}' } } },
    ] } },
  { op: 'create', type: 'flow', value: { apiName: 'loop_bump', displayName: 'Loop bump (cascade limit)', trigger: { event: 'row.updated', tableId: 'loop_counter' },
    steps: [{ id: 'again', action: 'records.update@1', input: { table: 'loop_counter', id: '{{ row.id }}', values: { bumps: '{{ row.bumps + 1 }}' } } }] } },
  { op: 'create', type: 'flow', value: { apiName: 'recalc_company', displayName: 'Touch company', description: 'Run from the site by a role holding flow:recalc_company:run.',
    steps: [
      { id: 'company', action: 'records.lookup@1', input: { table: 'companies', id: '{{ input.company }}' } },
      { id: 'touch', action: 'records.update@1', input: { table: 'companies', id: '{{ input.company }}', values: { last_activity_at: '{{ now() }}' } } },
    ] } },
  { op: 'create', type: 'flow', value: { apiName: 'chain', displayName: 'Chain (flow.invoke)',
    steps: [{ id: 'go', action: 'flow.invoke@1', input: { flow: 'recalc_company', input: { company: '{{ input.company }}' } } }] } },
  { op: 'create', type: 'flow', value: { apiName: 'digest_email', displayName: 'Digest email',
    steps: [
      { id: 'count', action: 'records.query@1', input: { table: 'deals', filter: { field: 'stage', op: 'eq', value: 'won' }, pageSize: 1 } },
      { id: 'mail', action: 'dcms.email.send@1', input: { to: '{{ input.to }}', subject: 'DA stress digest', text: '{{ steps.count.total }} deals won' } },
    ] } },
];

/** A second change set, published later, so revisions, diff and rollback have something to compare. */
export const MODEL_V2 = [
  { op: 'create', type: 'field', target: 'deals', value: f('source', 'text', { filterable: true }) },
  { op: 'update', type: 'field', target: 'deals.probability', value: { description: 'Percent' } },
  { op: 'update', type: 'settings', value: { description: 'DA stress test CRM (v2)' } },
];
