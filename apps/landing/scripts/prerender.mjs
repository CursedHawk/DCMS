// Renders every route in src/site.ts to static HTML, so crawlers and link previews get the whole
// page without running JavaScript. Runs after both Vite builds; the client bundle hydrates it.
import { readdir, readFile, rm, writeFile } from 'node:fs/promises';

const dist = new URL('../dist/', import.meta.url);
const ssr = new URL('../dist-ssr/', import.meta.url);
const { render, routes, SITE_URL } = await import(new URL('entry-server.js', ssr).href);

const template = await readFile(new URL('index.html', dist), 'utf8');
// Preload the latin body face: it is on every page and otherwise found only after the CSS.
const font = (await readdir(new URL('assets/', dist))).find(
    (f) =>
        f.startsWith('instrument-sans-400-700-latin-') &&
        !f.startsWith('instrument-sans-400-700-latin-ext-'),
);
if (!font) throw new Error('prerender: Instrument Sans latin font not found in dist/assets');
const preload = `<link rel="preload" href="/assets/${font}" as="font" type="font/woff2" crossorigin />`;

for (const route of routes) {
    const { html, head } = render(route);
    // The page's SEO is the point of this app; a route that renders without these ships nothing.
    for (const [what, ok] of [
        ['one <h1>', (html.match(/<h1[\s>]/g) ?? []).length === 1],
        ['<title>', head.includes('<title>')],
        ['canonical', !route.indexed || head.includes('rel="canonical"')],
    ]) {
        if (!ok) throw new Error(`prerender: ${route.path} is missing ${what}`);
    }
    const page = template
        .replace('<!--app-head-->', `${head}\n    ${preload}`)
        .replace('<!--app-html-->', html);
    await writeFile(new URL(route.file, dist), page);
    console.log(`prerendered ${route.path} -> ${route.file}`);
}

const today = new Date().toISOString().slice(0, 10);
const urls = routes
    .filter((r) => r.indexed)
    .map(
        (r) =>
            `  <url><loc>${SITE_URL}${r.path === '/' ? '/' : r.path}</loc><lastmod>${today}</lastmod></url>`,
    );
await writeFile(
    new URL('sitemap.xml', dist),
    `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${urls.join('\n')}\n</urlset>\n`,
);
await rm(ssr, { recursive: true });
