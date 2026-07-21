import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// Constrained Vite config for a DCMS Mode B site. Same-origin `/api` calls are
// served by site-host in production; in dev, point VITE_API_BASE_URL at the API.
export default defineConfig({
  plugins: [react()],
});
