import { StrictMode } from 'react';
import { createRoot, hydrateRoot } from 'react-dom/client';
import { App } from './App';
import './index.css';

const root = document.getElementById('root')!;
const app = (
  <StrictMode>
    <App path={location.pathname} />
  </StrictMode>
);

// Built pages arrive prerendered; only `vite dev` serves the empty template.
if (root.hasChildNodes()) hydrateRoot(root, app);
else createRoot(root).render(app);
