import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { config } from './config';
import { installAnalytics } from './dcms';

// Anonymous analytics, gated on consent: nothing is stored or sent until the
// visitor accepts (see src/dcms/analytics.ts), and the banner is only shown when
// this tenant actually records something. Installed before render so a
// client-side route change is seen from the very first navigation.
//
// Pass `mode: 'off'` only if you have established that your site does not need
// consent — it starts collecting immediately and shows no banner.
installAnalytics({ apiBaseUrl: config.apiBaseUrl });

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
