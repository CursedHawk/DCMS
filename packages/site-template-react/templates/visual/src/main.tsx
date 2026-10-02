import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { config } from './config';
import { installAnalytics } from './dcms';
import { DcmsApp, loadDocuments } from './dcms/runtime';

// Anonymous analytics, gated on consent: nothing is stored or sent until the visitor accepts.
installAnalytics({ apiBaseUrl: config.apiBaseUrl });

// Every page, the routes and the theme, as the builder saved them in dcms/.
const documents = loadDocuments(import.meta.glob('../dcms/**/*.json', { eager: true, import: 'default' }));

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <DcmsApp documents={documents} />
  </StrictMode>,
);
