import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';
import { dcmsRoutes } from './src/dcms/runtime/vite';

// A DCMS Mode D site (ADR 0020). `dcmsRoutes` writes each route's HTML with its own title and
// description, so crawlers and link previews see them without running the app.
export default defineConfig({
  plugins: [react(), dcmsRoutes()],
});
