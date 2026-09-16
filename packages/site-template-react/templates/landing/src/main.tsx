import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { config } from './config';
import { installAnalytics } from './dcms';
import './styles.css';

// Anonymous analytics, gated on consent: nothing is stored or sent until the visitor accepts,
// and the banner only appears when this tenant records anything. Installed before render so the
// first client-side navigation is seen.
installAnalytics({ apiBaseUrl: config.apiBaseUrl });

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
