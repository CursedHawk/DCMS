// The demo CRM's application model (Dynamic Apps), published in three revisions:
//
//   V1  the data model: 20 tables, every field type and option, every relationship kind and
//       delete behaviour (self-references, one-to-one, many-to-many), row rules through up to
//       three hops matching user.email and user.groups, views, a composite unique index.
//       Fields that automations own later are still writable here, so the import can carry
//       their history over from "the old CRM".
//   V2  the automations: ~30 flows on every trigger kind, every built-in action and the
//       User Authentication ones; the computed fields become read-only.
//   V3  a schema change over live data: a text field replaced by a choice (copyFrom), a field
//       deprecated, a field and a view added.
//
// Every table and field carries a description: they are the generated OpenAPI document.

const label = (apiName) => apiName.charAt(0).toUpperCase() + apiName.slice(1).replaceAll('_', ' ');
const f = (apiName, type, description, extra = {}) => ({ apiName, displayName: label(apiName), type, description, ...extra });
const opts = (...pairs) => pairs.map(([value, color, text]) => ({ value, label: text ?? label(value), ...(color ? { color } : {}) }));
const choiceSet = (apiName, displayName, options) => ({ op: 'create', type: 'choiceSet', value: { apiName, displayName, options } });
const table = (value) => ({ op: 'create', type: 'table', value });
const rel = (value) => ({ op: 'create', type: 'relationship', value: { onDelete: 'restrict', ...value } });
const view = (tableId, apiName, displayName, columns, sort = [], isDefault = false) =>
  ({ op: 'create', type: 'view', value: { apiName, displayName, tableId, columns, sort, isDefault } });
const by = (fieldId, descending = false) => ({ fieldId, descending });

const GREEN = '#16a34a', RED = '#dc2626', AMBER = '#d97706', BLUE = '#2563eb', GREY = '#6b7280', PURPLE = '#7c3aed', TEAL = '#0d9488';

// Row access. A sales rep reaches an account when the account team lists their email (read and
// update), and reads every account of a territory owned by one of their groups. Everything that
// belongs to an account inherits both through the account.
const teamOf = (path, more = {}) => ({ path: [...path, 'team'], field: 'user_email', matches: 'user.email', read: true, update: true, ...more });
const territoryOf = (path) => ({ path: [...path, 'territory'], field: 'group_id', matches: 'user.groups', read: true });
const viaAccount = (more = {}) => [teamOf(['account'], more), territoryOf(['account'])];

export const SETTINGS_V1 = 'Northwind Sales Cloud: the CRM of a B2B software and services company selling across Europe, the Americas and APAC. '
  + 'Accounts, contacts and leads; opportunities with line items, quotes, contracts and invoices; marketing campaigns; activities; '
  + 'customer support cases with a knowledge base; sales targets and KPI history.\n\n'
  + 'Site API access: products and knowledge articles are public. Everything that belongs to an account is visible to the members of its '
  + 'account team (read and update) and to the sales group that owns its territory (read). Customers signed in to the portal see and update '
  + 'the support cases they opened. Anyone may submit a lead (web-to-lead); a signed-in rep reaches the leads assigned to them.\n\n'
  + 'Money is a decimal in the record\'s currency; dates are ISO 8601 (yyyy-mm-dd), times are UTC.';

