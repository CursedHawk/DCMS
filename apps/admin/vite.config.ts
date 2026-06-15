import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      // Dev: admin-api from compose (override) listens on 5002.
      '/api': {
        target: 'http://localhost:5002',
        changeOrigin: true,
      },
    },
  },
});
