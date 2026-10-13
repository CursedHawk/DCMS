// Deterministic, internally consistent data for the demo CRM (crm-demo-model.mjs): the same run
// gives the same rows. Lookups name their target by its primary field (account name, contact
// email, quote number…), which the import resolves; many-to-many links come back as pairs of
// primary values for the caller to resolve to ids.
//
// Consistency the reports rely on: an opportunity's amount is the sum of its line items, an
// account's lifetime value the sum of its won opportunities (in EUR), a quote's subtotal its
// opportunity's amount, a target's achieved the owner's wins in the period, and every date
// follows from the one before it (lead → opportunity → quote → contract → invoices).

const TODAY = Date.UTC(2026, 9, 13);
const DAY = 86_400_000;

function rng(seed) {
  return () => {
    seed |= 0; seed = (seed + 0x6D2B79F5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const rand = rng(20261013);
const pick = (xs) => xs[Math.floor(rand() * xs.length)];
const int = (lo, hi) => lo + Math.floor(rand() * (hi - lo + 1));
const chance = (p) => rand() < p;
/** Picks by weight: [[value, weight], …]. */
/** A fast picker over many weighted items, built once. */
const sampler = (items, weightOf) => {
  const cum = []; let total = 0;
  for (const x of items) cum.push(total += weightOf(x));
  return () => { const r = rand() * total; let lo = 0, hi = cum.length - 1; while (lo < hi) { const mid = (lo + hi) >> 1; if (cum[mid] < r) lo = mid + 1; else hi = mid; } return items[lo]; };
};
const weighted = (pairs) => { let r = rand() * pairs.reduce((s, [, w]) => s + w, 0); for (const [v, w] of pairs) if ((r -= w) <= 0) return v; return pairs.at(-1)[0]; };
const pad = (n, w = 6) => String(n).padStart(w, '0');
const date = (ms) => new Date(ms).toISOString().slice(0, 10);
const time = (ms) => new Date(ms).toISOString().replace(/\.\d{3}Z$/, 'Z');
const money = (x) => Math.round(x * 100) / 100;
const slug = (s) => s.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');

// ---------------------------------------------------------------- the world

const FX = { EUR: 1, USD: 1.09, GBP: 0.86, CZK: 25.2, CHF: 0.95, SEK: 11.4, PLN: 4.3, JPY: 162, AUD: 1.66 };
const C = (code, name, territory, phone, currency, suffixes, cities, streets, vat = true) => ({ code, name, territory, phone, currency, suffixes, cities, streets, vat });
const COUNTRIES = [
  [C('GB', 'United Kingdom', 'UK & Ireland', '+44', 'GBP', ['Ltd', 'plc'], ['London', 'Manchester', 'Edinburgh', 'Bristol', 'Leeds'], ['High Street', 'Victoria Road', 'King Street', 'Station Road']), 9],
  [C('IE', 'Ireland', 'UK & Ireland', '+353', 'EUR', ['Ltd', 'DAC'], ['Dublin', 'Cork', 'Galway'], ['Main Street', 'Grafton Street']), 2],
  [C('DE', 'Germany', 'DACH', '+49', 'EUR', ['GmbH', 'AG', 'SE'], ['Berlin', 'Munich', 'Hamburg', 'Frankfurt', 'Cologne', 'Stuttgart'], ['Hauptstraße', 'Bahnhofstraße', 'Industriestraße', 'Gartenweg']), 11],
  [C('AT', 'Austria', 'DACH', '+43', 'EUR', ['GmbH', 'AG'], ['Vienna', 'Graz', 'Linz', 'Salzburg'], ['Ringstraße', 'Mariahilfer Straße']), 3],
  [C('CH', 'Switzerland', 'DACH', '+41', 'CHF', ['AG', 'SA', 'GmbH'], ['Zurich', 'Geneva', 'Basel', 'Lausanne'], ['Bahnhofstrasse', 'Rue du Rhône']), 3],
  [C('SE', 'Sweden', 'Nordics & Benelux', '+46', 'SEK', ['AB'], ['Stockholm', 'Gothenburg', 'Malmö'], ['Drottninggatan', 'Kungsgatan']), 3],
  [C('FI', 'Finland', 'Nordics & Benelux', '+358', 'EUR', ['Oy'], ['Helsinki', 'Espoo', 'Tampere'], ['Mannerheimintie', 'Aleksanterinkatu']), 2],
  [C('DK', 'Denmark', 'Nordics & Benelux', '+45', 'EUR', ['A/S', 'ApS'], ['Copenhagen', 'Aarhus'], ['Strøget', 'Vesterbrogade']), 2],
  [C('NL', 'Netherlands', 'Nordics & Benelux', '+31', 'EUR', ['B.V.', 'N.V.'], ['Amsterdam', 'Rotterdam', 'Utrecht', 'Eindhoven'], ['Keizersgracht', 'Coolsingel', 'Stationsplein']), 5],
  [C('BE', 'Belgium', 'Nordics & Benelux', '+32', 'EUR', ['NV', 'SRL'], ['Brussels', 'Antwerp', 'Ghent'], ['Rue de la Loi', 'Meir']), 2],
  [C('FR', 'France', 'Southern Europe', '+33', 'EUR', ['SA', 'SAS', 'SARL'], ['Paris', 'Lyon', 'Toulouse', 'Lille', 'Nantes'], ['Rue de Rivoli', 'Avenue Victor Hugo', 'Boulevard Haussmann']), 7],
  [C('ES', 'Spain', 'Southern Europe', '+34', 'EUR', ['S.L.', 'S.A.'], ['Madrid', 'Barcelona', 'Valencia', 'Seville'], ['Gran Vía', 'Calle de Alcalá', 'Passeig de Gràcia']), 4],
  [C('IT', 'Italy', 'Southern Europe', '+39', 'EUR', ['S.p.A.', 'S.r.l.'], ['Milan', 'Rome', 'Turin', 'Bologna'], ['Via Roma', 'Corso Buenos Aires', 'Via Garibaldi']), 4],
  [C('PT', 'Portugal', 'Southern Europe', '+351', 'EUR', ['Lda', 'S.A.'], ['Lisbon', 'Porto'], ['Avenida da Liberdade', 'Rua Augusta']), 1],
  [C('CZ', 'Czechia', 'CEE', '+420', 'CZK', ['s.r.o.', 'a.s.'], ['Prague', 'Brno', 'Ostrava', 'Plzeň', 'Olomouc'], ['Václavské náměstí', 'Národní', 'Vinohradská', 'Masarykova']), 6],
  [C('SK', 'Slovakia', 'CEE', '+421', 'EUR', ['s.r.o.', 'a.s.'], ['Bratislava', 'Košice', 'Žilina'], ['Obchodná', 'Hlavná']), 2],
  [C('PL', 'Poland', 'CEE', '+48', 'PLN', ['Sp. z o.o.', 'S.A.'], ['Warsaw', 'Kraków', 'Wrocław', 'Poznań', 'Gdańsk'], ['Marszałkowska', 'Floriańska', 'Piotrkowska']), 5],
  [C('HU', 'Hungary', 'CEE', '+36', 'EUR', ['Kft.', 'Zrt.'], ['Budapest', 'Debrecen'], ['Andrássy út', 'Váci utca']), 2],
  [C('US', 'United States', 'NA East', '+1', 'USD', ['Inc.', 'LLC', 'Corp.'], ['New York', 'Boston', 'Chicago', 'Atlanta', 'Philadelphia', 'Miami'], ['Main Street', 'Broadway', 'Market Street', '5th Avenue'], false), 9],
  [C('US', 'United States', 'NA West', '+1', 'USD', ['Inc.', 'LLC', 'Corp.'], ['San Francisco', 'Seattle', 'Austin', 'Denver', 'Los Angeles', 'San Diego'], ['Mission Street', 'Pine Street', 'Congress Avenue'], false), 8],
  [C('CA', 'Canada', 'NA East', '+1', 'USD', ['Inc.', 'Ltd.'], ['Toronto', 'Montreal', 'Ottawa'], ['Bay Street', 'Rue Sainte-Catherine'], false), 3],
  [C('CA', 'Canada', 'NA West', '+1', 'USD', ['Inc.', 'Ltd.'], ['Vancouver', 'Calgary'], ['Granville Street', 'Stephen Avenue'], false), 1],
  [C('BR', 'Brazil', 'LATAM', '+55', 'USD', ['Ltda.', 'S.A.'], ['São Paulo', 'Rio de Janeiro', 'Curitiba'], ['Avenida Paulista', 'Rua Oscar Freire'], false), 3],
  [C('MX', 'Mexico', 'LATAM', '+52', 'USD', ['S.A. de C.V.'], ['Mexico City', 'Guadalajara', 'Monterrey'], ['Paseo de la Reforma', 'Avenida Juárez'], false), 2],
  [C('AR', 'Argentina', 'LATAM', '+54', 'USD', ['S.A.', 'S.R.L.'], ['Buenos Aires', 'Córdoba'], ['Avenida Corrientes', 'Calle Florida'], false), 1],
  [C('CL', 'Chile', 'LATAM', '+56', 'USD', ['SpA', 'S.A.'], ['Santiago', 'Valparaíso'], ['Avenida Providencia'], false), 1],
  [C('JP', 'Japan', 'APAC', '+81', 'JPY', ['K.K.', 'G.K.'], ['Tokyo', 'Osaka', 'Yokohama'], ['Chuo-dori', 'Omotesando'], false), 3],
  [C('SG', 'Singapore', 'APAC', '+65', 'USD', ['Pte. Ltd.'], ['Singapore'], ['Orchard Road', 'Raffles Place'], false), 2],
  [C('AU', 'Australia', 'APAC', '+61', 'AUD', ['Pty Ltd'], ['Sydney', 'Melbourne', 'Brisbane', 'Perth'], ['George Street', 'Collins Street'], false), 3],
  [C('IN', 'India', 'APAC', '+91', 'USD', ['Pvt. Ltd.', 'Ltd.'], ['Bengaluru', 'Mumbai', 'Pune', 'Hyderabad'], ['MG Road', 'Linking Road'], false), 2],
];

export const TERRITORIES = [
  { name: 'UK & Ireland', region: 'emea', countries: 'GB, IE', manager: 'oliver.hughes' },
  { name: 'DACH', region: 'emea', countries: 'DE, AT, CH', manager: 'katrin.weber' },
  { name: 'Nordics & Benelux', region: 'emea', countries: 'SE, FI, DK, NL, BE', manager: 'lars.lindqvist' },
  { name: 'Southern Europe', region: 'emea', countries: 'FR, ES, IT, PT', manager: 'camille.martin' },
  { name: 'CEE', region: 'emea', countries: 'CZ, SK, PL, HU', manager: 'jana.dvorakova' },
  { name: 'NA East', region: 'americas', countries: 'US (east), CA (east)', manager: 'michael.brooks' },
  { name: 'NA West', region: 'americas', countries: 'US (west), CA (west)', manager: 'sarah.chen' },
  { name: 'LATAM', region: 'americas', countries: 'BR, MX, AR, CL', manager: 'lucas.oliveira' },
  { name: 'APAC', region: 'apac', countries: 'JP, SG, AU, IN', manager: 'kenji.tanaka' },
];
const REPS_BY_TERRITORY = {
  'UK & Ireland': ['emily.carter', 'james.walsh'],
  DACH: ['felix.hoffmann', 'lena.schmidt'],
  'Nordics & Benelux': ['sofie.jansen', 'erik.nilsson'],
  'Southern Europe': ['lucia.romano', 'antoine.dubois'],
  CEE: ['eva.novakova', 'tomasz.kowalski'],
  'NA East': ['daniel.reed', 'olivia.bennett'],
  'NA West': ['ryan.patel', 'grace.kim'],
  LATAM: ['mariana.silva', 'diego.hernandez'],
  APAC: ['hiro.sato', 'priya.nair'],
};
const REP_DOMAIN = 'northwind.example';
const repEmail = (key) => `${key}@${REP_DOMAIN}`;
const repName = (key) => key.split('.').map((p) => p[0].toUpperCase() + p.slice(1)).join(' ');

/** The demo site users (User Authentication), and where they sit. */
export const DEMO_USERS = [
  { key: 'emea1', name: 'Eva Demo (Sales CEE)', group: 'CRM Sales EMEA', territory: 'CEE' },
  { key: 'emea2', name: 'Felix Demo (Sales DACH)', group: 'CRM Sales EMEA', territory: 'DACH' },
  { key: 'amer1', name: 'Daniel Demo (Sales NA East)', group: 'CRM Sales Americas', territory: 'NA East' },
  { key: 'apac1', name: 'Hiro Demo (Sales APAC)', group: 'CRM Sales APAC', territory: 'APAC' },
  { key: 'manager1', name: 'Morgan Demo (Sales manager)', group: 'CRM Sales Managers' },
  { key: 'support1', name: 'Sam Demo (Support)', group: 'CRM Support' },
  { key: 'partner1', name: 'Pat Demo (Partner)', group: 'CRM Partners' },
  { key: 'customer1', name: 'Chris Demo (Customer)', group: 'CRM Customers' },
  { key: 'customer2', name: 'Alex Demo (Customer)', group: 'CRM Customers' },
];
export const demoEmail = (key) => `test+crm-${key}@highgeek.eu`;
for (const u of DEMO_USERS.filter((x) => x.territory)) REPS_BY_TERRITORY[u.territory].push(`demo:${u.key}`);
const ownerEmail = (rep) => (rep.startsWith('demo:') ? demoEmail(rep.slice(5)) : repEmail(rep));

const FIRST = ['Anna', 'Jan', 'Petra', 'Martin', 'Lucie', 'Tomáš', 'Katarzyna', 'Piotr', 'Sophie', 'Lukas', 'Mia', 'Jonas', 'Emma', 'Noah', 'Olivia', 'Liam',
  'Charlotte', 'Oliver', 'Amelia', 'George', 'Isla', 'Harry', 'Chloé', 'Hugo', 'Léa', 'Louis', 'Giulia', 'Marco', 'Sofia', 'Alessandro', 'Lucía', 'Pablo',
  'Ingrid', 'Magnus', 'Freja', 'Mikko', 'Aino', 'Daan', 'Sanne', 'Ava', 'Ethan', 'Madison', 'Jacob', 'Abigail', 'Michael', 'Emily', 'William', 'Ella',
  'Mateo', 'Valentina', 'Santiago', 'Camila', 'Yuki', 'Haruto', 'Sakura', 'Ren', 'Wei', 'Mei', 'Arjun', 'Ananya', 'Rohan', 'Isabella', 'Zofia', 'Ondřej'];
const LAST = ['Novák', 'Svoboda', 'Dvořák', 'Černý', 'Procházka', 'Kowalski', 'Nowak', 'Wiśniewski', 'Müller', 'Schmidt', 'Schneider', 'Fischer', 'Weber',
  'Meyer', 'Wagner', 'Becker', 'Smith', 'Jones', 'Taylor', 'Brown', 'Williams', 'Wilson', 'Davies', 'Evans', 'Thomas', 'Martin', 'Bernard', 'Dubois',
  'Durand', 'Lefebvre', 'Moreau', 'Rossi', 'Russo', 'Ferrari', 'Esposito', 'Bianchi', 'García', 'Fernández', 'López', 'Martínez', 'Sánchez', 'Johansson',
  'Andersson', 'Nielsen', 'Hansen', 'Korhonen', 'Virtanen', 'de Jong', 'Jansen', 'de Vries', 'Peeters', 'Johnson', 'Miller', 'Davis', 'Rodriguez',
  'Anderson', 'Jackson', 'White', 'Harris', 'Clark', 'Lewis', 'Silva', 'Santos', 'Oliveira', 'Pereira', 'Hernández', 'González', 'Tanaka', 'Suzuki',
  'Takahashi', 'Watanabe', 'Tan', 'Lim', 'Wong', 'Sharma', 'Patel', 'Gupta', 'Iyer', 'O\'Brien', 'Murphy', 'Kelly', 'Horváth', 'Nagy', 'Kovács'];
const NAME_A = ['Northwind', 'Contoso', 'Fabrikam', 'Blue Harbor', 'Silverline', 'Ironclad', 'Brightpath', 'Red Oak', 'Summit', 'Vertex', 'Lumen', 'Atlas',
  'Nordic', 'Pioneer', 'Quantum', 'Cobalt', 'Evergreen', 'Granite', 'Horizon', 'Keystone', 'Meridian', 'Nimbus', 'Orion', 'Polaris', 'Riverbend',
  'Sapphire', 'Titan', 'Union', 'Vanguard', 'Willow', 'Zenith', 'Alder', 'Beacon', 'Cascade', 'Delta', 'Falcon', 'Harbor', 'Juniper', 'Kestrel',
  'Lighthouse', 'Maple', 'Northstar', 'Oakridge', 'Pinnacle', 'Redwood', 'Sterling', 'Trident', 'Waypoint', 'Aurora', 'Bluebird', 'Crescent', 'Ember'];
const NAME_B = ['Logistics', 'Analytics', 'Foods', 'Health', 'Energy', 'Systems', 'Labs', 'Manufacturing', 'Retail', 'Capital', 'Media', 'Robotics',
  'Pharma', 'Telecom', 'Software', 'Engineering', 'Consulting', 'Biotech', 'Textiles', 'Motors', 'Aerospace', 'Insurance', 'Hospitality',
  'Construction', 'Electronics', 'Water', 'Security', 'Learning', 'Freight', 'Solar', 'Mobility', 'Payments', 'Clinics', 'Hotels', 'Packaging', 'Data'];
const INDUSTRY_OF = { Logistics: 'logistics', Freight: 'logistics', Mobility: 'logistics', Packaging: 'manufacturing', Analytics: 'software', Software: 'software',
  Data: 'software', Labs: 'software', Systems: 'software', Foods: 'retail', Retail: 'retail', Health: 'healthcare', Pharma: 'healthcare', Biotech: 'healthcare',
  Clinics: 'healthcare', Energy: 'energy', Solar: 'energy', Water: 'energy', Manufacturing: 'manufacturing', Robotics: 'manufacturing', Textiles: 'manufacturing',
  Motors: 'manufacturing', Aerospace: 'manufacturing', Electronics: 'manufacturing', Construction: 'manufacturing', Engineering: 'manufacturing',
  Capital: 'financial_services', Insurance: 'financial_services', Payments: 'financial_services', Media: 'media', Telecom: 'telecommunications',
  Consulting: 'software', Security: 'software', Learning: 'education', Hospitality: 'hospitality', Hotels: 'hospitality' };

const PRODUCT_LINES = [
  // family, code, name, unit, price, cost, editions?
  ['subscription', 'CRM', 'Northwind Sales Cloud', 'user_month', 45, 9, true],
  ['subscription', 'SVC', 'Northwind Service Desk', 'user_month', 39, 8, true],
  ['subscription', 'ANL', 'Northwind Analytics', 'user_month', 29, 6, true],
  ['subscription', 'MKT', 'Northwind Marketing Hub', 'user_month', 55, 12, true],
  ['subscription', 'INT', 'Northwind Integration Platform', 'user_month', 120, 30, true],
  ['subscription', 'STO', 'Northwind Cloud Storage 1 TB', 'user_month', 18, 7, false],
  ['license', 'ONP', 'Northwind Server (on-premise)', 'license', 14000, 2500, true],
  ['license', 'MOB', 'Northwind Mobile Suite', 'license', 2400, 300, false],
  ['license', 'API', 'API Gateway Add-on', 'license', 6000, 900, false],
  ['hardware', 'TAB', 'Field Tablet 11"', 'device', 690, 455, false],
  ['hardware', 'SCN', 'Barcode Scanner Pro', 'device', 340, 210, false],
  ['hardware', 'KSK', 'Self-service Kiosk', 'device', 3900, 2600, false],
  ['hardware', 'GTW', 'Edge Gateway', 'device', 1250, 780, false],
  ['professional_services', 'IMP', 'Implementation Package', 'package', 18000, 9000, true],
  ['professional_services', 'MIG', 'Data Migration', 'day', 1150, 520, false],
  ['professional_services', 'CON', 'Solution Consulting', 'day', 1300, 600, false],
  ['professional_services', 'DEV', 'Custom Development', 'hour', 140, 70, false],
  ['support', 'SUP', 'Support Plan', 'user_year', 60, 18, true],
  ['support', 'TAM', 'Technical Account Manager', 'package', 24000, 13000, false],
  ['training', 'TRN', 'Administrator Training', 'day', 950, 300, false],
  ['training', 'ACA', 'Northwind Academy Seat', 'user_year', 390, 60, false],
  ['training', 'WSP', 'On-site Workshop', 'day', 2200, 800, false],
];
const EDITIONS = [['STD', 'Standard', 1], ['PRO', 'Professional', 1.6], ['ENT', 'Enterprise', 2.4]];

const COMPETITORS = [
  ['Salesforce', 'https://www.salesforce.com', 'high', 'Brand, ecosystem, breadth of apps.', 'Total cost, implementation time, admin overhead for mid-market.'],
  ['Microsoft Dynamics 365', 'https://dynamics.microsoft.com', 'high', 'Microsoft 365 bundling, Power Platform.', 'Licensing complexity, partner-dependent delivery.'],
  ['HubSpot', 'https://www.hubspot.com', 'high', 'Marketing automation, ease of use, freemium entry.', 'Enterprise controls, data residency, price at scale.'],
  ['Zoho CRM', 'https://www.zoho.com/crm', 'medium', 'Price, suite breadth.', 'Support quality, enterprise integrations.'],
  ['Pipedrive', 'https://www.pipedrive.com', 'medium', 'Simple pipeline UX for small sales teams.', 'Service, analytics and multi-entity needs.'],
  ['SAP Sales Cloud', 'https://www.sap.com', 'medium', 'SAP ERP customers.', 'Usability, speed of change.'],
  ['Oracle CX', 'https://www.oracle.com/cx', 'low', 'Large enterprise footprint.', 'Cost, agility.'],
  ['Freshworks', 'https://www.freshworks.com', 'medium', 'Service desk heritage, pricing.', 'Sales depth, customisation.'],
  ['monday sales CRM', 'https://monday.com/crm', 'low', 'Work-management users, visual boards.', 'Data model depth, reporting.'],
  ['Creatio', 'https://www.creatio.com', 'low', 'No-code process designer.', 'Ecosystem size.'],
  ['Sugar CRM', 'https://www.sugarcrm.com', 'low', 'On-premise option.', 'Roadmap pace, UI.'],
  ['In-house build', null, 'medium', 'Exact fit, no licence.', 'Maintenance burden, time to value.'],
];

const KB = [
  ['getting_started', 'Your first week with Northwind Sales Cloud', 'CRM'], ['getting_started', 'Inviting users and assigning licences', 'CRM'],
  ['getting_started', 'Importing accounts and contacts from a spreadsheet', 'CRM'], ['getting_started', 'Setting up your sales pipeline stages', 'CRM'],
  ['getting_started', 'Connecting your mailbox', 'CRM'], ['getting_started', 'Service Desk quick start', 'SVC'], ['getting_started', 'Analytics: building your first dashboard', 'ANL'],
  ['how_to', 'Creating a quote from an opportunity', 'CRM'], ['how_to', 'Using price books and discounts', 'CRM'], ['how_to', 'Automating lead assignment', 'CRM'],
  ['how_to', 'Building an approval flow for large discounts', 'CRM'], ['how_to', 'Tracking campaign ROI', 'MKT'], ['how_to', 'Designing an email nurture programme', 'MKT'],
  ['how_to', 'Routing support cases by product', 'SVC'], ['how_to', 'Setting SLA policies', 'SVC'], ['how_to', 'Publishing a customer self-service portal', 'SVC'],
  ['how_to', 'Scheduling reports by email', 'ANL'], ['how_to', 'Syncing invoices to your ERP', 'INT'], ['how_to', 'Calling the REST API with an API key', 'API'],
  ['how_to', 'Webhooks: reacting to record changes', 'INT'], ['how_to', 'Enrolling field tablets', 'TAB'], ['how_to', 'Pairing a barcode scanner', 'SCN'],
  ['how_to', 'Configuring the self-service kiosk', 'KSK'], ['how_to', 'Upgrading Northwind Server', 'ONP'], ['how_to', 'Offline mode in the mobile app', 'MOB'],
  ['troubleshooting', 'Emails are not syncing', 'CRM'], ['troubleshooting', 'Users cannot sign in with SSO', 'CRM'], ['troubleshooting', 'Reports show stale data', 'ANL'],
  ['troubleshooting', 'Integration jobs fail with 429 Too Many Requests', 'INT'], ['troubleshooting', 'Tablet does not receive updates', 'TAB'],
  ['troubleshooting', 'Scanner reads the wrong barcode format', 'SCN'], ['troubleshooting', 'Kiosk shows a blank screen', 'KSK'],
  ['troubleshooting', 'Server upgrade stops at database migration', 'ONP'], ['troubleshooting', 'Mobile app crashes on start', 'MOB'],
  ['troubleshooting', 'Storage quota exceeded', 'STO'], ['troubleshooting', 'API returns 401 after key rotation', 'API'],
  ['billing', 'Understanding your invoice', 'SUP'], ['billing', 'Changing the number of seats', 'CRM'], ['billing', 'Paying by bank transfer or card', 'SUP'],
  ['billing', 'VAT and reverse charge', 'SUP'], ['billing', 'Renewals and cancellation notice', 'SUP'],
  ['security', 'Two-factor authentication for all users', 'CRM'], ['security', 'Data residency and backups', 'STO'], ['security', 'Audit log and field history', 'CRM'],
  ['security', 'Role-based access and row-level sharing', 'CRM'], ['security', 'GDPR: exporting and erasing a contact', 'CRM'],
  ['release_notes', 'Release 2026.3: pipeline forecasting', 'CRM'], ['release_notes', 'Release 2026.2: service SLAs', 'SVC'], ['release_notes', 'Release 2026.1: analytics alerts', 'ANL'],
  ['release_notes', 'Release 2025.4: integration platform GA', 'INT'],
];

const CASE_SUBJECTS = {
  question: ['How do I export all contacts?', 'Can we change the fiscal year start?', 'Where do I see field history?', 'Question about user limits', 'How to set up territories?'],
  bug: ['Report total does not match list view', 'Date picker shows wrong week start', 'Duplicate emails logged on activity', 'Quote PDF cuts off the last line', 'Sorting by amount ignores currency'],
  outage: ['Application not loading for all users', 'API returning 503 errors', 'Sync stopped overnight', 'Kiosks offline in all stores', 'Login page times out'],
  billing: ['Invoice shows wrong VAT number', 'Charged for removed seats', 'Need a copy of last year\'s invoices', 'Payment failed but card is valid', 'Upgrade proration question'],
  feature_request: ['Please add Gantt view for tasks', 'Request: bulk edit of opportunities', 'Support for Slovak localisation', 'Custom fields on quote lines', 'Dark mode for the mobile app'],
  access: ['User locked out after password reset', 'SSO users get access denied', 'New hire cannot see accounts', 'Need to transfer ownership from leaver', 'API key permissions'],
};

// ---------------------------------------------------------------- generation

export function generate({ groupIdsByName }) {
  const out = {};
  const territoryGroup = { emea: 'CRM Sales EMEA', americas: 'CRM Sales Americas', apac: 'CRM Sales APAC' };

  out.territories = TERRITORIES.map((t) => ({
    name: t.name, region: t.region, countries: t.countries, group_id: groupIdsByName[territoryGroup[t.region]] ?? null,
    manager_email: repEmail(t.manager), description: `${t.name}: owned by ${territoryGroup[t.region]}. Managed by ${repName(t.manager)}.`,
  }));

  // ---- products, competitors, campaigns
  out.products = [];
  for (const [family, code, name, unit, price, cost, editions] of PRODUCT_LINES) {
    for (const [ed, edName, mult] of editions ? EDITIONS : [['', '', 1]]) {
      for (const term of family === 'subscription' ? ['M', 'Y'] : ['']) {
        const sku = ['NW', code, ed, term].filter(Boolean).join('-');
        const yearly = term === 'Y';
        out.products.push({
          sku, name: `${name}${edName ? ` ${edName}` : ''}${yearly ? ' (annual)' : term === 'M' ? ' (monthly)' : ''}`, family,
          description: `${name}${edName ? `, ${edName} edition` : ''}. ${family === 'subscription' ? 'Hosted in the EU, 99.9% availability SLA.' : family === 'hardware' ? 'Includes a two-year warranty.' : 'Delivered by certified Northwind staff.'}`,
          unit: yearly ? 'user_year' : unit, list_price: money(price * mult * (yearly ? 10 : 1)), cost: money(cost * mult * (yearly ? 12 : 1)),
          tax_rate: 21, is_active: !(code === 'MOB' || (code === 'STO' && term === 'M')), launched_on: date(TODAY - int(200, 2400) * DAY),
          specs: family === 'hardware' ? { warrantyYears: 2, weightKg: money(rand() * 4 + 0.3), connectivity: pick(['wifi', 'lte', 'ethernet']) }
            : family === 'subscription' ? { sla: '99.9%', region: 'eu-central', storageGb: int(1, 10) * 50 } : null,
        });
      }
    }
  }
  const sellable = out.products.filter((p) => p.is_active);

  out.competitors = COMPETITORS.map(([name, website, threat_level, strengths, weaknesses]) => ({
    name, website, threat_level, strengths, weaknesses, pricing_notes: `List price seen in 2026 deals: ${pick(['higher', 'similar', 'lower'])} than ours; discounts of ${int(10, 40)}% are common at quarter end.`,
  }));

  out.campaigns = [];
  for (let y = 2024; y <= 2026; y++) for (let q = 1; q <= 4; q++) {
    const start = Date.UTC(y, (q - 1) * 3, int(1, 20));
    for (const [type, title] of [['webinar', 'Webinar: Modern pipeline management'], ['trade_show', pick(['CeBIT-style Expo', 'Retail Tech Summit', 'SaaStock Europe', 'Web Summit'])],
      ['email', 'Nurture: product updates'], ['paid_ads', 'Search ads']]) {
      const end = start + int(1, type === 'email' || type === 'paid_ads' ? 80 : 3) * DAY;
      const budget = { webinar: 4000, trade_show: 45000, email: 2500, paid_ads: 18000 }[type] * (0.8 + rand() * 0.5);
      out.campaigns.push({
        name: `${y} Q${q} ${title}`, type, start_date: date(start), end_date: date(end),
        status: end < TODAY ? (chance(0.05) ? 'cancelled' : 'completed') : start <= TODAY ? 'active' : 'planned',
        budget: money(budget), actual_cost: end < TODAY ? money(budget * (0.7 + rand() * 0.4)) : start <= TODAY ? money(budget * rand() * 0.6) : 0,
        expected_revenue: money(budget * int(4, 12)), utm_campaign: slug(`${y}-q${q}-${type}`), landing_page: `https://www.northwind.example/go/${slug(`${y}-q${q}-${type}`)}`,
        description: `${title} for ${pick(['mid-market', 'enterprise', 'SMB', 'all'])} prospects in ${pick(['EMEA', 'the Americas', 'APAC', 'all regions'])}.`,
        leads_generated: 0,
      });
    }
  }

  // ---- accounts
  const names = new Set();
  const accounts = [];
  for (let i = 1; accounts.length < 3000; i++) {
    const country = weighted(COUNTRIES);
    const a = pick(NAME_A), b = pick(NAME_B);
    let name = `${a} ${b} ${pick(country.suffixes)}`;
    // Unique regardless of case: the import resolves lookups by name case-insensitively.
    if (names.has(name.toLowerCase())) name = `${a} ${b} ${country.cities[0]} ${pick(country.suffixes)}`;
    if (names.has(name.toLowerCase())) continue;
    names.add(name.toLowerCase());
    const n = accounts.length + 1;
    const employees = Math.round(Math.exp(rand() * Math.log(60000)) + 5);
    const segment = employees < 100 ? 'smb' : employees < 1000 ? 'mid_market' : employees < 10000 ? 'enterprise' : 'strategic';
    const type = weighted([['customer', 40], ['prospect', 45], ['partner', 5], ['reseller', 4], ['former_customer', 4], ['competitor', 2]]);
    const reps = REPS_BY_TERRITORY[country.territory];
    const domain = `${slug(`${a}-${b}`)}${n}.example`;
    accounts.push({
      _country: country, _rep: reps[n % reps.length], _domain: domain,
      name, account_number: `ACC-${pad(n)}`, type, industry: INDUSTRY_OF[b] ?? 'software', segment,
      rating: weighted([['hot', 12], ['warm', 40], ['cold', 33], ['Hot', 3], ['Warm', 4], ['COLD', 3], ['n/a', 3], ['tbd', 2]]),
      tags: [chance(0.08) && 'vip', chance(0.06) && 'reference', type === 'customer' && chance(0.1) && 'churn_risk', chance(0.12) && 'expansion', chance(0.04) && 'public_logo', chance(0.03) && 'beta_program'].filter(Boolean),
      website: `https://www.${domain}`, phone: `${country.phone} ${int(100, 999)} ${int(100, 999)} ${int(100, 999)}`, email: `info@${domain}`,
      employees, annual_revenue: money(employees * (60000 + rand() * 180000)),
      billing_street: `${pick(country.streets)} ${int(1, 220)}`, billing_city: pick(country.cities), billing_postal_code: String(int(10000, 99999)), billing_country: country.code,
      vat_id: country.vat ? `${country.code}${int(10000000, 99999999)}` : null,
      description: `${name} is a ${segment.replace('_', '-')} ${(INDUSTRY_OF[b] ?? 'software').replaceAll('_', ' ')} company headquartered in ${pick(country.cities)}, ${country.name}.`,
      owner_email: ownerEmail(reps[n % reps.length]),
      health_score: type === 'customer' ? int(25, 100) : null, customer_since: null, is_active: chance(0.97), credit_limit: money(Math.min(employees * 400, 2_000_000)),
      lifetime_value: 0, last_activity_at: null,
      erp_data: type === 'customer' ? { erpId: `C${pad(n, 7)}`, paymentTermsDays: pick([14, 30, 45, 60]), dunningLevel: weighted([[0, 90], [1, 7], [2, 3]]) } : null,
      territory: country.territory,
      // Subsidiaries point at a parent in an earlier import batch (rows 1–1000).
      parent_account: n > 2000 && chance(0.06) ? null : undefined,
    });
  }
  for (const acc of accounts.slice(2000)) if (acc.parent_account === null) {
    const parent = accounts[int(0, 999)];
    acc.parent_account = parent.name;
  }
  const accountByName = new Map(accounts.map((x) => [x.name, x]));

  // ---- account teams: the owner, sometimes a second rep, and the demo partner on partner-sourced accounts
  out.account_team = [];
  for (const acc of accounts) {
    out.account_team.push({ account: acc.name, user_email: acc.owner_email, role: 'owner', since: date(TODAY - int(30, 900) * DAY) });
    if (chance(0.3)) {
      const other = pick(REPS_BY_TERRITORY[acc._country.territory].filter((r) => ownerEmail(r) !== acc.owner_email));
      out.account_team.push({ account: acc.name, user_email: ownerEmail(other), role: pick(['account_executive', 'solutions_engineer', 'customer_success']), since: date(TODAY - int(10, 600) * DAY) });
    }
  }
  for (const acc of accounts.filter((x) => x.type === 'customer').slice(0, 25)) {
    out.account_team.push({ account: acc.name, user_email: demoEmail('partner1'), role: 'partner', since: date(TODAY - int(10, 300) * DAY) });
  }

  // ---- contacts: every account's primary contact first, so reports_to always points at an earlier import batch
  const emails = new Set();
  const person = (domain) => {
    const first = pick(FIRST), last = pick(LAST);
    let email = `${slug(first)}.${slug(last)}@${domain}`;
    for (let k = 2; emails.has(email); k++) email = `${slug(first)}.${slug(last)}${k}@${domain}`;
    emails.add(email);
    return { first, last, email };
  };
  const contactsOf = new Map();
  const primaries = [], others = [];
  const TITLES = { executive: ['CEO', 'COO', 'Managing Director', 'CFO'], it: ['CIO', 'IT Manager', 'Systems Administrator', 'Head of IT'], finance: ['Finance Manager', 'Controller', 'Accounts Payable'],
    operations: ['Operations Manager', 'Head of Operations'], sales: ['VP Sales', 'Sales Manager', 'Sales Operations Lead'], marketing: ['CMO', 'Marketing Manager', 'Demand Generation Lead'],
    procurement: ['Procurement Manager', 'Buyer'], hr: ['HR Director', 'People Partner'], engineering: ['CTO', 'Engineering Manager', 'Lead Engineer'], legal: ['General Counsel', 'Legal Counsel'] };
  for (const acc of accounts) {
    const count = { smb: int(1, 3), mid_market: int(2, 5), enterprise: int(3, 7), strategic: int(5, 9) }[acc.segment];
    const list = [];
    for (let k = 0; k < count; k++) {
      const p = person(acc._domain);
      const department = k === 0 ? pick(['executive', 'it', 'sales', 'operations']) : pick(Object.keys(TITLES));
      const c = {
        email: p.email, salutation: pick(['mr', 'ms', 'ms', 'mr', 'dr', 'mx']), first_name: p.first, last_name: p.last,
        job_title: pick(TITLES[department]), department, phone: `${acc._country.phone} ${int(100, 999)} ${int(100, 999)} ${int(100, 999)}`,
        mobile: chance(0.6) ? `${acc._country.phone} ${int(600, 799)} ${int(100, 999)} ${int(100, 999)}` : null, fax: chance(0.08) ? `${acc._country.phone} ${int(100, 999)} ${int(1000, 9999)}` : null,
        linkedin: chance(0.5) ? `https://www.linkedin.com/in/${slug(`${p.first}-${p.last}`)}-${int(100, 999)}` : null,
        birthday: chance(0.3) ? date(Date.UTC(int(1960, 2000), int(0, 11), int(1, 28))) : null,
        preferred_channel: weighted([['email', 60], ['phone', 20], ['linkedin', 10], ['sms', 5], ['none', 5]]),
        email_opt_in: chance(0.55), do_not_call: chance(0.07), is_primary: k === 0, lead_score: int(0, 100),
        notes: chance(0.2) ? pick(['Prefers short calls in the morning.', 'Champion for the rollout.', 'Sceptical about cloud; wants on-premise option.', 'Met at the trade show.', 'Signs off budgets above 50k.']) : null,
        mailing_address: chance(0.05) ? { street: `${pick(acc._country.streets)} ${int(1, 99)}`, city: pick(acc._country.cities), postalCode: String(int(10000, 99999)), country: acc._country.code } : null,
        account: acc.name,
      };
      (k === 0 ? primaries : others).push(c);
      list.push(c);
    }
    for (const c of list.slice(1)) if (chance(0.5)) c.reports_to = list[0].email;
    contactsOf.set(acc.name, list);
  }
  out.contacts = [...primaries, ...others];

  // ---- opportunities and line items
  out.opportunities = [];
  out.opportunity_line_items = [];
  const stageProb = { prospecting: 10, qualification: 20, needs_analysis: 35, proposal: 50, negotiation: 75, closed_won: 100, closed_lost: 0 };
  const forecastOf = { prospecting: 'pipeline', qualification: 'pipeline', needs_analysis: 'best_case', proposal: 'best_case', negotiation: 'commit', closed_won: 'closed', closed_lost: 'omitted' };
  const pool = accounts.filter((x) => x.type !== 'competitor');
  const pickForDeal = sampler(pool, (x) => (x.type === 'customer' ? 3 : x.type === 'prospect' ? 1.5 : 1));
  const pickForActivity = sampler(pool, (x) => (x.type === 'customer' ? 3 : 1));
  for (let n = 1; n <= 8000; n++) {
    const acc = pickForDeal();
    const past = chance(0.62);
    const close = past ? TODAY - int(1, 1000) * DAY : TODAY + int(1, 270) * DAY;
    const stage = past ? (chance(0.55) ? 'closed_won' : 'closed_lost') : weighted([['prospecting', 25], ['qualification', 25], ['needs_analysis', 18], ['proposal', 17], ['negotiation', 15]]);
    const type = acc.type === 'customer' ? weighted([['upsell', 35], ['cross_sell', 20], ['renewal', 35], ['new_business', 10]]) : 'new_business';
    const currency = acc._country.currency;
    const fx = FX[currency];
    const what = { new_business: pick(['Sales Cloud rollout', 'Service Desk', 'Analytics platform', 'Digital transformation', 'Field service']), upsell: pick(['More seats', 'Enterprise upgrade', 'Analytics add-on']),
      cross_sell: pick(['Marketing Hub', 'Integration Platform', 'Hardware refresh', 'Training programme']), renewal: `Renewal ${new Date(close).getUTCFullYear()}` }[type];
    const name = `${acc.name} – ${what} (OPP-${pad(n)})`;
    let amount = 0;
    const lines = int(1, acc.segment === 'smb' ? 2 : 4);
    for (let k = 0; k < lines; k++) {
      const p = pick(sellable);
      const quantity = ['user_month', 'user_year'].includes(p.unit) ? int(5, { smb: 40, mid_market: 250, enterprise: 1200, strategic: 4000 }[acc.segment])
        : p.unit === 'day' ? int(2, 30) : p.unit === 'hour' ? int(20, 300) : p.unit === 'device' ? int(5, 200) : int(1, 3);
      const months = p.unit === 'user_month' ? 12 : 1;
      const unit_price = money(p.list_price * months * fx);
      const discount_percent = weighted([[0, 40], [5, 20], [10, 20], [15, 12], [20, 6], [30, 2]]);
      const line_total = money(quantity * unit_price * (1 - discount_percent / 100));
      amount += line_total;
      out.opportunity_line_items.push({ opportunity: name, product: p.sku, description: `${p.name}${months === 12 ? ' – 12 months' : ''}`, quantity, unit_price, discount_percent, line_total,
        service_start: ['subscription', 'support'].includes(p.family) ? date(close + 14 * DAY) : null });
    }
    amount = money(amount);
    const contacts = contactsOf.get(acc.name);
    const closedAt = past ? time(close + int(9, 17) * 3600_000) : null;
    const opp = {
      _acc: acc, _close: close, _contacts: contacts,
      name, stage, amount, currency, probability: stageProb[stage], weighted_amount: money(amount * stageProb[stage] / 100), close_date: date(close),
      forecast_category: forecastOf[stage], type, lead_source: weighted([['website', 25], ['referral', 15], ['partner', 10], ['event', 15], ['webinar', 10], ['cold_call', 10], ['advertising', 10], ['form', 5]]),
      next_step: past ? null : pick(['Discovery call with IT', 'Send proposal', 'Security questionnaire', 'Pricing review with procurement', 'Executive sponsor meeting', 'Pilot kick-off', 'Legal review of MSA']),
      loss_reason: stage === 'closed_lost' ? weighted([['price', 30], ['competitor', 30], ['no_budget', 15], ['no_decision', 15], ['timing', 5], ['product_fit', 5]]) : null,
      description: `${acc.name} is looking at ${what.toLowerCase()}. Decision makers: ${contacts.slice(0, 2).map((c) => `${c.first_name} ${c.last_name} (${c.job_title})`).join(', ')}.`,
      discount_percent: weighted([[0, 50], [5, 20], [10, 20], [15, 10]]), owner_email: acc.owner_email,
      won_at: stage === 'closed_won' ? closedAt : null, closed_at: closedAt, account: acc.name, primary_contact: contacts[0].email,
      campaign: chance(0.3) ? pick(out.campaigns.filter((c) => Date.parse(c.start_date) < close))?.name ?? null : null,
    };
    out.opportunities.push(opp);
    if (stage === 'closed_won') {
      acc.lifetime_value = money(acc.lifetime_value + amount / fx);
      if (!acc.customer_since || acc.customer_since > opp.close_date) acc.customer_since = opp.close_date;
    }
  }

  // ---- quotes, contracts, invoices
  out.quotes = [];
  out.contracts = [];
  out.invoices = [];
  let q = 0;
  for (const opp of out.opportunities.filter((o) => ['proposal', 'negotiation', 'closed_won'].includes(o.stage) || (o.stage === 'closed_lost' && chance(0.6)))) {
    const taxRate = opp._acc._country.vat ? 0.21 : 0;
    const sentAt = Math.min(opp._close - int(5, 40) * DAY, TODAY - DAY);
    const quote = (status, at) => {
      const year = new Date(at).getUTCFullYear();
      const row = { quote_number: `Q-${year}-${pad(++q)}`, opportunity: opp.name, status, valid_until: date(at + 30 * DAY), subtotal: opp.amount,
        tax_amount: money(opp.amount * taxRate), total: money(opp.amount * (1 + taxRate)), payment_terms_days: pick([14, 30, 30, 45, 60]),
        terms: 'Prices exclude VAT unless stated. Subscriptions are billed in advance for the billing period. Northwind General Terms 2026 apply.',
        sent_at: status === 'draft' ? null : time(at), contact: opp.primary_contact };
      out.quotes.push(row);
      return row;
    };
    if (chance(0.2)) quote(pick(['expired', 'rejected']), sentAt - int(20, 60) * DAY);
    const final = quote({ proposal: chance(0.2) ? 'draft' : 'sent', negotiation: 'sent', closed_won: 'accepted', closed_lost: 'rejected' }[opp.stage], sentAt);
    if (opp.stage !== 'closed_won') continue;

    const start = opp._close + 14 * DAY;
    const term = weighted([[12, 60], [24, 25], [36, 15]]);
    const end = Date.UTC(new Date(start).getUTCFullYear(), new Date(start).getUTCMonth() + term, new Date(start).getUTCDate()) - DAY;
    const freq = weighted([['annually', 55], ['quarterly', 25], ['monthly', 10], ['one_time', 10]]);
    const value = money(opp.amount / FX[opp.currency]);
    const contract = { contract_number: `CT-${final.quote_number}`, account: opp.account, quote: final.quote_number,
      status: end < TODAY ? 'expired' : chance(0.03) ? 'terminated' : 'active',
      start_date: date(start), end_date: date(end), term_months: term, value, billing_frequency: freq, auto_renew: chance(0.75), renewal_notice_days: pick([30, 60, 90]),
      signed_by: `${opp._contacts[0].first_name} ${opp._contacts[0].last_name}` };
    out.contracts.push(contract);

    const perYear = { annually: 1, quarterly: 4, monthly: 12, one_time: 0 }[freq];
    const periods = perYear ? Math.round(term / 12 * perYear) : 1;
    const step = perYear ? 12 / perYear : 0;
    const issued = [];
    for (let k = 0; k < periods; k++) {
      const at = Date.UTC(new Date(start).getUTCFullYear(), new Date(start).getUTCMonth() + k * step, new Date(start).getUTCDate());
      if (at > TODAY) break;
      issued.push(at);
    }
    for (const [k, at] of issued.slice(-8).entries()) {
      const due = at + 30 * DAY;
      const amount = money(value / periods);
      const status = due < TODAY - 20 * DAY ? (chance(0.95) ? 'paid' : 'overdue') : due < TODAY ? (chance(0.6) ? 'paid' : 'overdue') : chance(0.3) ? 'paid' : 'issued';
      out.invoices.push({ invoice_number: `INV-${contract.contract_number}-${pad(issued.length - Math.min(issued.length, 8) + k + 1, 2)}`, account: opp.account, contract: contract.contract_number,
        status: contract.status === 'terminated' && status !== 'paid' ? 'void' : status, issue_date: date(at), due_date: date(due), amount,
        tax_amount: money(amount * (opp._acc._country.vat ? 0.21 : 0)), payment_method: weighted([['bank_transfer', 60], ['card', 20], ['direct_debit', 15], ['paypal', 5]]),
        paid_at: status === 'paid' ? time(Math.min(due - int(-10, 25) * DAY, TODAY - DAY)) : null });
    }
  }

  // ---- leads
  out.leads = [];
  const usedContacts = new Set();
  const customerAccounts = accounts.filter((x) => x.type === 'customer');
  for (let n = 1; n <= 6000; n++) {
    const status = weighted([['new', 15], ['contacted', 20], ['nurturing', 15], ['qualified', 10], ['disqualified', 25], ['converted', 15]]);
    let country = weighted(COUNTRIES), company, p, convertedAccount = null, convertedContact = null;
    if (status === 'converted') {
      const acc = pick(customerAccounts);
      const c = contactsOf.get(acc.name).find((x) => !usedContacts.has(x.email));
      if (c) { usedContacts.add(c.email); convertedAccount = acc.name; convertedContact = c.email; country = acc._country; company = acc.name; p = { first: c.first_name, last: c.last_name, email: c.email }; }
    }
    if (!p) {
      company = `${pick(NAME_A)} ${pick(NAME_B)} ${pick(country.suffixes)}`;
      const first = pick(FIRST), last = pick(LAST);
      p = { first, last, email: `${slug(first)}.${slug(last)}@${slug(company)}.example` };
    }
    const reps = REPS_BY_TERRITORY[country.territory];
    const source = weighted([['website', 30], ['form', 10], ['referral', 10], ['partner', 8], ['event', 15], ['webinar', 12], ['cold_call', 5], ['advertising', 7], ['social', 3]]);
    const campaign = ['event', 'webinar', 'advertising'].includes(source) || chance(0.2) ? pick(out.campaigns.filter((c) => Date.parse(c.start_date) <= TODAY)) : null;
    if (campaign) campaign.leads_generated++;
    const value = money(int(2, 400) * 500);
    out.leads.push({
      name: `${p.first} ${p.last}`, email: p.email, company_name: chance(0.95) ? company : null, job_title: pick(['Head of Sales', 'IT Manager', 'Operations Director', 'CEO', 'Marketing Manager', 'Procurement']),
      phone: chance(0.7) ? `${country.phone} ${int(100, 999)} ${int(100, 999)} ${int(100, 999)}` : null, country: country.code, source, status,
      rating: status === 'disqualified' ? 'cold' : value >= 50000 ? 'hot' : pick(['warm', 'cold', null]), estimated_value: value,
      owner_email: chance(0.92) ? ownerEmail(reps[n % reps.length]) : null,
      message: chance(0.6) ? pick(['We are evaluating CRM systems for our sales team of about 40 people.', 'Please send pricing for the Enterprise edition.', 'Interested in a demo of the service desk.',
        'We need field tablets for 120 technicians.', 'Looking to replace our spreadsheets.', 'Can you integrate with SAP?']) : null,
      utm: source === 'advertising' || source === 'website' ? { source: pick(['google', 'linkedin', 'bing', 'newsletter']), medium: pick(['cpc', 'email', 'organic']), campaign: campaign?.utm_campaign ?? null } : null,
      consent: chance(0.95), converted_at: status === 'converted' ? time(TODAY - int(5, 700) * DAY) : null,
      campaign: campaign?.name ?? null, converted_account: convertedAccount, converted_contact: convertedContact,
    });
  }

  // ---- activities
  out.activities = [];
  const SUBJECTS = { call: ['Discovery call', 'Follow-up call', 'Pricing call', 'Check-in call', 'Renewal call'], email: ['Sent case study', 'Sent proposal follow-up', 'Shared security documentation', 'Introduced solutions engineer'],
    meeting: ['On-site meeting', 'Quarterly business review', 'Executive briefing', 'Requirements workshop'], task: ['Prepare proposal', 'Update forecast', 'Send contract for signature', 'Book demo environment'],
    demo: ['Product demo', 'Analytics demo', 'Mobile app demo'], note: ['Account plan update', 'Competitive intel', 'Org chart change'] };
  const oppsOf = new Map();
  for (const o of out.opportunities) (oppsOf.get(o.account) ?? oppsOf.set(o.account, []).get(o.account)).push(o);
  for (let n = 0; n < 40000; n++) {
    const acc = pickForActivity();
    const type = weighted([['call', 30], ['email', 25], ['meeting', 12], ['task', 18], ['demo', 5], ['note', 10]]);
    const due = TODAY + int(-700, 60) * DAY + int(8, 17) * 3600_000;
    const status = due < TODAY ? (chance(0.92) ? 'completed' : 'cancelled') : 'planned';
    const contacts = contactsOf.get(acc.name);
    const opp = chance(0.5) ? pick(oppsOf.get(acc.name) ?? [null]) : null;
    const duration = { call: int(5, 45), email: 5, meeting: pick([30, 60, 90]), task: int(15, 120), demo: pick([45, 60]), note: 5 }[type];
    if (status === 'completed' && ['call', 'meeting', 'demo'].includes(type) && (!acc.last_activity_at || acc.last_activity_at < time(due))) acc.last_activity_at = time(due + duration * 60_000);
    out.activities.push({
      subject: `${pick(SUBJECTS[type])} – ${acc.name}`, type, status, priority: weighted([['low', 20], ['normal', 60], ['high', 17], ['urgent', 3]]), due_at: time(due), duration_minutes: duration,
      outcome: status === 'completed' && type !== 'note' ? weighted([['positive', 35], ['neutral', 35], ['negative', 8], ['no_answer', 10], ['follow_up', 12]]) : null,
      description: chance(0.4) ? pick(['Customer asked for references in their industry.', 'Agreed on pilot scope.', 'Budget confirmed for next quarter.', 'Waiting on IT security review.', 'Champion is leaving the company.']) : null,
      assignee_email: acc.owner_email, completed_at: status === 'completed' ? time(due + duration * 60_000) : null,
      account: acc.name, contact: pick(contacts).email, opportunity: opp?.name ?? null,
    });
  }

  // ---- support: cases and comments
  out.cases = [];
  out.case_comments = [];
  const agents = ['nina.berg', 'tom.harper', 'aisha.khan', 'pavel.horak', 'laura.costa'].map((x) => `${x}@support.${REP_DOMAIN}`);
  const slaHours = { urgent: 4, high: 8, normal: 24, low: 72 };
  const supportProducts = out.products.filter((p) => ['subscription', 'license', 'hardware'].includes(p.family));
  for (let n = 1; n <= 6000; n++) {
    const acc = pick(customerAccounts);
    const contact = pick(contactsOf.get(acc.name));
    const category = weighted([['question', 30], ['bug', 25], ['outage', 5], ['billing', 12], ['feature_request', 13], ['access', 15]]);
    const priority = category === 'outage' ? 'urgent' : weighted([['low', 20], ['normal', 55], ['high', 20], ['urgent', 5]]);
    const opened = TODAY - Math.floor(Math.pow(rand(), 2) * 720) * DAY - int(0, 23) * 3600_000;
    const ageDays = (TODAY - opened) / DAY;
    const status = ageDays > 30 ? weighted([['closed', 80], ['resolved', 18], ['on_hold', 2]]) : ageDays > 7 ? weighted([['resolved', 45], ['closed', 25], ['pending', 15], ['open', 15]])
      : weighted([['new', 25], ['open', 40], ['pending', 20], ['resolved', 15]]);
    const resolved = ['resolved', 'closed'].includes(status) ? opened + int(1, slaHours[priority] * 2) * 3600_000 : null;
    const row = {
      case_number: `CS-${pad(n)}`, subject: pick(CASE_SUBJECTS[category]), description: `${contact.first_name} ${contact.last_name} reported: ${pick(CASE_SUBJECTS[category]).toLowerCase()}. ${pick(['Affects the whole team.', 'Started after the last update.', 'Happens intermittently.', 'Blocking month-end close.', 'Not urgent but annoying.'])}`,
      status, priority, channel: weighted([['email', 35], ['portal', 30], ['phone', 15], ['chat', 15], ['form', 5]]), category, reporter_email: contact.email,
      sla_due_at: time(opened + slaHours[priority] * 3600_000), first_response_at: status === 'new' ? null : time(opened + int(10, 240) * 60_000),
      resolved_at: resolved ? time(resolved) : null, escalated: priority === 'urgent' || chance(0.03), satisfaction: resolved && chance(0.7) ? weighted([[5, 45], [4, 30], [3, 12], [2, 8], [1, 5]]) : null,
      internal_notes: chance(0.2) ? pick(['Known issue, see engineering ticket.', 'Customer on legacy plan.', 'Offer a training session.', 'Check with billing before refund.']) : null,
      account: acc.name, contact: contact.email, product: chance(0.8) ? pick(supportProducts).sku : null,
    };
    out.cases.push(row);
    if (status === 'new') continue;
    const agent = pick(agents);
    const thread = int(1, 5);
    for (let k = 0; k < thread; k++) {
      const fromAgent = k % 2 === 0;
      out.case_comments.push({ case: row.case_number, author_email: fromAgent ? agent : contact.email,
        body: fromAgent ? pick(['Thanks for reaching out, we are looking into it.', 'Could you send a screenshot and the time it happened?', 'A fix has been deployed; can you confirm?', 'We reproduced the issue and escalated it to engineering.', 'Here is a workaround while we investigate.'])
          : pick(['Here is the screenshot.', 'Still happening this morning.', 'Confirmed, works now. Thank you!', 'Any update on this?', 'The workaround helps for now.']),
        is_public: !(fromAgent && chance(0.1)) });
    }
  }

  // ---- knowledge base
  out.knowledge_articles = KB.map(([category, title, code], i) => ({
    title, slug: slug(title), summary: `${title}: a step-by-step guide.`, category, status: i % 17 === 16 ? 'draft' : i % 23 === 22 ? 'archived' : 'published',
    body: `# ${title}\n\n${pick(['This guide walks you through', 'Learn how to complete', 'Follow these steps for'])} ${title.toLowerCase()}.\n\n1. Open **Settings**.\n2. ${pick(['Choose the workspace.', 'Select the users.', 'Pick the integration.'])}\n3. Save and verify.\n\n> Tip: administrators can automate this with a flow.`,
    helpful_votes: int(0, 400), published_on: date(TODAY - int(5, 900) * DAY), _code: code,
  }));

  // ---- targets: 2026 quarters, achieved = the owner's wins in the quarter (EUR)
  out.sales_targets = [];
  const quarters = [0, 1, 2, 3].map((k) => [Date.UTC(2026, k * 3, 1), Date.UTC(2026, k * 3 + 3, 1) - DAY]);
  const wonEur = (email, from, to) => out.opportunities.filter((o) => o.owner_email === email && o.stage === 'closed_won' && o._close >= from && o._close <= to)
    .reduce((s, o) => s + o.amount / FX[o.currency], 0);
  for (const [territory, reps] of Object.entries(REPS_BY_TERRITORY)) for (const rep of reps) for (const [k, [from, to]] of quarters.entries()) {
    const email = ownerEmail(rep);
    // A quota is the rep's average 2025 quarter plus 15% growth, so attainment lands around target.
    const quota = Math.max(50_000, Math.round(wonEur(email, Date.UTC(2025, 0, 1), Date.UTC(2025, 11, 31)) / 4 * 1.15 / 10_000) * 10_000);
    const achieved = wonEur(email, from, to);
    out.sales_targets.push({ name: `2026 Q${k + 1} – ${rep.startsWith('demo:') ? DEMO_USERS.find((u) => `demo:${u.key}` === rep).name : repName(rep)}`, owner_email: email,
      period_start: date(from), period_end: date(to), quota, achieved: money(achieved), territory });
  }

  // ---- KPI history: 90 days, daily
  out.kpi_snapshots = [];
  const base = { open_pipeline_count: 2900, won_count: 2500, open_cases: 380, urgent_cases: 12, new_leads: 820 };
  for (let d = 90; d >= 1; d--) for (const [metric, v] of Object.entries(base)) {
    const trend = metric === 'won_count' ? -d * 2.4 : metric === 'open_pipeline_count' ? -d * 1.1 : 0;
    out.kpi_snapshots.push({ metric, value: Math.max(0, Math.round(v + trend + (rand() - 0.5) * v * 0.08)), captured_at: time(TODAY - d * DAY + 6 * 3600_000) });
  }

  // ---- many-to-many links, by primary value
  const links = { members: [], stakeholders: [], competitors: [], articleProducts: [] };
  const optedIn = out.contacts.filter((c) => c.email_opt_in);
  for (const c of out.campaigns.filter((x) => x.type === 'email' || x.type === 'webinar')) for (let k = 0; k < 130; k++) links.members.push([c.name, pick(optedIn).email]);
  for (const o of out.opportunities.filter((x) => x.stage !== 'closed_lost').slice(0, 1400)) {
    for (const c of o._contacts.slice(0, int(1, 3))) links.stakeholders.push([o.name, c.email]);
  }
  for (const o of out.opportunities.filter(() => chance(0.2))) for (let k = int(1, 2); k > 0; k--) links.competitors.push([o.name, pick(out.competitors).name]);
  for (const a of out.knowledge_articles) for (const p of out.products.filter((x) => x.sku.startsWith(`NW-${a._code}`)).slice(0, 3)) links.articleProducts.push([a.title, p.sku]);

  // Strip the generator's own bookkeeping (_fields) before the rows leave.
  const clean = (rows) => rows.map((r) => Object.fromEntries(Object.entries(r).filter(([k, v]) => !k.startsWith('_') && v !== undefined)));
  out.accounts = accounts;
  const tables = Object.fromEntries(Object.entries(out).map(([k, v]) => [k, clean(v)]));
  return { tables, links: { members: dedupe(links.members), stakeholders: dedupe(links.stakeholders), competitors: dedupe(links.competitors), articleProducts: dedupe(links.articleProducts) } };
}

const dedupe = (pairs) => [...new Map(pairs.map((p) => [p.join('\u0000'), p])).values()];

/** Import order: every lookup's target is imported first. */
export const IMPORT_ORDER = ['territories', 'products', 'competitors', 'campaigns', 'accounts', 'account_team', 'contacts', 'leads', 'opportunities',
  'opportunity_line_items', 'quotes', 'contracts', 'invoices', 'activities', 'cases', 'case_comments', 'knowledge_articles', 'sales_targets', 'kpi_snapshots'];
