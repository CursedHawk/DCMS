import { StrictMode } from 'react';
import { createRoot, hydrateRoot } from 'react-dom/client';
import { config } from './config';
import { CookieConsent, installAnalytics } from './dcms';
import { DcmsApp, createFetchDataClient, loadDocuments } from './dcms/runtime';

// Anonymous analytics, gated on consent: nothing is stored or sent until the visitor accepts.
installAnalytics({ apiBaseUrl: config.apiBaseUrl });

// Every page, the routes and the theme, as the builder saved them in dcms/ — and the developer
// components (src/components/<name>.tsx) whose contracts are in dcms/code/.
const documents = loadDocuments(
  import.meta.glob('../dcms/**/*.json', { eager: true, import: 'default' }),
  import.meta.glob('./components/*.tsx', { eager: true, import: 'default' }),
);

const app = (
  <StrictMode>
    <DcmsApp documents={documents} dataClient={createFetchDataClient(config.apiBaseUrl)} />
    {/* Without it nothing was ever recorded: the default mode waits for an answer no one was
        asked for. It renders nothing until there is something to consent to, so the
        prerendered markup still hydrates as-is. */}
    <CookieConsent />
  </StrictMode>
);
// A prerendered page (scripts/prerender.mjs) is hydrated; any other starts from empty.
const root = document.getElementById('root')!;
if (root.firstElementChild) hydrateRoot(root, app);
else createRoot(root).render(app);
