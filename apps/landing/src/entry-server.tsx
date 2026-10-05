/* eslint-disable react-refresh/only-export-components -- the SSR entry is run by scripts/prerender.mjs, never hot-reloaded */
import { renderToString } from 'react-dom/server';
import { App } from './App';
import { faq } from './faq';
import { CONTACT_EMAIL, LEGAL_UPDATED, routes, SITE_URL, type Route } from './site';

export { routes, SITE_URL };

const esc = (s: string) =>
  s.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

const organization = {
  '@type': 'Organization',
  '@id': `${SITE_URL}/#org`,
  name: 'HighGeek',
  url: SITE_URL,
  logo: `${SITE_URL}/apple-touch-icon.png`,
  email: CONTACT_EMAIL,
};

function structuredData(route: Route, url: string): object[] {
  if (route.path === '/') {
    return [
      organization,
      {
        '@type': 'WebSite',
        '@id': `${SITE_URL}/#site`,
        url: `${SITE_URL}/`,
        name: 'DCMS',
        publisher: { '@id': organization['@id'] },
      },
      {
        '@type': 'SoftwareApplication',
        name: 'DCMS',
        url: `${SITE_URL}/`,
        applicationCategory: 'WebApplication',
        operatingSystem: 'Web browser',
        description: route.description,
        publisher: { '@id': organization['@id'] },
      },
      {
        '@type': 'FAQPage',
        mainEntity: faq.map(({ q, a }) => ({
          '@type': 'Question',
          name: q,
          acceptedAnswer: { '@type': 'Answer', text: a },
        })),
      },
    ];
  }
  return [
    {
      '@type': 'WebPage',
      url,
      name: route.title,
      description: route.description,
      dateModified: LEGAL_UPDATED,
      isPartOf: { '@type': 'WebSite', '@id': `${SITE_URL}/#site` },
      publisher: organization,
    },
  ];
}

function head(route: Route): string {
  const url = SITE_URL + (route.path === '/' ? '/' : route.path);
  const tags = [
    `<title>${esc(route.title)}</title>`,
    `<meta name="description" content="${esc(route.description)}" />`,
  ];
  if (!route.indexed) return [...tags, '<meta name="robots" content="noindex" />'].join('\n    ');

  // `<` escaped so no string in the data can close the script element.
  const ld = JSON.stringify({
    '@context': 'https://schema.org',
    '@graph': structuredData(route, url),
  }).replace(/</g, '\\u003c');
  return [
    ...tags,
    `<link rel="canonical" href="${url}" />`,
    `<meta property="og:type" content="website" />`,
    `<meta property="og:site_name" content="DCMS" />`,
    `<meta property="og:locale" content="en_GB" />`,
    `<meta property="og:url" content="${url}" />`,
    `<meta property="og:title" content="${esc(route.title)}" />`,
    `<meta property="og:description" content="${esc(route.description)}" />`,
    `<meta property="og:image" content="${SITE_URL}/og.png" />`,
    `<meta property="og:image:width" content="1200" />`,
    `<meta property="og:image:height" content="630" />`,
    `<meta property="og:image:alt" content="DCMS — build, version and publish websites on your own domain" />`,
    `<meta name="twitter:card" content="summary_large_image" />`,
    `<script type="application/ld+json">${ld}</script>`,
  ].join('\n    ');
}

export function render(route: Route) {
  return { html: renderToString(<App path={route.path} />), head: head(route) };
}