export const MODEL_V1 = [
  { op: 'update', type: 'settings', value: { description: SETTINGS_V1 } },

  // ---------------------------------------------------------------- choice sets
  choiceSet('account_type', 'Account type', opts(['prospect', BLUE], ['customer', GREEN], ['partner', PURPLE], ['reseller', TEAL], ['competitor', RED], ['former_customer', GREY, 'Former customer'])),
  choiceSet('industry', 'Industry', opts(['software', null], ['manufacturing', null], ['retail', null], ['financial_services', null, 'Financial services'], ['healthcare', null],
    ['energy', null], ['logistics', null], ['media', null], ['telecommunications', null], ['public_sector', null, 'Public sector'], ['education', null], ['hospitality', null])),
  choiceSet('segment', 'Segment', opts(['smb', null, 'SMB'], ['mid_market', null, 'Mid-market'], ['enterprise', null, 'Enterprise'], ['strategic', null, 'Strategic'])),
  choiceSet('account_tags', 'Account tags', opts(['vip', PURPLE, 'VIP'], ['reference', GREEN], ['churn_risk', RED, 'Churn risk'], ['expansion', BLUE], ['public_logo', TEAL, 'Public logo'], ['beta_program', AMBER, 'Beta program'])),
  choiceSet('region', 'Region', opts(['emea', null, 'EMEA'], ['americas', null, 'Americas'], ['apac', null, 'APAC'])),
  choiceSet('team_role', 'Account team role', opts(['owner', BLUE], ['account_executive', null, 'Account executive'], ['solutions_engineer', null, 'Solutions engineer'], ['customer_success', null, 'Customer success'], ['partner', PURPLE])),
  choiceSet('salutation', 'Salutation', opts(['mr', null, 'Mr.'], ['ms', null, 'Ms.'], ['mx', null, 'Mx.'], ['dr', null, 'Dr.'], ['prof', null, 'Prof.'])),
  choiceSet('department', 'Department', opts(['executive', null], ['it', null, 'IT'], ['finance', null], ['operations', null], ['sales', null], ['marketing', null], ['procurement', null], ['hr', null, 'HR'], ['engineering', null], ['legal', null])),
  choiceSet('channel', 'Contact channel', opts(['email', null], ['phone', null], ['sms', null, 'SMS'], ['linkedin', null, 'LinkedIn'], ['none', GREY, 'Do not contact'])),
  choiceSet('lead_source', 'Lead source', opts(['website', BLUE], ['form', BLUE, 'Web form'], ['referral', GREEN], ['partner', PURPLE], ['event', AMBER], ['webinar', AMBER], ['cold_call', GREY, 'Cold call'], ['advertising', TEAL], ['social', TEAL])),
  choiceSet('lead_status', 'Lead status', opts(['new', BLUE], ['contacted', null], ['nurturing', AMBER], ['qualified', GREEN], ['disqualified', GREY], ['converted', PURPLE])),
  choiceSet('rating', 'Rating', opts(['hot', RED], ['warm', AMBER], ['cold', BLUE])),
  choiceSet('campaign_type', 'Campaign type', opts(['email', null], ['webinar', null], ['trade_show', null, 'Trade show'], ['paid_ads', null, 'Paid ads'], ['content', null], ['partner', null], ['direct_mail', null, 'Direct mail'])),
  choiceSet('campaign_status', 'Campaign status', opts(['planned', GREY], ['active', GREEN], ['completed', BLUE], ['cancelled', RED])),
  choiceSet('stage', 'Opportunity stage', opts(['prospecting', GREY], ['qualification', BLUE], ['needs_analysis', BLUE, 'Needs analysis'], ['proposal', AMBER], ['negotiation', AMBER],
    ['closed_won', GREEN, 'Closed won'], ['closed_lost', RED, 'Closed lost'])),
  choiceSet('forecast', 'Forecast category', opts(['pipeline', GREY], ['best_case', BLUE, 'Best case'], ['commit', GREEN], ['closed', GREEN], ['omitted', RED])),
  choiceSet('opportunity_type', 'Opportunity type', opts(['new_business', null, 'New business'], ['upsell', null], ['cross_sell', null, 'Cross-sell'], ['renewal', null])),
  choiceSet('loss_reason', 'Loss reason', opts(['price', null], ['competitor', null], ['no_budget', null, 'No budget'], ['no_decision', null, 'No decision'], ['timing', null], ['product_fit', null, 'Product fit'])),
  choiceSet('currency', 'Currency', opts(['EUR', null], ['USD', null], ['GBP', null], ['CZK', null], ['CHF', null], ['SEK', null], ['PLN', null], ['JPY', null], ['AUD', null])),
  choiceSet('product_family', 'Product family', opts(['subscription', BLUE], ['license', PURPLE], ['hardware', GREY], ['professional_services', TEAL, 'Professional services'], ['support', GREEN], ['training', AMBER])),
  choiceSet('unit', 'Unit', opts(['user_month', null, 'User / month'], ['user_year', null, 'User / year'], ['license', null], ['device', null], ['day', null], ['hour', null], ['package', null])),
  choiceSet('threat', 'Threat level', opts(['low', GREEN], ['medium', AMBER], ['high', RED])),
  choiceSet('quote_status', 'Quote status', opts(['draft', GREY], ['sent', BLUE], ['accepted', GREEN], ['rejected', RED], ['expired', GREY])),
  choiceSet('contract_status', 'Contract status', opts(['draft', GREY], ['active', GREEN], ['expired', AMBER], ['terminated', RED])),
  choiceSet('billing_frequency', 'Billing frequency', opts(['monthly', null], ['quarterly', null], ['annually', null], ['one_time', null, 'One-time'])),
  choiceSet('invoice_status', 'Invoice status', opts(['draft', GREY], ['issued', BLUE], ['paid', GREEN], ['overdue', RED], ['void', GREY])),
  choiceSet('payment_method', 'Payment method', opts(['bank_transfer', null, 'Bank transfer'], ['card', null], ['direct_debit', null, 'Direct debit'], ['paypal', null, 'PayPal'])),
  choiceSet('activity_type', 'Activity type', opts(['call', BLUE], ['email', TEAL], ['meeting', PURPLE], ['task', AMBER], ['demo', GREEN], ['note', GREY])),
  choiceSet('activity_status', 'Activity status', opts(['planned', BLUE], ['completed', GREEN], ['cancelled', GREY])),
  choiceSet('activity_outcome', 'Activity outcome', opts(['positive', GREEN], ['neutral', GREY], ['negative', RED], ['no_answer', AMBER, 'No answer'], ['follow_up', BLUE, 'Follow-up needed'])),
  choiceSet('priority', 'Priority', opts(['low', GREY], ['normal', BLUE], ['high', AMBER], ['urgent', RED])),
  choiceSet('case_status', 'Case status', opts(['new', BLUE], ['open', AMBER], ['pending', PURPLE, 'Waiting on customer'], ['on_hold', GREY, 'On hold'], ['resolved', GREEN], ['closed', GREY])),
  choiceSet('case_channel', 'Case channel', opts(['email', null], ['phone', null], ['portal', null], ['chat', null], ['form', null, 'Web form'])),
  choiceSet('case_category', 'Case category', opts(['question', null], ['bug', null], ['outage', null], ['billing', null], ['feature_request', null, 'Feature request'], ['access', null, 'Access & login'])),
  choiceSet('article_status', 'Article status', opts(['draft', GREY], ['published', GREEN], ['archived', GREY])),
  choiceSet('article_category', 'Article category', opts(['getting_started', null, 'Getting started'], ['how_to', null, 'How-to'], ['troubleshooting', null], ['billing', null], ['security', null], ['release_notes', null, 'Release notes'])),
  choiceSet('log_level', 'Log level', opts(['debug', GREY], ['info', BLUE], ['warning', AMBER], ['error', RED])),

  // ---------------------------------------------------------------- tables
  table({ apiName: 'territories', displayName: 'Territory', pluralName: 'Territories', primaryFieldId: 'name',
    description: 'A sales territory: a slice of the world owned by one regional sales group. Accounts belong to a territory, and every member of the owning group may read its accounts.',
    fields: [
      f('name', 'text', 'Territory name, e.g. "DACH" or "NA West".', { required: true, unique: true, maxLength: 80 }),
      f('region', 'choice', 'The region the territory rolls up into for reporting.', { choiceSetId: 'region', required: true }),
      f('countries', 'text', 'ISO 3166-1 alpha-2 codes of the countries it covers, comma separated.'),
      f('group_id', 'text', 'Id of the site user group (User Authentication) that owns the territory. Its members read every account in it.', { hiddenFromPublic: true }),
      f('manager_email', 'email', 'The regional sales manager.'),
      f('description', 'longText', 'Notes on the territory: focus industries, partners, coverage model.'),
    ] }),

  table({ apiName: 'accounts', displayName: 'Account', pluralName: 'Accounts', primaryFieldId: 'name',
    description: 'A company the business deals with: prospect, customer, partner, reseller or competitor. The hub of the CRM: contacts, opportunities, contracts, invoices, activities and cases all hang off an account.',
    public: { read: 'none', rules: [teamOf([]), territoryOf([])] },
    fields: [
      f('name', 'text', 'Legal or trading name. Unique across the CRM.', { required: true, unique: true, searchable: true, sortable: true, maxLength: 200 }),
      f('account_number', 'text', 'Internal account number, ACC-nnnnnn. Unique; used on quotes and invoices.', { unique: true, maxLength: 20 }),
      f('type', 'choice', 'Relationship with the company. Becomes "customer" automatically when its first opportunity is won.', { choiceSetId: 'account_type', default: 'prospect', filterable: true }),
      f('industry', 'choice', 'Primary industry.', { choiceSetId: 'industry', filterable: true }),
      f('segment', 'choice', 'Size segment, driving coverage model and pricing.', { choiceSetId: 'segment' }),
      f('rating', 'text', 'Legacy rating carried over from the previous CRM (free text). Replaced by account_rating in revision 3.'),
      f('tags', 'multiChoice', 'Labels for targeting and reporting.', { choiceSetId: 'account_tags' }),
      f('website', 'url', 'Company website.'),
      f('phone', 'text', 'Main switchboard number in E.164 format.', { maxLength: 40 }),
      f('email', 'email', 'General contact address, e.g. info@….'),
      f('employees', 'integer', 'Number of employees, company-wide.', { minimum: 0 }),
      f('annual_revenue', 'decimal', 'Annual revenue in EUR, last reported.', { minimum: 0 }),
      f('billing_street', 'text', 'Billing address: street and number.'),
      f('billing_city', 'text', 'Billing address: city.'),
      f('billing_postal_code', 'text', 'Billing address: postal code.', { maxLength: 20 }),
      f('billing_country', 'text', 'Billing address: ISO 3166-1 alpha-2 country code.', { maxLength: 2 }),
      f('vat_id', 'text', 'VAT / tax registration number.', { maxLength: 30 }),
      f('description', 'longText', 'What the company does and why it matters to us.'),
      f('logo', 'media', 'Company logo from the media library (an asset id).'),
      f('owner_email', 'email', 'The account owner: the sales rep accountable for the relationship.'),
      f('health_score', 'integer', 'Customer health from 0 (about to churn) to 100 (thriving), maintained by customer success.', { minimum: 0, maximum: 100 }),
      f('customer_since', 'date', 'Date of the first won opportunity.'),
      f('is_active', 'boolean', 'False for companies that ceased trading or merged; they are kept for history.', { default: true }),
      f('credit_limit', 'decimal', 'Credit limit in EUR approved by finance. Internal: never served on the site.', { minimum: 0, hiddenFromPublic: true }),
      f('lifetime_value', 'decimal', 'Sum of won opportunity amounts in EUR. Maintained by the "Opportunity won" automation.', { minimum: 0, filterable: true }),
      f('last_activity_at', 'dateTime', 'When the last call, meeting or demo with the account was completed. Maintained by automation.'),
      f('erp_data', 'json', 'Raw record synchronised from the ERP (payment terms, dunning level…). Internal: never served on the site.', { hiddenFromPublic: true }),
    ] }),

  table({ apiName: 'account_team', displayName: 'Account team member', pluralName: 'Account team members', primaryFieldId: 'user_email',
    description: 'Who works on an account. A site user listed here reads and updates the account and everything that belongs to it (contacts, opportunities, quotes, activities, cases): access is data, managed per account.',
    fields: [
      f('user_email', 'email', 'Email of the team member\'s site account.', { required: true, filterable: true }),
      f('role', 'choice', 'What they do on the account.', { choiceSetId: 'team_role', default: 'account_executive' }),
      f('since', 'date', 'When they joined the account team.'),
    ] }),

  table({ apiName: 'contacts', displayName: 'Contact', pluralName: 'Contacts', primaryFieldId: 'email',
    description: 'A person at an account: decision makers, champions, users and billing contacts.',
    public: { read: 'none', rules: viaAccount() },
    fields: [
      f('email', 'email', 'Work email. Unique: one contact per address.', { required: true, unique: true, searchable: true }),
      f('salutation', 'choice', 'How to address them.', { choiceSetId: 'salutation' }),
      f('first_name', 'text', 'Given name.', { searchable: true, maxLength: 80 }),
      f('last_name', 'text', 'Family name.', { required: true, searchable: true, sortable: true, maxLength: 80 }),
      f('job_title', 'text', 'Job title as written on their signature.'),
      f('department', 'choice', 'Department they work in.', { choiceSetId: 'department' }),
      f('phone', 'text', 'Direct line, E.164.', { maxLength: 40 }),
      f('mobile', 'text', 'Mobile number, E.164.', { maxLength: 40 }),
      f('fax', 'text', 'Fax number. Deprecated in revision 3; kept for existing records.', { maxLength: 40 }),
      f('linkedin', 'url', 'LinkedIn profile URL.'),
      f('birthday', 'date', 'Date of birth, for greetings. Optional.'),
      f('preferred_channel', 'choice', 'How they prefer to be contacted.', { choiceSetId: 'channel', default: 'email' }),
      f('email_opt_in', 'boolean', 'Consented to marketing email (GDPR). Required before adding them to email campaigns.', { default: false }),
      f('do_not_call', 'boolean', 'Asked not to be called.', { default: false }),
      f('is_primary', 'boolean', 'The main contact of their account.', { default: false }),
      f('lead_score', 'integer', 'Engagement score from 0 to 100 computed from campaign responses and activities.', { minimum: 0, maximum: 100 }),
      f('photo', 'media', 'Portrait from the media library (an asset id).'),
      f('notes', 'longText', 'Free-form notes.'),
      f('mailing_address', 'json', 'Postal address when it differs from the account\'s: { street, city, postalCode, country }.'),
    ] }),

  table({ apiName: 'leads', displayName: 'Lead', pluralName: 'Leads', primaryFieldId: 'name',
    description: 'An unqualified prospect, typically from the website, a form, an event or a campaign. A qualified lead is converted into an account, a contact and an opportunity by the "Convert lead" automation.\n\nAnyone may create one through the site (web-to-lead); the rep named in owner_email reads and updates it.',
    public: { create: true, rules: [{ path: [], field: 'owner_email', matches: 'user.email', read: true, update: true }] },
    fields: [
      f('name', 'text', 'Full name of the person.', { required: true, searchable: true, maxLength: 160 }),
      f('email', 'email', 'Their email address.', { required: true, searchable: true }),
      f('company_name', 'text', 'The company they work for, as they wrote it.', { searchable: true, maxLength: 200 }),
      f('job_title', 'text', 'Their job title.'),
      f('phone', 'text', 'Phone number.', { maxLength: 40 }),
      f('country', 'text', 'ISO 3166-1 alpha-2 country code.', { maxLength: 2 }),
      f('source', 'choice', 'Where the lead came from.', { choiceSetId: 'lead_source', default: 'website' }),
      f('status', 'choice', 'Qualification status. "converted" once the Convert lead automation turned it into an account, contact and opportunity.', { choiceSetId: 'lead_status', default: 'new', filterable: true }),
      f('rating', 'choice', 'Sales-assessed temperature. Leads with an estimated value from 50,000 are rated hot automatically.', { choiceSetId: 'rating' }),
      f('estimated_value', 'decimal', 'Rough deal size in EUR.', { minimum: 0 }),
      f('owner_email', 'email', 'The sales rep working the lead.', { filterable: true }),
      f('message', 'longText', 'What they asked for.', { maxLength: 5000 }),
      f('utm', 'json', 'Marketing attribution: { source, medium, campaign, term, content }. Internal.', { hiddenFromPublic: true }),
      f('consent', 'boolean', 'Agreed to be contacted about the request.', { default: true }),
      f('converted_at', 'dateTime', 'When the lead was converted. Set by the Convert lead automation.'),
    ] }),

  table({ apiName: 'campaigns', displayName: 'Campaign', pluralName: 'Campaigns', primaryFieldId: 'name',
    description: 'A marketing campaign: webinars, trade shows, email programmes and paid advertising. Leads and opportunities record the campaign that sourced them; contacts are campaign members.',
    fields: [
      f('name', 'text', 'Campaign name. Unique.', { required: true, unique: true, searchable: true, maxLength: 160 }),
      f('type', 'choice', 'Kind of campaign.', { choiceSetId: 'campaign_type' }),
      f('status', 'choice', 'Lifecycle status.', { choiceSetId: 'campaign_status', default: 'planned', filterable: true }),
      f('start_date', 'date', 'First day of the campaign.'),
      f('end_date', 'date', 'Last day of the campaign.'),
      f('budget', 'decimal', 'Approved budget in EUR.', { minimum: 0 }),
      f('actual_cost', 'decimal', 'Spend so far in EUR.', { minimum: 0 }),
      f('expected_revenue', 'decimal', 'Pipeline the campaign is expected to generate, in EUR.', { minimum: 0 }),
      f('utm_campaign', 'text', 'The utm_campaign value its links carry. Unique.', { unique: true, maxLength: 80 }),
      f('landing_page', 'url', 'Where the campaign sends people.'),
      f('description', 'longText', 'Audience, message and goals.'),
      f('leads_generated', 'integer', 'Leads attributed to the campaign. Incremented by automation.', { minimum: 0, default: 0 }),
    ] }),

  table({ apiName: 'products', displayName: 'Product', pluralName: 'Products', primaryFieldId: 'sku',
    description: 'The price list: subscriptions, licences, hardware, services, support plans and training. Public: the site may list the catalogue (costs stay internal).',
    public: { read: 'all' },
    fields: [
      f('sku', 'text', 'Stock keeping unit, e.g. NW-ANL-PRO-Y. Unique.', { required: true, unique: true, searchable: true, maxLength: 40 }),
      f('name', 'text', 'Product name as shown on quotes.', { required: true, searchable: true, sortable: true, maxLength: 160 }),
      f('family', 'choice', 'Product family.', { choiceSetId: 'product_family', filterable: true }),
      f('description', 'longText', 'What the customer gets.'),
      f('unit', 'choice', 'What one unit of quantity is.', { choiceSetId: 'unit' }),
      f('list_price', 'decimal', 'List price per unit in EUR, before discounts and tax.', { required: true, minimum: 0 }),
      f('cost', 'decimal', 'Our cost per unit in EUR. Internal: never served on the site.', { minimum: 0, hiddenFromPublic: true }),
      f('tax_rate', 'decimal', 'Default VAT rate in percent.', { minimum: 0, maximum: 100, default: 21 }),
      f('is_active', 'boolean', 'Can be sold. Inactive products stay on existing quotes and contracts.', { default: true }),
      f('launched_on', 'date', 'General availability date.'),
      f('image', 'media', 'Product image from the media library (an asset id).'),
      f('specs', 'json', 'Technical specification as structured data, e.g. { "storageGb": 500, "sla": "99.9%" }.'),
    ] }),

  table({ apiName: 'competitors', displayName: 'Competitor', pluralName: 'Competitors', primaryFieldId: 'name',
    description: 'A company we compete with. Opportunities list the competitors in the deal, so win rates can be reported per competitor.',
    fields: [
      f('name', 'text', 'Competitor name. Unique.', { required: true, unique: true, maxLength: 120 }),
      f('website', 'url', 'Their website.'),
      f('threat_level', 'choice', 'How often and how hard they beat us.', { choiceSetId: 'threat', default: 'medium' }),
      f('strengths', 'longText', 'Where they are strong.'),
      f('weaknesses', 'longText', 'Where we win against them: the battle card.'),
      f('pricing_notes', 'longText', 'What we know about their pricing.'),
    ] }),

  table({ apiName: 'opportunities', displayName: 'Opportunity', pluralName: 'Opportunities', primaryFieldId: 'name',
    description: 'A potential sale to an account, moving through the pipeline stages to closed won or closed lost. Its amount is the sum of its line items; quotes are issued from it, and a won opportunity becomes a contract.',
    public: { read: 'none', rules: viaAccount() },
    fields: [
      f('name', 'text', 'Opportunity name, "<account> – <what> (OPP-nnnnnn)". Unique.', { required: true, unique: true, searchable: true, sortable: true, maxLength: 240 }),
      f('stage', 'choice', 'Pipeline stage. Moving it to closed won or closed lost runs the matching automation.', { choiceSetId: 'stage', default: 'prospecting', filterable: true }),
      f('amount', 'decimal', 'Total value in the opportunity currency: the sum of its line items, kept up to date by automation.', { minimum: 0, sortable: true }),
      f('currency', 'choice', 'Currency of all amounts on the opportunity and its quotes.', { choiceSetId: 'currency', default: 'EUR' }),
      f('probability', 'integer', 'Likelihood of winning, in percent.', { minimum: 0, maximum: 100, default: 10 }),
      f('weighted_amount', 'decimal', 'amount × probability / 100, for the weighted forecast. Computed by automation.', { minimum: 0 }),
      f('close_date', 'date', 'Expected (or actual) close date.', { required: true, sortable: true }),
      f('forecast_category', 'choice', 'Forecast roll-up category.', { choiceSetId: 'forecast', default: 'pipeline' }),
      f('type', 'choice', 'New business, upsell, cross-sell or renewal.', { choiceSetId: 'opportunity_type', default: 'new_business' }),
      f('lead_source', 'choice', 'Where the opportunity originated.', { choiceSetId: 'lead_source' }),
      f('next_step', 'text', 'The agreed next step, e.g. "Security review with IT on Tuesday".', { maxLength: 400 }),
      f('loss_reason', 'choice', 'Why it was lost. Expected when the stage is closed lost.', { choiceSetId: 'loss_reason' }),
      f('description', 'longText', 'Customer need, decision process and timeline.'),
      f('discount_percent', 'decimal', 'Overall discount granted, in percent. Internal: never served on the site.', { minimum: 0, maximum: 100, hiddenFromPublic: true }),
      f('owner_email', 'email', 'The rep who owns the opportunity.', { filterable: true }),
      f('won_at', 'dateTime', 'When it was won. Set by the Opportunity won automation.'),
      f('closed_at', 'dateTime', 'When it was closed, won or lost. Set by automation.'),
    ] }),

  table({ apiName: 'opportunity_line_items', displayName: 'Line item', pluralName: 'Line items', primaryFieldId: 'description',
    description: 'One product on an opportunity: quantity, price and discount. line_total is computed by automation, which also keeps the opportunity amount equal to the sum of its lines.',
    public: { read: 'none', rules: [{ path: ['opportunity', 'account', 'team'], field: 'user_email', matches: 'user.email', read: true, update: true, delete: true }] },
    fields: [
      f('description', 'text', 'Line description; defaults to the product name.', { required: true, maxLength: 240 }),
      f('quantity', 'integer', 'Number of units.', { required: true, minimum: 1, default: 1 }),
      f('unit_price', 'decimal', 'Price per unit in the opportunity currency.', { required: true, minimum: 0 }),
      f('discount_percent', 'decimal', 'Line discount in percent.', { minimum: 0, maximum: 100, default: 0 }),
      f('line_total', 'decimal', 'quantity × unit_price × (1 − discount_percent / 100), rounded to cents. Computed by automation.', { minimum: 0 }),
      f('service_start', 'date', 'For subscriptions and services: when delivery starts.'),
    ] }),

  table({ apiName: 'quotes', displayName: 'Quote', pluralName: 'Quotes', primaryFieldId: 'quote_number',
    description: 'A priced offer sent to the customer for an opportunity. Marking a quote sent logs an email activity; marking it accepted wins the opportunity and drafts the contract.',
    public: { read: 'none', rules: [{ path: ['opportunity', 'account', 'team'], field: 'user_email', matches: 'user.email', read: true, update: true }] },
    fields: [
      f('quote_number', 'text', 'Quote number, Q-yyyy-nnnnnn. Unique.', { required: true, unique: true, searchable: true, maxLength: 30 }),
      f('status', 'choice', 'Where the quote stands with the customer.', { choiceSetId: 'quote_status', default: 'draft', filterable: true }),
      f('valid_until', 'date', 'The offer expires after this date.'),
      f('subtotal', 'decimal', 'Sum of the lines before tax, in the opportunity currency.', { minimum: 0 }),
      f('tax_amount', 'decimal', 'Tax on the subtotal.', { minimum: 0 }),
      f('total', 'decimal', 'subtotal + tax_amount.', { minimum: 0 }),
      f('payment_terms_days', 'integer', 'Days the customer has to pay each invoice.', { minimum: 0, maximum: 180, default: 30 }),
      f('terms', 'longText', 'Commercial terms printed on the quote.'),
      f('sent_at', 'dateTime', 'When it was sent to the customer. Set by automation.'),
      f('document', 'media', 'The signed PDF from the media library (an asset id).'),
    ] }),

  table({ apiName: 'contracts', displayName: 'Contract', pluralName: 'Contracts', primaryFieldId: 'contract_number',
    description: 'A signed agreement with an account, usually created from an accepted quote. Activating a contract issues its first invoice; contracts nearing their end are reported for renewal.',
    public: { read: 'none', rules: [teamOf(['account'], { update: false })] },
    fields: [
      f('contract_number', 'text', 'Contract number, CT-…. Unique.', { required: true, unique: true, searchable: true, maxLength: 40 }),
      f('status', 'choice', 'Lifecycle status. Setting it to active issues the first invoice.', { choiceSetId: 'contract_status', default: 'draft', filterable: true }),
      f('start_date', 'date', 'First day of service.'),
      f('end_date', 'date', 'Last day of service.'),
      f('term_months', 'integer', 'Length of the term in months.', { minimum: 1, maximum: 120, default: 12 }),
      f('value', 'decimal', 'Total contract value in EUR.', { minimum: 0 }),
      f('billing_frequency', 'choice', 'How often it is invoiced.', { choiceSetId: 'billing_frequency', default: 'annually' }),
      f('auto_renew', 'boolean', 'Renews for another term unless cancelled.', { default: true }),
      f('renewal_notice_days', 'integer', 'Days before the end date by which a cancellation must arrive.', { minimum: 0, default: 60 }),
      f('signed_by', 'text', 'Name of the customer\'s signatory.'),
      f('signed_document', 'media', 'The countersigned PDF from the media library (an asset id).'),
    ] }),

  table({ apiName: 'invoices', displayName: 'Invoice', pluralName: 'Invoices', primaryFieldId: 'invoice_number',
    description: 'An invoice issued to an account, usually under a contract. Marking it paid stamps the payment time.',
    public: { read: 'none', rules: [teamOf(['account'], { update: false })] },
    fields: [
      f('invoice_number', 'text', 'Invoice number, INV-…. Unique.', { required: true, unique: true, searchable: true, maxLength: 40 }),
      f('status', 'choice', 'Payment status.', { choiceSetId: 'invoice_status', default: 'issued', filterable: true }),
      f('issue_date', 'date', 'Date of issue.'),
      f('due_date', 'date', 'Payment due date.', { filterable: true }),
      f('amount', 'decimal', 'Amount before tax, in EUR.', { required: true, minimum: 0 }),
      f('tax_amount', 'decimal', 'Tax, in EUR.', { minimum: 0 }),
      f('payment_method', 'choice', 'How it was or will be paid.', { choiceSetId: 'payment_method' }),
      f('paid_at', 'dateTime', 'When payment arrived. Set by automation when the status becomes paid.'),
    ] }),

  table({ apiName: 'activities', displayName: 'Activity', pluralName: 'Activities', primaryFieldId: 'subject',
    description: 'A call, email, meeting, demo, task or note, linked to the account and optionally to a contact, opportunity or lead. Completing a call, meeting or demo updates the account\'s last activity time.',
    public: { read: 'none', rules: [
      teamOf(['account'], { delete: true }), territoryOf(['account']),
      { path: [], field: 'assignee_email', matches: 'user.email', read: true, update: true, delete: true }] },
    fields: [
      f('subject', 'text', 'What it is about.', { required: true, searchable: true, maxLength: 240 }),
      f('type', 'choice', 'Kind of activity.', { choiceSetId: 'activity_type', default: 'task', filterable: true }),
      f('status', 'choice', 'Planned, completed or cancelled. Completing it stamps completed_at.', { choiceSetId: 'activity_status', default: 'planned' }),
      f('priority', 'choice', 'How urgent it is.', { choiceSetId: 'priority', default: 'normal' }),
      f('due_at', 'dateTime', 'When it is scheduled or due.', { filterable: true }),
      f('duration_minutes', 'integer', 'Planned or actual length, in minutes.', { minimum: 0, maximum: 1440 }),
      f('outcome', 'choice', 'How it went.', { choiceSetId: 'activity_outcome' }),
      f('description', 'longText', 'Notes and minutes.'),
      f('assignee_email', 'email', 'Who has to do it. They read and update it on the site.'),
      f('completed_at', 'dateTime', 'When it was completed. Set by automation.'),
    ] }),

  table({ apiName: 'cases', displayName: 'Case', pluralName: 'Cases', primaryFieldId: 'case_number',
    description: 'A customer support case. Its SLA deadline follows from its priority, set by automation when it is opened.\n\nCustomers signed in to the portal open cases and see and update their own; the account team reads and updates every case of the account.',
    public: { read: 'own', create: true, updateOwn: true, rules: [teamOf(['account'])] },
    fields: [
      f('case_number', 'text', 'Case number: CS-nnnnnn, or WEB-<id> for cases opened on the site. Unique; set by automation when left out.', { unique: true, searchable: true, maxLength: 48 }),
      f('subject', 'text', 'Short summary of the problem.', { required: true, searchable: true, maxLength: 240 }),
      f('description', 'longText', 'What happened, steps to reproduce, impact.', { maxLength: 20000 }),
      f('status', 'choice', 'Where the case stands.', { choiceSetId: 'case_status', default: 'new', filterable: true }),
      f('priority', 'choice', 'Business impact. Sets the SLA: urgent 4 h, high 8 h, normal 24 h, low 72 h.', { choiceSetId: 'priority', default: 'normal', filterable: true }),
      f('channel', 'choice', 'How the case came in.', { choiceSetId: 'case_channel', default: 'portal' }),
      f('category', 'choice', 'What kind of problem it is.', { choiceSetId: 'case_category' }),
      f('reporter_email', 'email', 'Who reported it.'),
      f('sla_due_at', 'dateTime', 'Resolution deadline under the SLA. Set by automation.', { sortable: true }),
      f('first_response_at', 'dateTime', 'When support first responded. Set by automation.'),
      f('resolved_at', 'dateTime', 'When it was resolved. Set by automation.'),
      f('escalated', 'boolean', 'Escalated to the second line. Set by automation.', { default: false }),
      f('satisfaction', 'integer', 'Customer satisfaction rating from 1 to 5, after resolution.', { minimum: 1, maximum: 5 }),
      f('internal_notes', 'longText', 'Support-only notes. Internal: never served on the site.', { hiddenFromPublic: true }),
    ] }),

  table({ apiName: 'case_comments', displayName: 'Case comment', pluralName: 'Case comments', primaryFieldId: 'author_email',
    description: 'A message on a case, from the customer or from support. A customer reply to a case waiting on them reopens it.',
    public: { read: 'own', create: true, rules: [teamOf(['case', 'account'], { update: false })] },
    fields: [
      f('author_email', 'email', 'Who wrote it.', { required: true }),
      f('body', 'longText', 'The message.', { required: true, maxLength: 20000 }),
      f('is_public', 'boolean', 'Visible to the customer. Internal notes are false.', { default: true }),
      f('attachment', 'media', 'A screenshot or log file from the media library (an asset id).'),
    ] }),

  table({ apiName: 'knowledge_articles', displayName: 'Knowledge article', pluralName: 'Knowledge base', primaryFieldId: 'title',
    description: 'A help-centre article. Public: the site may read the knowledge base.',
    public: { read: 'all' },
    fields: [
      f('title', 'text', 'Article title. Unique.', { required: true, unique: true, searchable: true, maxLength: 200 }),
      f('slug', 'text', 'URL slug. Unique.', { required: true, unique: true, maxLength: 120 }),
      f('summary', 'text', 'One-sentence summary for search results.', { maxLength: 400 }),
      f('body', 'longText', 'The article, in Markdown.', { searchable: true }),
      f('category', 'choice', 'Help-centre section.', { choiceSetId: 'article_category' }),
      f('status', 'choice', 'Only published articles should be shown.', { choiceSetId: 'article_status', default: 'draft', filterable: true }),
      f('helpful_votes', 'integer', 'Readers who found it helpful.', { minimum: 0, default: 0 }),
      f('published_on', 'date', 'Publication date.'),
    ] }),

  table({ apiName: 'sales_targets', displayName: 'Sales target', pluralName: 'Sales targets', primaryFieldId: 'name',
    description: 'A rep\'s quota for one period. One target per rep and period start (a unique index). achieved is the sum of their won opportunities in the period.',
    fields: [
      f('name', 'text', 'e.g. "2026 Q3 – Eva Nováková".', { required: true, maxLength: 160 }),
      f('owner_email', 'email', 'The rep.', { required: true, filterable: true }),
      f('period_start', 'date', 'First day of the period.', { required: true }),
      f('period_end', 'date', 'Last day of the period.', { required: true }),
      f('quota', 'decimal', 'Bookings target in EUR.', { required: true, minimum: 0 }),
      f('achieved', 'decimal', 'Won so far in the period, in EUR. Maintained by automation.', { minimum: 0, default: 0 }),
    ],
    indexes: [{ apiName: 'one_per_rep_and_period', fieldIds: ['owner_email', 'period_start'], unique: true }] }),

  table({ apiName: 'audit_log', displayName: 'Audit entry', pluralName: 'Audit log', primaryFieldId: 'message',
    description: 'What the automations did and noticed, newest first. Written by flows only; admin-only.',
    fields: [
      f('message', 'text', 'What happened.', { required: true, searchable: true, maxLength: 500 }),
      f('level', 'choice', 'Severity.', { choiceSetId: 'log_level', default: 'info', filterable: true }),
      f('category', 'text', 'Area: sales, support, billing, marketing, access, model.', { maxLength: 40 }),
      f('ref', 'text', 'Id of the record it is about.', { maxLength: 80 }),
      f('details', 'json', 'Structured detail.'),
    ] }),

  table({ apiName: 'kpi_snapshots', displayName: 'KPI snapshot', pluralName: 'KPI snapshots', primaryFieldId: 'metric',
    description: 'A point-in-time business metric, written hourly by the KPI automation (and back-filled with 90 days of history), for dashboards.',
    fields: [
      f('metric', 'text', 'Metric key: open_pipeline_count, won_count, open_cases, urgent_cases, new_leads.', { required: true, filterable: true, maxLength: 60 }),
      f('value', 'decimal', 'Its value at captured_at.', { required: true }),
      f('captured_at', 'dateTime', 'When it was measured.', { required: true }),
    ] }),

  // ---------------------------------------------------------------- relationships
  rel({ apiName: 'territory', displayName: 'Territory', sourceTableId: 'accounts', targetTableId: 'territories', inverseApiName: 'accounts', onDelete: 'setNull' }),
  rel({ apiName: 'parent_account', displayName: 'Parent account', sourceTableId: 'accounts', targetTableId: 'accounts', inverseApiName: 'subsidiaries', onDelete: 'setNull' }),
  rel({ apiName: 'account', displayName: 'Account', sourceTableId: 'account_team', targetTableId: 'accounts', inverseApiName: 'team', required: true, onDelete: 'cascade' }),
  rel({ apiName: 'account', displayName: 'Account', sourceTableId: 'contacts', targetTableId: 'accounts', inverseApiName: 'contacts', onDelete: 'setNull' }),
  rel({ apiName: 'reports_to', displayName: 'Reports to', sourceTableId: 'contacts', targetTableId: 'contacts', inverseApiName: 'direct_reports', onDelete: 'setNull' }),
  rel({ apiName: 'campaign', displayName: 'Campaign', sourceTableId: 'leads', targetTableId: 'campaigns', inverseApiName: 'leads', onDelete: 'setNull' }),
  rel({ apiName: 'converted_account', displayName: 'Converted account', sourceTableId: 'leads', targetTableId: 'accounts', inverseApiName: 'converted_leads', onDelete: 'setNull' }),
  rel({ apiName: 'converted_contact', displayName: 'Converted contact', kind: 'oneToOne', sourceTableId: 'leads', targetTableId: 'contacts', inverseApiName: 'source_lead', onDelete: 'setNull' }),
  rel({ apiName: 'members', displayName: 'Members', kind: 'manyToMany', sourceTableId: 'campaigns', targetTableId: 'contacts', inverseApiName: 'campaigns' }),
  rel({ apiName: 'account', displayName: 'Account', sourceTableId: 'opportunities', targetTableId: 'accounts', inverseApiName: 'opportunities', required: true, onDelete: 'restrict' }),
  rel({ apiName: 'primary_contact', displayName: 'Primary contact', sourceTableId: 'opportunities', targetTableId: 'contacts', inverseApiName: 'opportunities', onDelete: 'setNull' }),
  rel({ apiName: 'campaign', displayName: 'Campaign', sourceTableId: 'opportunities', targetTableId: 'campaigns', inverseApiName: 'opportunities', onDelete: 'setNull' }),
  rel({ apiName: 'stakeholders', displayName: 'Stakeholders', kind: 'manyToMany', sourceTableId: 'opportunities', targetTableId: 'contacts', inverseApiName: 'stakeholder_in' }),
  rel({ apiName: 'competitors', displayName: 'Competitors', kind: 'manyToMany', sourceTableId: 'opportunities', targetTableId: 'competitors', inverseApiName: 'opportunities' }),
  rel({ apiName: 'opportunity', displayName: 'Opportunity', sourceTableId: 'opportunity_line_items', targetTableId: 'opportunities', inverseApiName: 'line_items', required: true, onDelete: 'cascade' }),
  rel({ apiName: 'product', displayName: 'Product', sourceTableId: 'opportunity_line_items', targetTableId: 'products', inverseApiName: 'line_items', required: true, onDelete: 'restrict' }),
  rel({ apiName: 'opportunity', displayName: 'Opportunity', sourceTableId: 'quotes', targetTableId: 'opportunities', inverseApiName: 'quotes', required: true, onDelete: 'cascade' }),
  rel({ apiName: 'contact', displayName: 'Contact', sourceTableId: 'quotes', targetTableId: 'contacts', inverseApiName: 'quotes', onDelete: 'setNull' }),
  rel({ apiName: 'account', displayName: 'Account', sourceTableId: 'contracts', targetTableId: 'accounts', inverseApiName: 'contracts', required: true, onDelete: 'cascade' }),
  rel({ apiName: 'quote', displayName: 'Quote', kind: 'oneToOne', sourceTableId: 'contracts', targetTableId: 'quotes', inverseApiName: 'contract', onDelete: 'setNull' }),
  rel({ apiName: 'account', displayName: 'Account', sourceTableId: 'invoices', targetTableId: 'accounts', inverseApiName: 'invoices', required: true, onDelete: 'restrict' }),
  rel({ apiName: 'contract', displayName: 'Contract', sourceTableId: 'invoices', targetTableId: 'contracts', inverseApiName: 'invoices', onDelete: 'setNull' }),
  rel({ apiName: 'account', displayName: 'Account', sourceTableId: 'activities', targetTableId: 'accounts', inverseApiName: 'activities', onDelete: 'cascade' }),
  rel({ apiName: 'contact', displayName: 'Contact', sourceTableId: 'activities', targetTableId: 'contacts', inverseApiName: 'activities', onDelete: 'setNull' }),
  rel({ apiName: 'opportunity', displayName: 'Opportunity', sourceTableId: 'activities', targetTableId: 'opportunities', inverseApiName: 'activities', onDelete: 'cascade' }),
  rel({ apiName: 'lead', displayName: 'Lead', sourceTableId: 'activities', targetTableId: 'leads', inverseApiName: 'activities', onDelete: 'cascade' }),
  rel({ apiName: 'account', displayName: 'Account', sourceTableId: 'cases', targetTableId: 'accounts', inverseApiName: 'cases', onDelete: 'setNull' }),
  rel({ apiName: 'contact', displayName: 'Contact', sourceTableId: 'cases', targetTableId: 'contacts', inverseApiName: 'cases', onDelete: 'setNull' }),
  rel({ apiName: 'product', displayName: 'Product', sourceTableId: 'cases', targetTableId: 'products', inverseApiName: 'cases', onDelete: 'setNull' }),
  rel({ apiName: 'case', displayName: 'Case', sourceTableId: 'case_comments', targetTableId: 'cases', inverseApiName: 'comments', required: true, onDelete: 'cascade' }),
  rel({ apiName: 'products', displayName: 'Products', kind: 'manyToMany', sourceTableId: 'knowledge_articles', targetTableId: 'products', inverseApiName: 'articles' }),
  rel({ apiName: 'territory', displayName: 'Territory', sourceTableId: 'sales_targets', targetTableId: 'territories', inverseApiName: 'targets', onDelete: 'setNull' }),

  // ---------------------------------------------------------------- views
  view('territories', 'all', 'All territories', ['name', 'region', 'manager_email', 'countries'], [], true),
  view('accounts', 'all', 'All accounts', ['name', 'type', 'industry', 'billing_country', 'owner_email', 'lifetime_value'], [by('name')], true),
  view('accounts', 'customers_by_value', 'Customers by lifetime value', ['name', 'segment', 'lifetime_value', 'health_score', 'customer_since'], [by('lifetime_value', true)]),
  view('accounts', 'health', 'Account health', ['name', 'health_score', 'tags', 'last_activity_at', 'owner_email']),
  view('account_team', 'all', 'Account teams', ['account', 'user_email', 'role', 'since'], [], true),
  view('contacts', 'all', 'All contacts', ['last_name', 'first_name', 'email', 'account', 'job_title', 'phone'], [by('last_name')], true),
  view('contacts', 'marketing', 'Marketing audience', ['email', 'first_name', 'last_name', 'account', 'email_opt_in', 'lead_score']),
  view('leads', 'all', 'All leads', ['name', 'company_name', 'email', 'source', 'status', 'owner_email'], [], true),
  view('leads', 'hot', 'Hot leads', ['name', 'company_name', 'rating', 'estimated_value', 'owner_email']),
  view('campaigns', 'all', 'All campaigns', ['name', 'type', 'status', 'start_date', 'budget', 'leads_generated'], [], true),
  view('products', 'catalog', 'Price list', ['sku', 'name', 'family', 'unit', 'list_price', 'is_active'], [by('name')], true),
  view('competitors', 'all', 'Competitors', ['name', 'threat_level', 'website'], [], true),
  view('opportunities', 'pipeline', 'Pipeline', ['name', 'account', 'stage', 'amount', 'currency', 'close_date', 'owner_email'], [by('close_date')], true),
  view('opportunities', 'largest', 'Largest deals', ['name', 'account', 'stage', 'amount', 'probability', 'weighted_amount'], [by('amount', true)]),
  view('opportunity_line_items', 'all', 'Line items', ['opportunity', 'product', 'quantity', 'unit_price', 'discount_percent', 'line_total'], [], true),
  view('quotes', 'all', 'Quotes', ['quote_number', 'opportunity', 'status', 'total', 'valid_until'], [], true),
  view('contracts', 'all', 'Contracts', ['contract_number', 'account', 'status', 'start_date', 'end_date', 'value'], [], true),
  view('invoices', 'receivables', 'Receivables', ['invoice_number', 'account', 'status', 'due_date', 'amount'], [], true),
  view('activities', 'agenda', 'Agenda', ['subject', 'type', 'status', 'due_at', 'account', 'assignee_email'], [], true),
  view('cases', 'queue', 'Support queue', ['case_number', 'subject', 'priority', 'status', 'account', 'sla_due_at'], [by('sla_due_at')], true),
  view('case_comments', 'all', 'Comments', ['case', 'author_email', 'is_public', 'body'], [], true),
  view('knowledge_articles', 'all', 'Knowledge base', ['title', 'category', 'status', 'helpful_votes', 'published_on'], [], true),
  view('sales_targets', 'all', 'Targets', ['name', 'owner_email', 'period_start', 'quota', 'achieved'], [], true),
  view('audit_log', 'all', 'Audit log', ['message', 'level', 'category', 'ref'], [], true),
  view('kpi_snapshots', 'all', 'KPI history', ['metric', 'value', 'captured_at'], [], true),
];

