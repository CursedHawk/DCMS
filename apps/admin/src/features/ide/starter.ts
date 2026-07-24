// Minimal Mode B starter seeded when an empty ReactApp site is first opened.
// package.json / pnpm-lock.yaml are intentionally omitted — the site-builder
// owns those (ReactAppBuilder rejects site-supplied toolchain files).

export const STARTER_FILES: Record<string, string> = {
  'index.html': `<!doctype html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>My site</title>
  </head>
  <body>
    <div id="root"></div>
    <script type="module" src="/src/main.tsx"></script>
  </body>
</html>
`,
  'vite.config.ts': `import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({ plugins: [react()] });
`,
  'tsconfig.json': `{
  "compilerOptions": {
    "target": "ES2022",
    "lib": ["ES2022", "DOM", "DOM.Iterable"],
    "module": "ESNext",
    "moduleResolution": "bundler",
    "jsx": "react-jsx",
    "strict": true,
    "noEmit": true,
    "skipLibCheck": true
  },
  "include": ["src"]
}
`,
  'src/main.tsx': `import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
`,
  'src/App.tsx': `export function App() {
  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', padding: '3rem', textAlign: 'center' }}>
      <h1>Hello from your React site 👋</h1>
      <p>Edit <code>src/App.tsx</code> and watch the preview update.</p>
    </main>
  );
}
`,
};
