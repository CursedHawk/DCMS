import { defineConfig } from 'vite';
import { dcmsPluginAdmin } from '@dcms/plugin-ui/vite';

// The whole build of an installed plugin's admin UI: one ES module (dist/index.js) and its
// stylesheet (dist/index.css), with React, @dcms/ui and the rest taken from the console.
// The plugin's .csproj publishes dist/ as admin/ in the plugin's folder.
export default defineConfig(dcmsPluginAdmin({ entry: 'src/index.tsx' }));