// ---------------------------------------------------------------- V2: automations

const step = (id, action, input, condition) => ({ id, action, input, ...(condition ? { condition } : {}) });
const audit = (message, category, level = 'info', ref = '{{ row.id }}', extra = {}) =>
  step('audit', 'records.create@1', { table: 'audit_log', values: { message, category, level, ref, ...extra } });
const flow = (apiName, displayName, description, trigger, steps, condition) =>
  ({ op: 'create', type: 'flow', value: { apiName, displayName, description, trigger, steps, ...(condition ? { condition } : {}) } });
/** An amount in EUR: the opportunity currencies at the rates the price list uses. */
const inEur = (amount, currency) => `round(coalesce(${amount}, 0) / (${currency} == 'USD' ? 1.09 : ${currency} == 'GBP' ? 0.86 : ${currency} == 'CZK' ? 25.2 : `
  + `${currency} == 'CHF' ? 0.95 : ${currency} == 'SEK' ? 11.4 : ${currency} == 'PLN' ? 4.3 : ${currency} == 'JPY' ? 162 : ${currency} == 'AUD' ? 1.66 : 1), 2)`;
const lineTotal = (r) => `round(${r}.quantity * ${r}.unit_price * (1 - coalesce(${r}.discount_percent, 0) / 100), 2)`;
const readOnly = (path) => ({ op: 'update', type: 'field', target: path, value: { readOnly: true } });

