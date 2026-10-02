import { defaultKit } from '@dcms/gjs-blocks';
import { APP_JSON, THEME_JSON, pagePath, type App, type Page } from '@dcms/site-runtime';

/** A document as the builder writes it: two-space JSON with a trailing newline, for readable diffs. */
export function serializeDoc(doc: unknown): string {
  return `${JSON.stringify(doc, null, 2)}\n`;
}

/**
 * What a new Mode D site starts with: one route, one page, the default design kit.
 *
 * Only the documents the builder owns. The Vite application around them arrives with the
 * generated layer (P2.1); until then a Mode D draft can be edited but not yet published.
 */
export function starterFiles(siteName?: string): Record<string, string> {
  const app: App = {
    schemaVersion: 1,
    routes: [{ id: 'home', path: '/', page: 'home' }],
    navigation: { main: [{ label: 'Home', to: '/' }] },
  };
  const home: Page = {
    schemaVersion: 1,
    id: 'home',
    title: 'Home',
    root: {
      id: 'root',
      type: 'dcms.page',
      slots: {
        default: [
          {
            id: 'hero',
            type: 'dcms.section',
            props: { background: 'soft', spacing: 'lg', width: 'narrow' },
            slots: {
              default: [
                { id: 'title', type: 'dcms.heading', props: { text: siteName || 'Welcome', level: '1', align: 'center' } },
                {
                  id: 'intro',
                  type: 'dcms.text',
                  props: { text: 'Drag components from the left to build this page.', size: 'lg', align: 'center', tone: 'muted' },
                },
                {
                  id: 'actions',
                  type: 'dcms.stack',
                  props: { direction: 'horizontal', gap: 'sm', justify: 'center' },
                  slots: { default: [{ id: 'cta', type: 'dcms.button', props: { label: 'Get started' } }] },
                },
              ],
            },
          },
        ],
      },
    },
  };
  return {
    [APP_JSON]: serializeDoc(app),
    [THEME_JSON]: serializeDoc(defaultKit().theme),
    [pagePath('home')]: serializeDoc(home),
  };
}
