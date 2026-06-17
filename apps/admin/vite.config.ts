import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    rollupOptions: {
      output: {
        // Split heavy, route-specific libraries so the initial bundle stays lean.
        manualChunks(id) {
          if (id.includes('@scalar')) return 'scalar';
          if (id.includes('recharts') || id.includes('/d3') || id.includes('victory')) return 'charts';
          if (id.includes('@rjsf') || id.includes('/ajv')) return 'rjsf';
          if (id.includes('@microsoft/signalr')) return 'signalr';
          if (id.includes('node_modules')) return 'vendor';
          return undefined;
        },
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      // Dev: admin-api from compose (override) listens on 5002.
      '/api': {
        target: 'http://localhost:5002',
        changeOrigin: true,
      },
      // Dev: the chat SignalR hub lives on content-api (5003); ws:true upgrades.
      '/hub': {
        target: 'http://localhost:5003',
        changeOrigin: true,
        ws: true,
      },
    },
  },
});