export const MODEL_V2 = [
  // What automations own from now on.
  ...['accounts.lifetime_value', 'accounts.last_activity_at', 'contacts.lead_score', 'leads.converted_at', 'campaigns.leads_generated',
    'opportunities.weighted_amount', 'opportunities.won_at', 'opportunities.closed_at', 'opportunity_line_items.line_total',
    'quotes.sent_at', 'invoices.paid_at', 'activities.completed_at', 'cases.sla_due_at', 'cases.first_response_at', 'cases.resolved_at',
    'cases.escalated', 'sales_targets.achieved'].map(readOnly),

  // ---- sales
  flow('opportunity_created', 'Opportunity created', 'Computes the weighted amount of a new opportunity and logs it.',
    { event: 'row.created', tableId: 'opportunities' }, [
      step('weigh', 'records.update@1', { table: 'opportunities', id: '{{ row.id }}',
        values: { weighted_amount: '{{ round(coalesce(row.amount, 0) * coalesce(row.probability, 0) / 100, 2) }}' } }),
      audit('New opportunity {{ row.name }}', 'sales'),
    ]),
  flow('opportunity_reweigh', 'Re-weigh opportunity', 'Keeps weighted_amount = amount × probability when either changes.',
    { event: 'row.updated', tableId: 'opportunities', changedFields: ['amount', 'probability'] }, [
      step('weigh', 'records.update@1', { table: 'opportunities', id: '{{ row.id }}',
        values: { weighted_amount: '{{ round(coalesce(row.amount, 0) * coalesce(row.probability, 0) / 100, 2) }}' } }),
    ]),
  flow('opportunity_won', 'Opportunity won', 'Stamps the win, adds the amount (converted to EUR) to the account\'s lifetime value, makes the account a customer, credits the owner\'s sales target, '
    + 'notifies the team about big wins and announces opportunity_won for the follow-up flows.',
    { event: 'row.updated', tableId: 'opportunities', changedFields: ['stage'] }, [
      step('account', 'records.lookup@1', { table: 'accounts', id: '{{ row.account }}' }),
      step('stamp', 'records.update@1', { table: 'opportunities', id: '{{ row.id }}',
        values: { won_at: '{{ now() }}', closed_at: '{{ now() }}', probability: 100, forecast_category: 'closed', weighted_amount: '{{ row.amount }}' } }),
      step('credit', 'records.update@1', { table: 'accounts', id: '{{ row.account }}', values: {
        lifetime_value: `{{ round(coalesce(steps.account.lifetime_value, 0) + ${inEur('row.amount', 'row.currency')}, 2) }}`,
        type: 'customer', customer_since: '{{ coalesce(steps.account.customer_since, today()) }}', version: '{{ steps.account.version }}' } }),
      step('target', 'records.lookup@1', { table: 'sales_targets', filter: { and: [
        { field: 'owner_email', op: 'eq', value: '{{ coalesce(row.owner_email, "-") }}' },
        { field: 'period_start', op: 'lte', value: '{{ today() }}' }, { field: 'period_end', op: 'gte', value: '{{ today() }}' }] } }),
      step('quota', 'records.update@1', { table: 'sales_targets', id: '{{ steps.target.id }}',
        values: { achieved: `{{ round(coalesce(steps.target.achieved, 0) + ${inEur('row.amount', 'row.currency')}, 2) }}`, version: '{{ steps.target.version }}' } }, 'steps.target.id != null'),
      step('tell', 'dcms.notifications.raise@1', { title: 'Big deal won', body: '{{ row.name }}: {{ row.amount }} {{ row.currency }}', severity: 'info' }, 'row.amount >= 150000'),
      step('announce', 'event.publish@1', { name: 'opportunity_won', payload: { opportunity: '{{ row.id }}', account: '{{ row.account }}', amount: '{{ row.amount }}', owner: '{{ row.owner_email }}' } }),
    ], "row.stage == 'closed_won' && previous.stage != 'closed_won'"),
  flow('opportunity_lost', 'Opportunity lost', 'Stamps the loss, zeroes the forecast and records the reason.',
    { event: 'row.updated', tableId: 'opportunities', changedFields: ['stage'] }, [
      step('stamp', 'records.update@1', { table: 'opportunities', id: '{{ row.id }}',
        values: { closed_at: '{{ now() }}', probability: 0, forecast_category: 'omitted', weighted_amount: 0 } }),
      audit('Lost {{ row.name }} ({{ coalesce(row.loss_reason, "no reason given") }})', 'sales', 'warning'),
    ], "row.stage == 'closed_lost' && previous.stage != 'closed_lost'"),
  flow('on_opportunity_won', 'Start onboarding', 'Reacts to opportunity_won: logs it and starts the onboarding flow for the account.',
    { event: 'flow.event.opportunity_won' }, [
      audit('Won {{ event.payload.amount }} on account {{ event.payload.account }}', 'sales', 'info', '{{ event.payload.opportunity }}'),
      step('onboard', 'flow.invoke@1', { flow: 'onboard_account', input: { account: '{{ event.payload.account }}', opportunity: '{{ event.payload.opportunity }}', owner: '{{ event.payload.owner }}' } }),
    ]),
  flow('onboard_account', 'Onboard account', 'Creates the kick-off meeting and the 30-day check-in for a new customer. Run by "Start onboarding", or by hand with input { account, opportunity, owner }.',
    { event: 'manual' }, [
      step('kickoff', 'records.create@1', { table: 'activities', values: { subject: 'Onboarding kick-off', type: 'meeting', status: 'planned', priority: 'high',
        due_at: '{{ addDays(now(), 5) }}', duration_minutes: 60, account: '{{ input.account }}', opportunity: '{{ input.opportunity }}', assignee_email: '{{ input.owner }}' } }),
      step('checkin', 'records.create@1', { table: 'activities', values: { subject: '30-day check-in', type: 'call', status: 'planned',
        due_at: '{{ addDays(now(), 30) }}', duration_minutes: 30, account: '{{ input.account }}', assignee_email: '{{ input.owner }}' } }),
    ]),
  flow('line_item_added', 'Line item added', 'Computes the line total and adds it to the opportunity amount.',
    { event: 'row.created', tableId: 'opportunity_line_items' }, [
      step('total', 'records.update@1', { table: 'opportunity_line_items', id: '{{ row.id }}', values: { line_total: `{{ ${lineTotal('row')} }}` } }),
      step('opportunity', 'records.lookup@1', { table: 'opportunities', id: '{{ row.opportunity }}' }),
      step('amount', 'records.update@1', { table: 'opportunities', id: '{{ row.opportunity }}',
        values: { amount: `{{ round(coalesce(steps.opportunity.amount, 0) + ${lineTotal('row')}, 2) }}`, version: '{{ steps.opportunity.version }}' } }),
    ]),
  flow('line_item_changed', 'Line item changed', 'Recomputes the line total and moves the opportunity amount by the difference.',
    { event: 'row.updated', tableId: 'opportunity_line_items', changedFields: ['quantity', 'unit_price', 'discount_percent'] }, [
      step('total', 'records.update@1', { table: 'opportunity_line_items', id: '{{ row.id }}', values: { line_total: `{{ ${lineTotal('row')} }}` } }),
      step('opportunity', 'records.lookup@1', { table: 'opportunities', id: '{{ row.opportunity }}' }),
      step('amount', 'records.update@1', { table: 'opportunities', id: '{{ row.opportunity }}',
        values: { amount: `{{ round(coalesce(steps.opportunity.amount, 0) + ${lineTotal('row')} - ${lineTotal('previous')}, 2) }}`, version: '{{ steps.opportunity.version }}' } }),
    ]),
  flow('line_item_removed', 'Line item removed', 'Takes a deleted line\'s total off the opportunity amount.',
    { event: 'row.deleted', tableId: 'opportunity_line_items' }, [
      step('opportunity', 'records.lookup@1', { table: 'opportunities', id: '{{ row.opportunity }}' }),
      step('amount', 'records.update@1', { table: 'opportunities', id: '{{ row.opportunity }}',
        values: { amount: '{{ round(coalesce(steps.opportunity.amount, 0) - coalesce(row.line_total, 0), 2) }}', version: '{{ steps.opportunity.version }}' } }, 'steps.opportunity.id != null'),
    ]),
  flow('stakeholder_added', 'Stakeholder added', 'Logs a contact joining an opportunity\'s buying committee.',
    { event: 'relation.created', relationshipId: 'opportunities.stakeholders' }, [
      audit('Stakeholder {{ event.payload.targetId }} added', 'sales', 'debug', '{{ event.payload.sourceId }}')]),
  flow('stakeholder_removed', 'Stakeholder removed', 'Logs a contact leaving an opportunity\'s buying committee.',
    { event: 'relation.deleted', relationshipId: 'opportunities.stakeholders' }, [
      audit('Stakeholder {{ event.payload.targetId }} removed', 'sales', 'debug', '{{ event.payload.sourceId }}')]),

  // ---- quotes, contracts, invoices
  flow('quote_sent', 'Quote sent', 'Stamps sent_at, logs an email activity and moves an early-stage opportunity to proposal.',
    { event: 'row.updated', tableId: 'quotes', changedFields: ['status'] }, [
      step('opportunity', 'records.lookup@1', { table: 'opportunities', id: '{{ row.opportunity }}' }),
      step('stamp', 'records.update@1', { table: 'quotes', id: '{{ row.id }}', values: { sent_at: '{{ now() }}' } }),
      step('log', 'records.create@1', { table: 'activities', values: { subject: 'Sent quote {{ row.quote_number }}', type: 'email', status: 'completed',
        due_at: '{{ now() }}', account: '{{ steps.opportunity.account }}', opportunity: '{{ row.opportunity }}', contact: '{{ row.contact }}' } }),
      step('advance', 'records.update@1', { table: 'opportunities', id: '{{ row.opportunity }}', values: { stage: 'proposal', probability: 50 } },
        "steps.opportunity.stage in ['prospecting', 'qualification', 'needs_analysis']"),
    ], "row.status == 'sent' && previous.status != 'sent'"),
  flow('quote_accepted', 'Quote accepted', 'Wins the opportunity (which runs Opportunity won) and drafts a 12-month contract linked to the quote.',
    { event: 'row.updated', tableId: 'quotes', changedFields: ['status'] }, [
      step('opportunity', 'records.lookup@1', { table: 'opportunities', id: '{{ row.opportunity }}' }),
      step('win', 'records.update@1', { table: 'opportunities', id: '{{ row.opportunity }}', values: { stage: 'closed_won' } }, "steps.opportunity.stage != 'closed_won'"),
      step('contract', 'records.create@1', { table: 'contracts', values: { contract_number: 'CT-{{ row.quote_number }}', status: 'draft', account: '{{ steps.opportunity.account }}',
        quote: '{{ row.id }}', start_date: '{{ addDays(today(), 14) }}', end_date: '{{ addDays(today(), 379) }}', term_months: 12,
        value: '{{ coalesce(row.subtotal, steps.opportunity.amount) }}', billing_frequency: 'annually' } }),
    ], "row.status == 'accepted' && previous.status != 'accepted'"),
  flow('contract_activated', 'Contract activated', 'Issues the contract\'s first invoice (the first period of its billing frequency) due in 30 days.',
    { event: 'row.updated', tableId: 'contracts', changedFields: ['status'] }, [
      step('invoice', 'records.create@1', { table: 'invoices', values: { invoice_number: 'INV-{{ row.contract_number }}-01', status: 'issued', account: '{{ row.account }}',
        contract: '{{ row.id }}', issue_date: '{{ today() }}', due_date: '{{ addDays(today(), 30) }}', payment_method: 'bank_transfer',
        amount: "{{ round(coalesce(row.value, 0) / (row.billing_frequency == 'monthly' ? 12 : row.billing_frequency == 'quarterly' ? 4 : 1), 2) }}",
        tax_amount: "{{ round(coalesce(row.value, 0) / (row.billing_frequency == 'monthly' ? 12 : row.billing_frequency == 'quarterly' ? 4 : 1) * 0.21, 2) }}" } }),
      audit('Contract {{ row.contract_number }} activated; issued {{ steps.invoice.invoice_number }}', 'billing'),
    ], "row.status == 'active' && previous.status != 'active'"),
  flow('invoice_paid', 'Invoice paid', 'Stamps the payment and announces invoice_paid.',
    { event: 'row.updated', tableId: 'invoices', changedFields: ['status'] }, [
      step('stamp', 'records.update@1', { table: 'invoices', id: '{{ row.id }}', values: { paid_at: '{{ now() }}' } }),
      step('announce', 'event.publish@1', { name: 'invoice_paid', payload: { invoice: '{{ row.id }}', amount: '{{ row.amount }}' } }),
    ], "row.status == 'paid' && previous.status != 'paid'"),
  flow('on_invoice_paid', 'Log payment', 'Reacts to invoice_paid.', { event: 'flow.event.invoice_paid' }, [
    audit('Payment of {{ event.payload.amount }} received', 'billing', 'info', '{{ event.payload.invoice }}')]),

  // ---- leads and marketing
  flow('lead_intake', 'Lead intake', 'For leads from the website or a form: rates large ones hot, credits the campaign, schedules a follow-up call for tomorrow and logs whether the company is already an account.',
    { event: 'row.created', tableId: 'leads' }, [
      step('known', 'records.query@1', { table: 'accounts', filter: { field: 'name', op: 'eq', value: '{{ row.company_name }}' }, pageSize: 1 }, 'row.company_name != null'),
      step('rate', 'records.update@1', { table: 'leads', id: '{{ row.id }}', values: { rating: 'hot' } }, 'coalesce(row.estimated_value, 0) >= 50000'),
      step('campaign', 'records.lookup@1', { table: 'campaigns', id: '{{ row.campaign }}' }, 'row.campaign != null'),
      step('credit', 'records.update@1', { table: 'campaigns', id: '{{ row.campaign }}',
        values: { leads_generated: '{{ coalesce(steps.campaign.leads_generated, 0) + 1 }}', version: '{{ steps.campaign.version }}' } }, 'steps.campaign.id != null'),
      step('follow_up', 'records.create@1', { table: 'activities', values: { subject: 'Call back {{ row.name }}', type: 'call', status: 'planned', priority: 'high',
        due_at: '{{ addDays(now(), 1) }}', lead: '{{ row.id }}', assignee_email: '{{ row.owner_email }}', description: '{{ row.message }}' } }),
      audit('Lead {{ row.email }} from {{ row.source }}; existing account matches: {{ coalesce(steps.known.total, 0) }}', 'marketing'),
    ], "row.source in ['website', 'form']"),
  flow('lead_spam_filter', 'Lead spam filter', 'Deletes web leads from throw-away domains or carrying links, and logs it.',
    { event: 'row.created', tableId: 'leads' }, [
      step('drop', 'records.delete@1', { table: 'leads', id: '{{ row.id }}' }),
      audit('Dropped spam lead {{ row.email }}', 'marketing', 'warning'),
    ], "endsWith(row.email, '@mailinator.com') || contains(coalesce(row.message, ''), 'http://')"),
  flow('convert_lead', 'Convert lead', 'Turns a qualified lead into an account (or reuses the existing one), a contact and an opportunity, then marks the lead converted. Input: { lead }.',
    { event: 'manual' }, [
      step('lead', 'records.lookup@1', { table: 'leads', id: '{{ input.lead }}' }),
      step('account_match', 'records.query@1', { table: 'accounts', filter: { field: 'name', op: 'eq', value: '{{ coalesce(steps.lead.company_name, steps.lead.name) }}' }, pageSize: 1 }),
      step('new_account', 'records.create@1', { table: 'accounts', values: { name: '{{ coalesce(steps.lead.company_name, steps.lead.name) }}', type: 'prospect',
        owner_email: '{{ steps.lead.owner_email }}', billing_country: '{{ steps.lead.country }}' } }, 'steps.account_match.total == 0'),
      step('contact_match', 'records.query@1', { table: 'contacts', filter: { field: 'email', op: 'eq', value: '{{ steps.lead.email }}' }, pageSize: 1 }),
      step('new_contact', 'records.create@1', { table: 'contacts', values: { email: '{{ steps.lead.email }}', last_name: '{{ steps.lead.name }}', job_title: '{{ steps.lead.job_title }}',
        phone: '{{ steps.lead.phone }}', account: '{{ steps.account_match.total > 0 ? steps.account_match.items[0].id : steps.new_account.id }}' } }, 'steps.contact_match.total == 0'),
      step('opportunity', 'records.create@1', { table: 'opportunities', values: {
        name: '{{ coalesce(steps.lead.company_name, steps.lead.name) }} – first order ({{ steps.lead.id }})',
        account: '{{ steps.account_match.total > 0 ? steps.account_match.items[0].id : steps.new_account.id }}',
        primary_contact: '{{ steps.contact_match.total > 0 ? steps.contact_match.items[0].id : steps.new_contact.id }}',
        amount: '{{ coalesce(steps.lead.estimated_value, 0) }}', stage: 'qualification', probability: 20, close_date: '{{ addDays(today(), 60) }}',
        lead_source: '{{ steps.lead.source }}', campaign: '{{ steps.lead.campaign }}', owner_email: '{{ steps.lead.owner_email }}' } }),
      step('mark', 'records.update@1', { table: 'leads', id: '{{ input.lead }}', values: { status: 'converted', converted_at: '{{ now() }}',
        converted_account: '{{ steps.account_match.total > 0 ? steps.account_match.items[0].id : steps.new_account.id }}',
        converted_contact: '{{ steps.contact_match.total > 0 ? null : steps.new_contact.id }}' } }),
      audit('Converted lead {{ steps.lead.email }} into {{ steps.opportunity.name }}', 'sales', 'info', '{{ input.lead }}'),
    ]),

  // ---- support
  flow('case_opened', 'Case opened', 'Numbers the case, sets its SLA deadline from the priority, escalates urgent cases and notifies support about them.',
    { event: 'row.created', tableId: 'cases' }, [
      step('number', 'records.update@1', { table: 'cases', id: '{{ row.id }}', values: { case_number: 'WEB-{{ row.id }}' } }, 'row.case_number == null'),
      step('sla', 'records.update@1', { table: 'cases', id: '{{ row.id }}', values: {
        sla_due_at: "{{ addHours(now(), row.priority == 'urgent' ? 4 : row.priority == 'high' ? 8 : row.priority == 'low' ? 72 : 24) }}",
        escalated: "{{ row.priority == 'urgent' }}" } }),
      step('tell', 'dcms.notifications.raise@1', { title: 'Urgent case', body: '{{ row.subject }}', severity: 'warning' }, "row.priority == 'urgent'"),
      audit('Case opened: {{ row.subject }} ({{ row.priority }})', 'support'),
    ]),
  flow('case_progress', 'Case progress', 'Stamps the first response when a case leaves "new" and the resolution time when it is resolved.',
    { event: 'row.updated', tableId: 'cases', changedFields: ['status'] }, [
      step('first', 'records.update@1', { table: 'cases', id: '{{ row.id }}', values: { first_response_at: '{{ now() }}' } }, "previous.status == 'new' && row.first_response_at == null"),
      step('resolved', 'records.update@1', { table: 'cases', id: '{{ row.id }}', values: { resolved_at: '{{ now() }}' } }, "row.status == 'resolved'"),
    ]),
  flow('escalate_case', 'Escalate case', 'Makes a case urgent with a 2-hour deadline, adds an internal note and alerts support. Input: { case, reason }.',
    { event: 'manual' }, [
      step('case', 'records.lookup@1', { table: 'cases', id: '{{ input.case }}' }),
      step('raise', 'records.update@1', { table: 'cases', id: '{{ input.case }}', values: { priority: 'urgent', escalated: true, sla_due_at: '{{ addHours(now(), 2) }}' } }),
      step('note', 'records.create@1', { table: 'case_comments', values: { case: '{{ input.case }}', author_email: 'automation@northwind.example',
        is_public: false, body: 'Escalated: {{ coalesce(input.reason, "no reason given") }}' } }),
      step('tell', 'dcms.notifications.raise@1', { title: 'Case escalated', body: '{{ steps.case.case_number }} {{ steps.case.subject }}', severity: 'error' }),
    ]),
  flow('customer_replied', 'Customer replied', 'A public reply from the reporter to a case waiting on the customer reopens it.',
    { event: 'row.created', tableId: 'case_comments' }, [
      step('case', 'records.lookup@1', { table: 'cases', id: '{{ row.case }}' }),
      step('reopen', 'records.update@1', { table: 'cases', id: '{{ row.case }}', values: { status: 'open' } },
        "steps.case.status == 'pending' && lower(coalesce(steps.case.reporter_email, '')) == lower(row.author_email)"),
    ], 'row.is_public'),

  // ---- activities
  flow('activity_completed', 'Activity completed', 'Stamps completion; a completed call, meeting or demo also updates the account\'s last activity.',
    { event: 'row.updated', tableId: 'activities', changedFields: ['status'] }, [
      step('stamp', 'records.update@1', { table: 'activities', id: '{{ row.id }}', values: { completed_at: '{{ now() }}' } }),
      step('touch', 'records.update@1', { table: 'accounts', id: '{{ row.account }}', values: { last_activity_at: '{{ now() }}' } },
        "row.account != null && row.type in ['call', 'meeting', 'demo']"),
    ], "row.status == 'completed' && previous.status != 'completed'"),
  flow('log_call', 'Log a call', 'Records a completed call from the site or the admin. Input: { account, contact?, opportunity?, summary, outcome? }.',
    { event: 'manual' }, [
      step('call', 'records.create@1', { table: 'activities', values: { subject: 'Call: {{ input.summary }}', type: 'call', status: 'completed', due_at: '{{ now() }}',
        account: '{{ input.account }}', contact: '{{ input.contact }}', opportunity: '{{ input.opportunity }}', outcome: "{{ coalesce(input.outcome, 'neutral') }}",
        description: '{{ input.summary }}' } }),
      step('touch', 'records.update@1', { table: 'accounts', id: '{{ input.account }}', values: { last_activity_at: '{{ now() }}' } }),
    ]),

  // ---- forms, users and the platform
  flow('demo_request_form', 'Demo request form', 'Turns a "demo-request" form submission on the site into a lead (which then runs Lead intake).',
    { event: 'form.submitted' }, [
      step('lead', 'records.create@1', { table: 'leads', values: { name: '{{ event.payload.data.name }}', email: '{{ event.payload.data.email }}',
        company_name: '{{ event.payload.data.company }}', phone: '{{ event.payload.data.phone }}', message: '{{ event.payload.data.message }}', source: 'form',
        estimated_value: '{{ coalesce(number(event.payload.data.seats), 0) * 600 }}' } }),
    ], "event.payload.formName == 'demo-request'"),
  flow('support_request_form', 'Support request form', 'Turns a "support-request" form submission into a case for the matching contact, if any (Case opened then sets the SLA).',
    { event: 'form.submitted' }, [
      step('contact', 'records.lookup@1', { table: 'contacts', filter: { field: 'email', op: 'eq', value: '{{ event.payload.data.email }}' } }),
      step('case', 'records.create@1', { table: 'cases', values: { subject: '{{ event.payload.data.subject }}', description: '{{ event.payload.data.message }}', channel: 'form',
        priority: "{{ coalesce(event.payload.data.priority, 'normal') }}", reporter_email: '{{ event.payload.data.email }}',
        contact: '{{ steps.contact.id }}', account: '{{ steps.contact.account }}' } }),
    ], "event.payload.formName == 'support-request'"),
  flow('portal_invite', 'Invite to customer portal', 'Invites a customer contact to the site\'s customer portal (group "CRM Customers"). Input: { email, name }.',
    { event: 'manual' }, [
      step('invite', 'user-auth.invite@1', { email: '{{ input.email }}', displayName: '{{ input.name }}', groups: ['CRM Customers'] }),
      audit('Invited {{ input.email }} to the customer portal', 'access', 'info', '{{ input.email }}'),
    ]),
  flow('grant_partner_access', 'Grant partner access', 'Puts a site user into the "CRM Partners" group. Input: { email }.',
    { event: 'manual' }, [
      step('grant', 'user-auth.add-to-group@1', { email: '{{ input.email }}', group: 'CRM Partners' }),
      audit('{{ input.email }} joined CRM Partners', 'access', 'info', '{{ input.email }}'),
    ]),
  flow('revoke_partner_access', 'Revoke partner access', 'Takes a site user out of the "CRM Partners" group. Input: { email }.',
    { event: 'manual' }, [
      step('revoke', 'user-auth.remove-from-group@1', { email: '{{ input.email }}', group: 'CRM Partners' }),
      audit('{{ input.email }} left CRM Partners', 'access', 'warning', '{{ input.email }}'),
    ]),
  flow('user_invited', 'Site user invited', 'Logs every site user invitation.', { event: 'user.invited' }, [
    audit('Site user invited: {{ event.payload.email }}', 'access', 'info', '{{ event.payload.userId }}')]),
  flow('user_activated', 'Site user activated', 'Logs activations; a contact who activated is noted on the contact.', { event: 'user.activated' }, [
    step('contact', 'records.lookup@1', { table: 'contacts', filter: { field: 'email', op: 'eq', value: '{{ event.payload.email }}' } }),
    step('note', 'records.update@1', { table: 'contacts', id: '{{ steps.contact.id }}', values: { notes: '{{ coalesce(steps.contact.notes, "") }} Activated portal access {{ today() }}.' } }, 'steps.contact.id != null'),
    audit('Site user activated: {{ event.payload.email }}', 'access', 'info', '{{ event.payload.userId }}'),
  ]),
  flow('model_published', 'Model published', 'Logs every published revision of this app.', { event: 'revision.published' }, [
    audit('App model revision published', 'model', 'info', '{{ event.payload.revisionId }}')]),

  // ---- reporting and email
  flow('kpi_hourly', 'Hourly KPIs', 'Captures open pipeline, wins, open and urgent cases and new leads into kpi_snapshots every hour.',
    { event: 'schedule', everyMinutes: 60 }, [
      step('open_pipeline', 'records.query@1', { table: 'opportunities', filter: { field: 'stage', op: 'nin', value: ['closed_won', 'closed_lost'] }, pageSize: 1 }),
      step('won', 'records.query@1', { table: 'opportunities', filter: { field: 'stage', op: 'eq', value: 'closed_won' }, pageSize: 1 }),
      step('open_cases', 'records.query@1', { table: 'cases', filter: { field: 'status', op: 'in', value: ['new', 'open', 'pending'] }, pageSize: 1 }),
      step('urgent', 'records.query@1', { table: 'cases', filter: { and: [{ field: 'status', op: 'in', value: ['new', 'open'] }, { field: 'priority', op: 'eq', value: 'urgent' }] }, pageSize: 1 }),
      step('new_leads', 'records.query@1', { table: 'leads', filter: { field: 'status', op: 'eq', value: 'new' }, pageSize: 1 }),
      ...[['open_pipeline_count', 'open_pipeline'], ['won_count', 'won'], ['open_cases', 'open_cases'], ['urgent_cases', 'urgent'], ['new_leads', 'new_leads']].map(([metric, from]) =>
        step(`save_${from}`, 'records.create@1', { table: 'kpi_snapshots', values: { metric, value: `{{ steps.${from}.total }}`, captured_at: '{{ now() }}' } })),
    ]),
  flow('renewals_weekly', 'Weekly renewals', 'Every week, notifies the team how many active contracts end within 60 days.',
    { event: 'schedule', everyMinutes: 10080 }, [
      step('ending', 'records.query@1', { table: 'contracts', filter: { and: [{ field: 'status', op: 'eq', value: 'active' },
        { field: 'end_date', op: 'lte', value: '{{ addDays(today(), 60) }}' }, { field: 'end_date', op: 'gte', value: '{{ today() }}' }] }, sort: [{ field: 'end_date', direction: 'asc' }], pageSize: 5 }),
      step('tell', 'dcms.notifications.raise@1', { title: 'Renewals due', body: '{{ steps.ending.total }} contracts end within 60 days, first {{ steps.ending.items[0].contract_number }}', severity: 'info' }, 'steps.ending.total > 0'),
    ]),
  flow('send_quote', 'Email a quote', 'Emails a quote to the customer and marks it sent (which runs Quote sent). Input: { quote, to }.',
    { event: 'manual' }, [
      step('quote', 'records.lookup@1', { table: 'quotes', id: '{{ input.quote }}' }),
      step('opportunity', 'records.lookup@1', { table: 'opportunities', id: '{{ steps.quote.opportunity }}' }),
      step('mail', 'dcms.email.send@1', { to: '{{ input.to }}', subject: 'Your quote {{ steps.quote.quote_number }}',
        html: '<p>Hello,</p><p>please find our offer <strong>{{ steps.quote.quote_number }}</strong> for {{ steps.opportunity.name }}: '
          + '{{ steps.quote.total }} {{ steps.opportunity.currency }} including tax, valid until {{ steps.quote.valid_until }}.</p><p>Northwind Sales</p>' }),
      step('mark', 'records.update@1', { table: 'quotes', id: '{{ input.quote }}', values: { status: 'sent' } }, "steps.quote.status == 'draft'"),
    ]),
  flow('pipeline_digest', 'Pipeline digest', 'Emails a short pipeline summary. Input: { to }.', { event: 'manual' }, [
    step('open', 'records.query@1', { table: 'opportunities', filter: { field: 'stage', op: 'nin', value: ['closed_won', 'closed_lost'] }, sort: [{ field: 'amount', direction: 'desc' }], pageSize: 5 }),
    step('urgent', 'records.query@1', { table: 'cases', filter: { and: [{ field: 'status', op: 'in', value: ['new', 'open'] }, { field: 'priority', op: 'eq', value: 'urgent' }] }, pageSize: 1 }),
    step('mail', 'dcms.email.send@1', { to: '{{ input.to }}', subject: 'Pipeline digest {{ today() }}',
      text: '{{ steps.open.total }} open opportunities. Largest: {{ join(steps.open.items, ", ") }}.\n{{ steps.urgent.total }} urgent support cases open.' }),
  ]),
  flow('adjust_lifetime_value', 'Adjust lifetime value', 'Corrects an account\'s lifetime value by a signed EUR amount and logs why. Input: { account, delta, reason }. For finance and sales managers.',
    { event: 'manual' }, [
      step('account', 'records.lookup@1', { table: 'accounts', id: '{{ input.account }}' }),
      step('adjust', 'records.update@1', { table: 'accounts', id: '{{ input.account }}',
        values: { lifetime_value: '{{ round(coalesce(steps.account.lifetime_value, 0) + number(input.delta), 2) }}', version: '{{ steps.account.version }}' } }),
      audit('Lifetime value of {{ steps.account.name }} adjusted by {{ input.delta }}: {{ coalesce(input.reason, "no reason given") }}', 'sales', 'warning', '{{ input.account }}'),
    ]),
  flow('adjust_target', 'Adjust sales target', 'Credits a signed EUR amount to a rep\'s current sales target and logs why. Input: { owner, amount, reason }.',
    { event: 'manual' }, [
      step('target', 'records.lookup@1', { table: 'sales_targets', filter: { and: [
        { field: 'owner_email', op: 'eq', value: '{{ input.owner }}' },
        { field: 'period_start', op: 'lte', value: '{{ today() }}' }, { field: 'period_end', op: 'gte', value: '{{ today() }}' }] } }),
      step('credit', 'records.update@1', { table: 'sales_targets', id: '{{ steps.target.id }}',
        values: { achieved: '{{ round(coalesce(steps.target.achieved, 0) + number(input.amount), 2) }}', version: '{{ steps.target.version }}' } }, 'steps.target.id != null'),
      audit('Target of {{ input.owner }} credited {{ input.amount }}: {{ coalesce(input.reason, "no reason given") }}', 'sales', 'info', '{{ steps.target.id }}'),
    ]),
];

