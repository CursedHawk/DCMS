import { loadDocuments } from './dcms/runtime';
import { prerender } from './dcms/runtime/server';

// The build's second pass (`vite build --ssr`, see package.json): the same documents and
// developer components the browser entry loads, rendered once per route by scripts/prerender.mjs.
const documents = loadDocuments(
  import.meta.glob('../dcms/**/*.json', { eager: true, import: 'default' }),
  import.meta.glob('./components/*.tsx', { eager: true, import: 'default' }),
);

export const pages = () => prerender(documents);
