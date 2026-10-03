import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { config } from './config';
import { installAnalytics } from './dcms';
import { DcmsApp, createFetchDataClient, loadDocuments } from './dcms/runtime';

// Anonymous analytics, gated on consent: nothing is stored or sent until the visitor accepts.
installAnalytics({ apiBaseUrl: config.apiBaseUrl });

// Every page, the routes and the theme, as the builder saved them in dcms/ — and the developer
// components (src/components/<name>.tsx) whose contracts are in dcms/code/.
const documents = loadDocuments(
  import.meta.glob('../dcms/**/*.json', { eager: true, import: 'default' }),
  import.meta.glob('./components/*.tsx', { eager: true, import: 'default' }),
);

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <DcmsApp documents={documents} dataClient={createFetchDataClient(config.apiBaseUrl)} />
  </StrictMode>,
);