// ---------------------------------------------------------------- V3: schema change over live data

export const SETTINGS_V3 = SETTINGS_V1 + '\n\nRevision 3: accounts.rating (free text) is replaced by the account_rating choice; contacts.fax is deprecated; opportunities gained next_step_date.';

export const MODEL_V3 = [
  { op: 'create', type: 'field', target: 'accounts', value: f('account_rating', 'choice', 'Sales temperature of the account: hot, warm or cold. Migrated from the legacy rating text.',
    { choiceSetId: 'rating', copyFrom: 'rating' }) },
  { op: 'update', type: 'field', target: 'accounts.rating', value: { deprecated: true, description: 'Deprecated: use account_rating. Kept read-only for records whose text did not fit a choice.' } },
  { op: 'update', type: 'field', target: 'contacts.fax', value: { deprecated: true } },
  { op: 'create', type: 'field', target: 'opportunities', value: f('next_step_date', 'date', 'When the next step is due.') },
  { op: 'update', type: 'view', target: 'accounts.health', value: { columns: ['name', 'account_rating', 'health_score', 'tags', 'last_activity_at', 'owner_email'] } },
  view('opportunities', 'closing_soon', 'Closing soon', ['name', 'account', 'stage', 'amount', 'close_date', 'next_step', 'next_step_date'], [by('close_date')]),
  { op: 'update', type: 'settings', value: { description: SETTINGS_V3 } },
];

/** The database indexes a set of tables asks for, as RecordIndexes.Wanted counts them. */
export function indexesWanted(ops) {
  const searchable = new Set(['text', 'longText', 'email', 'url']);
  const indexable = new Set([...searchable, 'choice', 'date', 'dateTime', 'integer', 'decimal']);
  let n = 0;
  for (const t of ops.filter((o) => o.type === 'table').map((o) => o.value)) {
    for (const x of t.fields) if ((x.sortable || x.filterable) && indexable.has(x.type)) n += x.sortable ? 2 : 1;
    const s = t.fields.filter((x) => x.searchable && searchable.has(x.type)).length;
    n += s || (searchable.has(t.fields.find((x) => x.apiName === t.primaryFieldId)?.type) ? 1 : 0);
  }
  return n;
}
