import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// A DCMS Mode B site. Same-origin `/api` calls are served on the published domain; for local
// development against a remote tenant, set VITE_API_BASE_URL in .env.
export default defineConfig({
  plugins: [react()],
});
